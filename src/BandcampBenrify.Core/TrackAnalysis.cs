using System.Diagnostics;
using NLayer;

namespace BandcampBenrify.Core;

/// <summary>1 曲を解析した結果。波形 (音量の推移) と推定 BPM。</summary>
public sealed record TrackAnalysis
{
    /// <summary>波形は 1 秒あたりこの数の値で持つ。</summary>
    public const int PeaksPerSecond = 10;

    /// <summary>0.1 秒ごとの音量 (RMS。0〜255)。</summary>
    public byte[] Peaks { get; init; } = [];

    /// <summary>推定 BPM。テンポがはっきりせず当てにならないときは null。</summary>
    public double? Bpm { get; init; }

    /// <summary>BPM の確からしさ (0〜1 くらい)。</summary>
    public double BpmConfidence { get; init; }
}

/// <summary>
/// 音声 (モノラルの float サンプル) を少しずつ受け取り、波形と BPM を求める。
/// BPM は「音の立ち上がりの強さ」の推移 (スペクトルフラックス) の自己相関から、繰り返しの周期を推定する。
/// </summary>
public sealed class AudioAnalyzer
{
    /// <summary>これより確からしさが低い BPM は出さない。</summary>
    public const double MinConfidence = 0.15;

    private const int FrameSize = 2048;
    private const int Hop = 512;

    /// <summary>立ち上がりを見る音域の分け方 (対数で等間隔。低音のキックも高音のハイハットも同じ重みにする)。</summary>
    private const int BandCount = 24;
    private const double LowestHz = 40;
    private const double HighestHz = 11000;
    private const double MinBpm = 60;
    private const double MaxBpm = 200;

    private readonly int _samplesPerPeak;
    private readonly List<byte> _peaks = [];
    private double _sumSquares;
    private int _peakSamples;

    private readonly float[] _frame = new float[FrameSize];
    private int _frameFill;
    private readonly double[] _window = new double[FrameSize];
    private readonly double[] _re = new double[FrameSize];
    private readonly double[] _im = new double[FrameSize];
    private readonly double[] _prevLog;
    private readonly int[] _bandEdges;
    private readonly List<float> _flux = [];
    private bool _hasPrev;

    public AudioAnalyzer(int sampleRate)
    {
        SampleRate = sampleRate;
        _samplesPerPeak = Math.Max(1, sampleRate / TrackAnalysis.PeaksPerSecond);

        // 音域の境目 (FFT のビン番号)。狭すぎて同じビンになる低音域はまとめる
        var edges = new List<int>();
        var top = Math.Min(FrameSize / 2, (int)(HighestHz * FrameSize / sampleRate));
        for (var b = 0; b <= BandCount; b++)
        {
            var hz = LowestHz * Math.Pow(HighestHz / LowestHz, (double)b / BandCount);
            var bin = Math.Clamp((int)Math.Round(hz * FrameSize / sampleRate), 1, top);
            if (edges.Count == 0 || bin > edges[^1])
            {
                edges.Add(bin);
            }
        }

        _bandEdges = [.. edges];
        _prevLog = new double[_bandEdges.Length - 1];
        for (var i = 0; i < FrameSize; i++)
        {
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FrameSize);
        }

