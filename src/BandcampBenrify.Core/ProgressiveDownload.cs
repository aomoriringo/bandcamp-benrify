namespace BandcampBenrify.Core;

/// <summary>
/// 1 回のダウンロードを、届いた分から複数の読み手 (再生と解析) で同時に読めるようにするバッファ。
/// 読み手はファイル全体の長さを知っている普通のシーク可能なストリームとして扱え、
/// まだ届いていない位置を読むと届くまで待つ。音声はメモリに置くだけでディスクには保存しない。
/// </summary>
public sealed class ProgressiveDownload : IDisposable
{
    private readonly byte[] _data;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private TaskCompletionSource _changed = NewSignal();
    private long _available;
    private bool _completed;
    private Exception? _error;

    private ProgressiveDownload(long length)
    {
        _data = new byte[length];
    }

    public long Length => _data.LongLength;

    public long Available
    {
        get
        {
            lock (_lock)
            {
                return _available;
            }
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_lock)
            {
                return _completed;
            }
        }
    }

    /// <summary>
    /// GET を送り、応答ヘッダーが届いた時点で返す (本体は裏で読み続ける)。
    /// 長さ (Content-Length) が分からないときは null。失敗した応答は <see cref="HttpRequestException"/>。
    /// </summary>
    public static async Task<ProgressiveDownload?> StartAsync(HttpClient http, string url, CancellationToken ct = default)
    {
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is not { } length || length is <= 0 or > int.MaxValue)
            {
                response.Dispose();
                return null;
            }

            var download = new ProgressiveDownload(length);
            _ = download.PumpAsync(response);
            return download;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>テスト用: 中身を外から流し込むバッファを作る。</summary>
    internal static ProgressiveDownload CreateForTest(long length) => new(length);

    private async Task PumpAsync(HttpResponseMessage response)
    {
        try
        {
            using (response)
            await using (var body = await response.Content.ReadAsStreamAsync(_cts.Token))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var n = await body.ReadAsync(buffer, _cts.Token);
                    if (n == 0)
                    {
                        break;
                    }

                    Append(buffer.AsSpan(0, n));
                }
            }

            Complete();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            var n = (int)Math.Min(bytes.Length, _data.LongLength - _available);
            bytes[..n].CopyTo(_data.AsSpan((int)_available));
            _available += n;
            signal = SwapSignal();
        }

        signal.TrySetResult();
    }

    internal void Complete()
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            _completed = true;
            if (_available < _data.LongLength)
            {
                _error ??= new IOException($"ダウンロードが途中で終わりました ({_available}/{_data.LongLength} バイト)");
            }

            signal = SwapSignal();
        }

        signal.TrySetResult();
    }

    internal void Fail(Exception error)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            _completed = true;
            _error ??= error;
            signal = SwapSignal();
        }

        signal.TrySetResult();
    }

    /// <summary>新しい読み手を作る。読み手ごとに読む位置は別。</summary>
    public Stream OpenReader() => new Reader(this);

    public void Dispose()
    {
        _cts.Cancel();
        Fail(new ObjectDisposedException(nameof(ProgressiveDownload)));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource SwapSignal()
    {
        var old = _changed;
        _changed = NewSignal();
        return old;
    }

    /// <summary>position の 1 バイト目が読めるようになるまで待ち、読めるバイト数を返す (末尾なら 0)。</summary>
    private async ValueTask<int> WaitForAsync(long position, int wanted, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (position >= _data.LongLength)
                {
                    return 0;
                }

                if (_available > position)
                {
                    return (int)Math.Min(wanted, _available - position);
                }

                if (_completed)
                {
                    throw _error as IOException ?? new IOException("音声のダウンロードに失敗しました", _error);
                }

                wait = _changed.Task;
            }

            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private int CopyTo(long position, Span<byte> destination, int count)
    {
        _data.AsSpan((int)position, count).CopyTo(destination);
        return count;
    }

    private sealed class Reader(ProgressiveDownload owner) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => owner.Length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, owner.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            var n = owner.WaitForAsync(_position, buffer.Length, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return Advance(owner.CopyTo(_position, buffer, n));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            var n = await owner.WaitForAsync(_position, buffer.Length, cancellationToken).ConfigureAwait(false);
            return Advance(owner.CopyTo(_position, buffer.Span, n));
        }

        private int Advance(int n)
        {
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => owner.Length + offset,
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
