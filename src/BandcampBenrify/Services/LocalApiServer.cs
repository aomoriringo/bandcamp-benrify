using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BandcampBenrify.Services;

/// <summary>
/// Chrome 拡張からの「キューに追加」「ランキング表示」を受け付ける localhost 専用の小さな HTTP サーバー。
/// 一般の Web ページから勝手に叩かれないよう、カスタムヘッダー (X-Benrify) を必須にしている
/// (ブラウザはカスタムヘッダー付きのクロスオリジン要求にプリフライトを要求し、このサーバーは許可しない)。
/// </summary>
public sealed class LocalApiServer : IDisposable
{
    public const string RequiredHeader = "X-Benrify";

    private readonly HttpListener _listener = new();
    private readonly Func<string, Task> _enqueue;
    private readonly Func<string, Task> _browse;
    private readonly CancellationTokenSource _cts = new();

    public LocalApiServer(int port, Func<string, Task> enqueue, Func<string, Task> browse)
    {
        Port = port;
        _enqueue = enqueue;
        _browse = browse;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public int Port { get; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (req.HttpMethod == "GET" && path == "/ping")
            {
                await WriteAsync(res, 200, new { app = "bandcamp-benrify", ok = true });
                return;
            }

            if (req.HttpMethod != "POST" || (path != "/enqueue" && path != "/browse"))
            {
                await WriteAsync(res, 404, new { ok = false, error = "not found" });
                return;
            }

            var origin = req.Headers["Origin"];
            if (req.Headers[RequiredHeader] != "1" || (origin is not null && !origin.StartsWith("chrome-extension://", StringComparison.Ordinal)))
            {
                await WriteAsync(res, 403, new { ok = false, error = "forbidden" });
                return;
            }

            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            using var json = JsonDocument.Parse(body);
            var url = json.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                await WriteAsync(res, 400, new { ok = false, error = "url が不正です" });
                return;
            }

            await (path == "/enqueue" ? _enqueue(url) : _browse(url));
            await WriteAsync(res, 202, new { ok = true });
        }
        catch (Exception ex)
        {
            try
            {
                await WriteAsync(res, 500, new { ok = false, error = ex.Message });
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse res, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