        // 各フレームは FrameSize 分のサンプルを見ているので、その真ん中の時刻のものとする
        Spectrum = new SpectrumTimeline(_prevLog.Length, FramesPerSecond, FrameSize / 2.0 / sampleRate);
    }

    public int SampleRate { get; }

    /// <summary>音域ごとの音量の推移 (ビジュアライザ用。解析しながら増えていく)。</summary>
    public SpectrumTimeline Spectrum { get; }

    /// <summary>立ち上がりの強さを 1 秒あたり何回求めるか。</summary>
    public double FramesPerSecond => (double)SampleRate / Hop;

    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            _sumSquares += s * s;
            if (++_peakSamples == _samplesPerPeak)
            {
                FlushPeak();
            }

            _frame[_frameFill++] = s;
            if (_frameFill == FrameSize)
            {
                ProcessFrame();
                Array.Copy(_frame, Hop, _frame, 0, FrameSize - Hop);
                _frameFill = FrameSize - Hop;
            }
        }
    }

    /// <summary>ここまでの波形のコピー。</summary>
    public byte[] PeaksSnapshot() => [.. _peaks];

    public TrackAnalysis Finish()
    {
        if (_peakSamples > 0)
        {
            FlushPeak();
        }

        var (bpm, confidence) = EstimateTempo(_flux, FramesPerSecond);
        return new TrackAnalysis
        {
            Peaks = [.. _peaks],
            Bpm = confidence >= MinConfidence ? bpm : null,
            BpmConfidence = Math.Round(confidence, 3),
        };
    }

    private void FlushPeak()
    {
        // 正弦波のフルスケール (RMS 0.707) で 255
        var rms = Math.Sqrt(_sumSquares / _peakSamples);
        _peaks.Add((byte)Math.Round(Math.Min(1, rms * Math.Sqrt(2)) * 255));
        _sumSquares = 0;
        _peakSamples = 0;
    }

    private void ProcessFrame()
    {
        for (var i = 0; i < FrameSize; i++)
        {
            _re[i] = _frame[i] * _window[i];
            _im[i] = 0;
        }

        Fft(_re, _im);
        double flux = 0;
        for (var b = 0; b < _prevLog.Length; b++)
        {
            double energy = 0;
            for (var k = _bandEdges[b]; k < _bandEdges[b + 1]; k++)
            {
                energy += _re[k] * _re[k] + _im[k] * _im[k];
            }

            var log = Math.Log(1 + 10 * Math.Sqrt(energy / (_bandEdges[b + 1] - _bandEdges[b])));
            if (_hasPrev && log > _prevLog[b])
            {
                flux += log - _prevLog[b];
            }

            _prevLog[b] = log;
        }

        _hasPrev = true;
        _flux.Add((float)flux);
        Spectrum.Append(_prevLog);
    }

    /// <summary>その場で計算する基数 2 の FFT。</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (var j = 0; j < len / 2; j++)
                {
                    var a = i + j;
                    var b = a + len / 2;
                    var tRe = re[b] * curRe - im[b] * curIm;
                    var tIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    (curRe, curIm) = (curRe * wRe - curIm * wIm, curRe * wIm + curIm * wRe);
                }
            }
        }
    }

    /// <summary>立ち上がりの強さの推移から BPM と確からしさを求める。短すぎる・無音などで求まらなければ (null, 0)。</summary>
    internal static (double? Bpm, double Confidence) EstimateTempo(IReadOnlyList<float> flux, double fps)
    {
        var n = flux.Count;
        if (n < fps * 10)
        {
            return (null, 0);
        }

        // ゆっくりした音量の変化を除く (約 1 秒の移動平均を引いて、正の部分だけ残す)
        var half = (int)(fps / 2);
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            prefix[i + 1] = prefix[i] + flux[i];
        }

        var e = new double[n];
        for (var i = 0; i < n; i++)
        {
            var lo = Math.Max(0, i - half);
            var hi = Math.Min(n, i + half + 1);
            e[i] = Math.Max(0, flux[i] - (prefix[hi] - prefix[lo]) / (hi - lo));
        }

        double energy = 0;
        foreach (var v in e)
        {
            energy += v * v;
        }

        if (energy <= 1e-9)
        {
            return (null, 0);
        }

        // 自己相関は必要なずれの分だけ計算して覚えておく
        var ac = new Dictionary<int, double>();
        double Ac(int lag)
        {
            if (lag >= n)
            {
                return 0;
            }

            if (!ac.TryGetValue(lag, out var v))
            {
                double sum = 0;
                for (var i = 0; i + lag < n; i++)
                {
                    sum += e[i] * e[i + lag];
                }

                v = ac[lag] = sum / energy * n / (n - lag);
            }

            return v;
        }

        double AcAt(double lag)
        {
            var l = (int)lag;
            var t = lag - l;
            return Ac(l) * (1 - t) + Ac(l + 1) * t;
        }

        // 周期 p の倍数 (1〜k 拍先) でどれだけ似ているか
        double Comb(double p, int k)
        {
            double sum = 0;
            for (var m = 1; m <= k; m++)
            {
                sum += AcAt(p * m);
            }

            return sum / k;
        }

        // 135 BPM を中心にした緩い好み (倍・半分の取り違えを減らす)
        // (中心と幅は、BPM が付いている手元の曲 160 曲での答え合わせで決めた)
        static double Prior(double bpm) => Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 135) / 0.8, 2));

        var minLag = 60 * fps / MaxBpm;
        var maxLag = 60 * fps / MinBpm;
        var scores = new List<double>();
        double bestScore = double.MinValue, bestLag = 0, bestComb = 0;
        for (var p = minLag; p <= maxLag; p += 0.05)
        {
            // 半拍 (8 分音符) にも音があることを少し加点する (拍の 1.5 倍・1.25 倍を拍と取り違えにくくする)
            var c = Comb(p, 4) + 0.25 * AcAt(p / 2);
            scores.Add(c);
            var s = c * Prior(60 * fps / p);
            if (s > bestScore)
            {
                (bestScore, bestLag, bestComb) = (s, p, c);
            }
        }

        // 遠くの拍まで見て周期を細かく合わせる
        var refined = bestLag;
        var refinedScore = double.MinValue;
        for (var p = bestLag - 0.3; p <= bestLag + 0.3; p += 0.005)
        {
            var c = Comb(p, 16);
            if (c > refinedScore)
            {
                (refinedScore, refined) = (c, p);
            }
        }

        scores.Sort();
        var median = scores[scores.Count / 2];
        var confidence = Math.Max(0, bestComb - median);
        return (Math.Round(60 * fps / refined, 1), confidence);
    }

    /// <summary>
    /// mp3 を読みながら解析する。読み込みが遅い (ダウンロード中) ときは届くのを待つ。
    /// onPeaks にはときどき、ここまでの波形を渡す。
    /// </summary>
    /// <param name="onSpectrum">解析を始めた時点で、音域ごとの音量の推移 (解析しながら増えていく) を渡す。</param>
    public static TrackAnalysis AnalyzeMp3(Stream mp3, Action<byte[]>? onPeaks = null, CancellationToken ct = default, Action<SpectrumTimeline>? onSpectrum = null)
    {
        using var mpeg = new MpegFile(mp3);
        var channels = Math.Max(1, mpeg.Channels);
        var analyzer = new AudioAnalyzer(mpeg.SampleRate);
        onSpectrum?.Invoke(analyzer.Spectrum);
        var buffer = new float[4096 * channels];
        var mono = new float[4096];
        var sinceReport = Stopwatch.StartNew();
        int read;
        while ((read = mpeg.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var frames = read / channels;
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    sum += buffer[i * channels + c];
                }

                mono[i] = sum / channels;
            }

            analyzer.Add(mono.AsSpan(0, frames));
            if (onPeaks is not null && sinceReport.ElapsedMilliseconds >= 300)
            {
                onPeaks(analyzer.PeaksSnapshot());
                sinceReport.Restart();
            }
        }

        return analyzer.Finish();
    }
}

