using System.Net;
using System.Text;

namespace BandcampBenrify.Core;

public enum RequestPriority
{
    /// <summary>ユーザー操作の直後に必要なもの (再生直前のストリームURL更新など)。バックグラウンドより先に処理する。</summary>
    Interactive = 0,
    /// <summary>レーベル全作品の走査など、時間がかかってもよいもの。</summary>
    Background = 1,
}

public sealed record HttpResult(string Body, Uri FinalUri);

/// <summary>
/// Bandcamp への全リクエストを 1 本の直列キューに通し、最低間隔を空けて 1 件ずつ送るクライアント。
/// 同時接続は常に 1。429/503 を受けたら全体を停止して待ち、以後の間隔も広げる。
/// </summary>
public sealed class ThrottledHttpClient : IDisposable
{
    public static readonly TimeSpan MinimumAllowedInterval = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly object _lock = new();
    private readonly Queue<WorkItem> _interactive = new();
    private readonly Queue<WorkItem> _background = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Random _rng = new();
    private readonly Task _worker;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private TimeSpan _minInterval;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private long _totalRequests;

    public ThrottledHttpClient(TimeSpan minInterval, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        MinInterval = minInterval;
        _delay = delay ?? Task.Delay;
        handler ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"BandcampBenrify/{typeof(ThrottledHttpClient).Assembly.GetName().Version?.ToString(3) ?? "0"} (+https://github.com/aomoriringo/bandcamp-benrify)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/json;q=0.9,*/*;q=0.8");
        _worker = Task.Run(WorkerLoopAsync);
    }

    /// <summary>リクエスト開始間隔の下限。1 秒未満には設定できない。</summary>
    public TimeSpan MinInterval
    {
        get => _minInterval;
        set => _minInterval = value < MinimumAllowedInterval ? MinimumAllowedInterval : value;
    }

    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _interactive.Count + _background.Count;
            }
        }
    }

    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    /// <summary>キューの状態が変わったとき / 待機メッセージを出したいときに発火 (任意スレッド)。</summary>
    public event Action<string?>? StatusChanged;

    public Task<HttpResult> GetAsync(string url, RequestPriority priority, CancellationToken ct = default) =>
        EnqueueAsync(() => new HttpRequestMessage(HttpMethod.Get, url), priority, ct);

    public Task<HttpResult> PostJsonAsync(string url, string json, RequestPriority priority, CancellationToken ct = default) =>
        EnqueueAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") },
            priority,
            ct);

    private Task<HttpResult> EnqueueAsync(Func<HttpRequestMessage> factory, RequestPriority priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var item = new WorkItem(factory, ct, new TaskCompletionSource<HttpResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (ct.CanBeCanceled)
        {
            item.Registration = ct.Register(() => item.Completion.TrySetCanceled(ct));
        }

        lock (_lock)
        {
            (priority == RequestPriority.Interactive ? _interactive : _background).Enqueue(item);
        }

        _signal.Release();
        StatusChanged?.Invoke(null);
        return item.Completion.Task;
    }

    private async Task WorkerLoopAsync()
    {
        var stop = _shutdown.Token;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            WorkItem item;
            lock (_lock)
            {
                item = _interactive.Count > 0 ? _interactive.Dequeue() : _background.Dequeue();
            }

            try
            {
                if (item.Completion.Task.IsCompleted)
                {
                    continue; // キャンセル済み
                }

                var wait = _lastRequestAt + MinInterval + TimeSpan.FromMilliseconds(_rng.Next(0, 500)) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, stop).ConfigureAwait(false);
                }

                if (item.Completion.Task.IsCompleted)
                {
                    continue;
                }

                var result = await SendWithBackoffAsync(item, stop).ConfigureAwait(false);
                item.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled();
                break;
            }
            catch (OperationCanceledException) when (item.Token.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(item.Token);
            }
            catch (Exception ex)
            {
                item.Completion.TrySetException(ex);
            }
            finally
            {
                item.Registration.Dispose();
                StatusChanged?.Invoke(null);
            }
        }

        // 終了時に残っているものはすべてキャンセル
        lock (_lock)
        {
            foreach (var item in _interactive.Concat(_background))
            {
                item.Completion.TrySetCanceled();
            }

            _interactive.Clear();
            _background.Clear();
        }
    }

    private async Task<HttpResult> SendWithBackoffAsync(WorkItem item, CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, item.Token);
        for (var attempt = 0; ; attempt++)
        {
            using var request = item.Factory();
            _lastRequestAt = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _totalRequests);

            // 間隔は「前のリクエストの開始」から測る。常に 1 本ずつなので同時接続にはならない。
            using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                // 相手が嫌がっているので、今後の間隔を広げ (最大 15 秒)、全体を止めて待つ
                MinInterval = TimeSpan.FromSeconds(Math.Min(MinInterval.TotalSeconds * 1.5, 15));
                if (attempt >= 3)
                {
                    throw new HttpRequestException($"Bandcamp から制限応答が続いています ({(int)response.StatusCode})。しばらく時間をおいてください。");
                }

                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                    ?? TimeSpan.FromSeconds(30 * Math.Pow(2, attempt));
                retryAfter = TimeSpan.FromSeconds(Math.Clamp(retryAfter.TotalSeconds, 10, 600));
                StatusChanged?.Invoke($"Bandcamp から制限応答 ({(int)response.StatusCode})。{retryAfter.TotalSeconds:0} 秒待機します (以後の間隔: {MinInterval.TotalSeconds:0.0} 秒)");
                await _delay(retryAfter, stop).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {request.RequestUri}", null, response.StatusCode);
            }

            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            return new HttpResult(body, response.RequestMessage?.RequestUri ?? request.RequestUri!);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _http.Dispose();
        _shutdown.Dispose();
    }

    private sealed class WorkItem(Func<HttpRequestMessage> factory, CancellationToken token, TaskCompletionSource<HttpResult> completion)
    {
        public Func<HttpRequestMessage> Factory { get; } = factory;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<HttpResult> Completion { get; } = completion;
        public CancellationTokenRegistration Registration { get; set; }
    }
}
