using System.Net;
using System.Text.Json;
using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

public sealed class CollectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bcb-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>コレクション全体 (新しい順) を持つ偽の Bandcamp。ファンページは先頭 2 件、API は 2 件ずつ返す。</summary>
    private sealed class FakeBandcamp(List<(string Type, long Id)> collection) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            string body;
            if (request.Method == HttpMethod.Get)
            {
                Requests.Add("page");
                var blob = new
                {
                    fan_data = new { fan_id = 42 },
                    collection_data = new { item_count = collection.Count, last_token = "tok:2" },
                    item_cache = new { collection = collection.Take(2).ToDictionary(i => i.Type + i.Id, i => (object)new { tralbum_id = i.Id, tralbum_type = i.Type }) },
                };
                body = $"<html><div id=\"pagedata\" data-blob=\"{WebUtility.HtmlEncode(JsonSerializer.Serialize(blob))}\"></div></html>";
            }
            else
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var token = json.RootElement.GetProperty("older_than_token").GetString()!;
                Requests.Add(token);
                var start = int.Parse(token.Split(':')[1]);
                var batch = collection.Skip(start).Take(2).ToList();
                var end = start + batch.Count;
                body = JsonSerializer.Serialize(new
                {
                    items = batch.Select(i => new { tralbum_id = i.Id, tralbum_type = i.Type }),
                    more_available = end < collection.Count,
                    last_token = $"tok:{end}",
                });
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request };
        }
    }

    private (BandcampClient Client, FakeBandcamp Fake, ThrottledHttpClient Http) Create(List<(string, long)> collection, DiskCache? cache = null)
    {
        var fake = new FakeBandcamp(collection);
        var http = new ThrottledHttpClient(TimeSpan.FromSeconds(1), fake, (_, _) => Task.CompletedTask);
        return (new BandcampClient(http, cache ?? new DiskCache(_dir)), fake, http);
    }

    private static List<(string, long)> Items(int n) => Enumerable.Range(1, n).Select(i => (i % 3 == 0 ? "t" : "a", (long)(1000 - i))).ToList();

    [Fact]
    public async Task FirstLoadReadsEverything()
    {
        var (client, fake, http) = Create(Items(7));
        using (http)
        {
            var c = await client.GetCollectionAsync("https://bandcamp.com/someone");
            Assert.Equal(7, c.Count);
            Assert.True(c.Complete);
            Assert.Equal(["page", "tok:2", "tok:4", "tok:6"], fake.Requests);
        }
    }

    [Fact]
    public async Task RefreshOnlyReadsNewPurchases()
    {
        var items = Items(7);
        var cache = new DiskCache(_dir);
        var (client, _, http) = Create(items, cache);
        using (http)
        {
            await client.GetCollectionAsync("someone");
        }

        // 1 件だけ新しく買った → ファンページの 2 件に既知の作品が含まれるので、ページ 1 回で終わる
        items.Insert(0, ("a", 5000));
        var (client2, fake2, http2) = Create(items, cache);
        using (http2)
        {
            var c = await client2.GetCollectionAsync("someone");
            Assert.Equal(8, c.Count);
            Assert.Equal(["page"], fake2.Requests);
        }

        // 3 件新しく買った → 既知の作品に行き当たるまで読む
        items.InsertRange(0, [("a", 6001), ("t", 6002), ("a", 6003)]);
        var (client3, fake3, http3) = Create(items, cache);
        using (http3)
        {
            var c = await client3.GetCollectionAsync("someone");
            Assert.Equal(11, c.Count);
            Assert.True(c.Complete);
            Assert.Equal(["page", "tok:2"], fake3.Requests);
        }
    }

    [Fact]
    public async Task CancelledLoadResumesWhereItStopped()
    {
        var items = Items(9);
        var cache = new DiskCache(_dir);
        var (client, _, http) = Create(items, cache);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Loaded >= 4)
            {
                cts.Cancel();
            }
        });
        using (http)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCollectionAsync("someone", progress, cts.Token));
        }

        var partial = new BandcampClient(new ThrottledHttpClient(TimeSpan.FromSeconds(1)), cache).TryGetCachedCollection("someone");
        Assert.NotNull(partial);
        Assert.False(partial!.Complete);
        Assert.Equal(4, partial.Count);

        var (client2, fake2, http2) = Create(items, cache);
        using (http2)
        {
            var c = await client2.GetCollectionAsync("someone");
            Assert.Equal(9, c.Count);
            Assert.True(c.Complete);
            Assert.Equal(["page", "tok:4", "tok:6", "tok:8"], fake2.Requests);
        }
    }

    private sealed class SyncProgress(Action<CollectionProgress> action) : IProgress<CollectionProgress>
    {
        public void Report(CollectionProgress value) => action(value);
    }
}