/// <summary>
/// 音域ごとの音量 (対数) の時間変化。解析スレッドが書き足し、画面側が再生位置の値を読む。メモリに置くだけで保存はしない。
/// </summary>
public sealed class SpectrumTimeline(int bands, double framesPerSecond, double firstFrameSeconds)
{
    private readonly Lock _lock = new();
    private readonly float[] _max = new float[bands];
    private float[] _data = new float[bands * 4096];
    private int _frames;

    /// <summary>音域の数 (低い方から)。</summary>
    public int Bands { get; } = bands;

    public double FramesPerSecond { get; } = framesPerSecond;

    /// <summary>解析が済んでいる長さ (秒)。</summary>
    public double AnalyzedSeconds
    {
        get
        {
            lock (_lock)
            {
                return firstFrameSeconds + _frames / FramesPerSecond;
            }
        }
    }

    internal void Append(ReadOnlySpan<double> values)
    {
        lock (_lock)
        {
            if ((_frames + 1) * Bands > _data.Length)
            {
                Array.Resize(ref _data, _data.Length * 2);
            }

            for (var b = 0; b < Bands; b++)
            {
                var v = (float)values[b];
                _data[_frames * Bands + b] = v;
                _max[b] = Math.Max(_max[b], v);
            }

            _frames++;
        }
    }

    /// <summary>
    /// その時刻の音域ごとの音量を 0〜1 で書き込む (音域ごとに、ここまでで一番大きかった音量を 1 とする)。
    /// まだ解析していない時刻なら false。
    /// </summary>
    public bool TryRead(double seconds, Span<float> destination)
    {
        lock (_lock)
        {
            var pos = (seconds - firstFrameSeconds) * FramesPerSecond;
            if (_frames < 2 || pos >= _frames - 1)
            {
                return false;
            }

            pos = Math.Max(0, pos);
            var i = (int)pos;
            var t = (float)(pos - i);
            for (var b = 0; b < Bands && b < destination.Length; b++)
            {
                var v = _data[i * Bands + b] * (1 - t) + _data[(i + 1) * Bands + b] * t;
                destination[b] = _max[b] > 0 ? v / _max[b] : 0;
            }

            return true;
        }
    }
}

/// <summary>
/// 解析結果の保存先 (キャッシュの「analysis」)。波形は曲ごとのファイル、BPM は一覧で使うので 1 つの索引にまとめる。
/// </summary>
public sealed class TrackAnalysisStore(DiskCache cache, Func<DateTimeOffset>? clock = null)
{
    public const string Namespace = "analysis";
    private const string IndexKey = "bpm-index";

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private Dictionary<long, double?>? _index;

    private Dictionary<long, double?> Index => _index ??= cache.TryGet<Dictionary<long, double?>>(Namespace, IndexKey)?.Value ?? [];

    /// <summary>解析済みか。解析済みでテンポが分からなかった曲は true で bpm が null。</summary>
    public bool TryGetBpm(long trackId, out double? bpm) => Index.TryGetValue(trackId, out bpm);

    public TrackAnalysis? TryGet(long trackId) => cache.TryGet<TrackAnalysis>(Namespace, TrackKey(trackId))?.Value;

    public void Save(long trackId, TrackAnalysis analysis)
    {
        var now = _clock();
        cache.Set(Namespace, TrackKey(trackId), analysis, now);
        Index[trackId] = analysis.Bpm;
        cache.Set(Namespace, IndexKey, Index, now);
    }

    public void Clear()
    {
        cache.Clear(Namespace);
        _index = [];
    }

    private static string TrackKey(long trackId) => $"track-{trackId}";
}
