using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

public class ThrottleTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _inFlight;

        public ConcurrentQueue<(string Url, DateTimeOffset At)> Requests { get; } = new();

        public int MaxConcurrent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            Requests.Enqueue((request.RequestUri!.ToString(), DateTimeOffset.UtcNow));
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref _inFlight);
            var res = respond(request);
            res.RequestMessage = request;
            return res;
        }
    }

    private static HttpResponseMessage Ok(string body = "ok") => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task RequestsAreSerialAndSpaced()
    {
        var handler = new FakeHandler(_ => Ok());
        using var client = new ThrottledHttpClient(TimeSpan.FromSeconds(1), handler);

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(i => client.GetAsync($"https://x.test/{i}", RequestPriority.Background)));
        sw.Stop();

        Assert.Equal(1, handler.MaxConcurrent);
        var times = handler.Requests.Select(r => r.At).ToList();
        for (var i = 1; i < times.Count; i++)
        {
            Assert.True(times[i] - times[i - 1] >= TimeSpan.FromMilliseconds(950), $"gap {i}: {(times[i] - times[i - 1]).TotalMilliseconds}ms");
        }
    }

    [Fact]
    public async Task UserAgentIsAppNameAndVersionOnly()
    {
        string? userAgent = null;
        var handler = new FakeHandler(req =>
        {
            userAgent = req.Headers.UserAgent.ToString();
            return Ok();
        });
        using var client = new ThrottledHttpClient(TimeSpan.FromSeconds(1), handler);

        await client.GetAsync("https://x.test/", RequestPriority.Interactive);

        var version = typeof(ThrottledHttpClient).Assembly.GetName().Version!.ToString(3);
        Assert.Equal($"BandcampBenrify/{version}", userAgent);
    }

    [Fact]
    public void IntervalCannotGoBelowOneSecond()
    {
        using var client = new ThrottledHttpClient(TimeSpan.FromMilliseconds(10), new FakeHandler(_ => Ok()));
        Assert.Equal(TimeSpan.FromSeconds(1), client.MinInterval);
    }

    [Fact]
    public async Task InteractiveRequestsJumpTheQueue()
    {
        var handler = new FakeHandler(_ => Ok());
        using var client = new ThrottledHttpClient(TimeSpan.FromSeconds(1), handler);

        var first = client.GetAsync("https://x.test/bg0", RequestPriority.Background);
        var bg = Enumerable.Range(1, 3).Select(i => client.GetAsync($"https://x.test/bg{i}", RequestPriority.Background)).ToList();
        var fg = client.GetAsync("https://x.test/fg", RequestPriority.Interactive);
        await Task.WhenAll(bg.Append(first).Append(fg));

        var order = handler.Requests.Select(r => r.Url).ToList();
        Assert.True(order.IndexOf("https://x.test/fg") <= 1, string.Join(",", order));
    }

    [Fact]
    public async Task BacksOffOn429AndWidensInterval()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var handler = new FakeHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
                return r;
            }

            return Ok("done");
        });
        using var client = new ThrottledHttpClient(TimeSpan.FromSeconds(2), handler, (d, ct) =>
        {
            lock (delays)
            {
                delays.Add(d);
            }

            return Task.CompletedTask;
        });

        var result = await client.GetAsync("https://x.test/a", RequestPriority.Background);

        Assert.Equal("done", result.Body);
        Assert.Contains(TimeSpan.FromSeconds(42), delays);
        Assert.Equal(TimeSpan.FromSeconds(3), client.MinInterval);
    }

    [Fact]
    public async Task BotChallengePageStopsWithoutRetry()
    {
        var handler = new FakeHandler(_ => Ok("<!DOCTYPE html><html><head><link href=\"/_fs-ch-1T1wmsGaOgGaSxcX/assets/styles.css\" rel=\"stylesheet\" /></head></html>"));
        using var client = new ThrottledHttpClient(TimeSpan.FromSeconds(2), handler, (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<BandcampBlockedException>(() => client.GetAsync("https://bandcamp.com/search?q=x", RequestPriority.Interactive));

        Assert.Single(handler.Requests); // 再試行しない
        Assert.Equal(TimeSpan.FromSeconds(4), client.MinInterval); // 間隔を広げる
    }

    [Fact]
    public async Task CancelledRequestsAreNeverSent()
    {
        var handler = new FakeHandler(_ => Ok());
        using var client = new ThrottledHttpClient(TimeSpan.FromSeconds(1), handler);
        using var cts = new CancellationTokenSource();

        var first = client.GetAsync("https://x.test/first", RequestPriority.Background);
        var cancelled = Enumerable.Range(0, 5).Select(i => client.GetAsync($"https://x.test/c{i}", RequestPriority.Background, cts.Token)).ToList();
        cts.Cancel();
        await first;
        foreach (var t in cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
        }

        await client.GetAsync("https://x.test/last", RequestPriority.Background);
        Assert.Equal(["https://x.test/first", "https://x.test/last"], handler.Requests.Select(r => r.Url).ToList());
    }
}
