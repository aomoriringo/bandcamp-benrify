using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

/// <summary>波形・BPM の解析と、再生と解析で 1 回のダウンロードを共有するバッファ。</summary>
public sealed class AnalysisTests
{
    private const int Rate = 44100;

    /// <summary>bpm の 4 つ打ち (キック) + 裏拍のハイハット + 小さなノイズ。</summary>
    private static float[] Beat(double bpm, double seconds, int seed = 1)
    {
        var rng = new Random(seed);
        var samples = new float[(int)(Rate * seconds)];
        var beat = 60.0 / bpm;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / Rate;
            var sinceBeat = t % beat;
            var kick = Math.Exp(-sinceBeat * 30) * Math.Sin(2 * Math.PI * 60 * sinceBeat);
            var sinceOff = (t + beat / 2) % beat;
            var hat = Math.Exp(-sinceOff * 80) * (rng.NextDouble() * 2 - 1) * 0.3;
            samples[i] = (float)(0.6 * kick + hat + (rng.NextDouble() * 2 - 1) * 0.02);
        }

        return samples;
    }

    private static TrackAnalysis Analyze(float[] samples)
    {
        var analyzer = new AudioAnalyzer(Rate);
        // 実際と同じく少しずつ渡す
        for (var i = 0; i < samples.Length; i += 4096)
        {
            analyzer.Add(samples.AsSpan(i, Math.Min(4096, samples.Length - i)));
        }

        return analyzer.Finish();
    }

    // 100 以下で裏拍にハイハットがあると、半拍ごと (倍の BPM) にも音が並ぶので倍に出ることがある。
    // 速い曲 (200 超え) を半分にしないことを優先した (TrackAnalysis の Prior の説明を参照)
    [Theory]
    [InlineData(128)]
    [InlineData(140)]
    [InlineData(174)]
    [InlineData(200)]
    [InlineData(220)]
    public void EstimatesTheTempoOfASteadyBeat(double bpm)
    {
        var result = Analyze(Beat(bpm, 40));
        Assert.NotNull(result.Bpm);
        Assert.InRange(result.Bpm!.Value, bpm - 0.5, bpm + 0.5);
        Assert.True(result.BpmConfidence >= AudioAnalyzer.MinConfidence);
    }

    [Fact]
    public void NewAnalysesCarryTheCurrentVersion()
    {
        // 版を持たない古い結果 (0) は、次に再生したときに解析し直して置き換える
        Assert.Equal(TrackAnalysis.CurrentVersion, Analyze(Beat(128, 20)).Version);
        Assert.Equal(0, new TrackAnalysis().Version);
    }

    [Fact]
    public void NoiseHasNoTempo()
    {
        var rng = new Random(3);
        var noise = Enumerable.Range(0, Rate * 30).Select(_ => (float)((rng.NextDouble() * 2 - 1) * 0.3)).ToArray();
        Assert.Null(Analyze(noise).Bpm);
    }

    [Fact]
    public void SilenceAndShortClipsHaveNoTempo()
    {
        Assert.Null(Analyze(new float[Rate * 30]).Bpm);
        Assert.Null(Analyze(Beat(120, 5)).Bpm);
    }

    [Fact]
    public void PeaksFollowTheLoudness()
    {
        // 1 秒の無音 → 1 秒のフルスケールの正弦波 → 0.5 秒の半分の大きさ
        var samples = new float[(int)(Rate * 2.5)];
        for (var i = Rate; i < samples.Length; i++)
        {
            var amp = i < Rate * 2 ? 1.0 : 0.5;
            samples[i] = (float)(amp * Math.Sin(2 * Math.PI * 440 * i / Rate));
        }

        var peaks = Analyze(samples).Peaks;
        Assert.Equal(25, peaks.Length);
        Assert.All(peaks[..10], p => Assert.Equal(0, p));
        Assert.All(peaks[10..20], p => Assert.InRange(p, 250, 255));
        Assert.All(peaks[20..], p => Assert.InRange(p, 124, 130));
    }

    [Fact]
    public void SpectrumFollowsWhereTheSoundIs()
    {
        // 前半 2 秒は 80Hz (低音)、後半 2 秒は 5kHz (高音)
        var samples = new float[Rate * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            var hz = i < Rate * 2 ? 80 : 5000;
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / Rate));
        }

        var analyzer = new AudioAnalyzer(Rate);
        var spectrum = analyzer.Spectrum;
        var bands = new float[spectrum.Bands];
        Assert.False(spectrum.TryRead(1.0, bands)); // まだ解析していない

        analyzer.Add(samples);
        Assert.True(spectrum.AnalyzedSeconds > 3.5);

        Assert.True(spectrum.TryRead(1.0, bands));
        var low = Array.IndexOf(bands, bands.Max());
        Assert.True(spectrum.TryRead(3.0, bands));
        var high = Array.IndexOf(bands, bands.Max());
        Assert.True(low < spectrum.Bands / 3, $"低音の一番大きい音域 {low}");
        Assert.True(high > spectrum.Bands * 2 / 3, $"高音の一番大きい音域 {high}");
        Assert.All(bands, v => Assert.InRange(v, 0, 1));
    }

    [Fact]
    public void ShadesSpreadAroundTheAccentColor()
    {
        var shades = ArtPalette.Shades(new Rgb(200, 60, 40), 5);
        Assert.Equal(5, shades.Length);
        Assert.Equal(5, shades.Distinct().Count());
    }

    [Fact]
    public void ShadesCanDriftAroundTheColorWheel()
    {
        var c = new Rgb(200, 60, 40);
        var start = ArtPalette.Shades(c, 6);
        Assert.Equal(start, ArtPalette.Shades(c, 6, hueShift: 0));
        Assert.NotEqual(start, ArtPalette.Shades(c, 6, hueShift: 120));
        Assert.Equal(start, ArtPalette.Shades(c, 6, hueShift: 360)); // 一周すると元の色
        Assert.Equal(ArtPalette.Shades(c, 6, hueShift: 300), ArtPalette.Shades(c, 6, hueShift: -60));
    }

    [Fact]
    public void StoreKeepsBpmIndexAndPeaks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bcb-analysis-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new DiskCache(dir);
            var store = new TrackAnalysisStore(cache);
            store.Save(1, new TrackAnalysis { Peaks = [1, 2, 3], Bpm = 128, BpmConfidence = 0.5 });
            store.Save(2, new TrackAnalysis { Peaks = [4], Bpm = null });

            var reopened = new TrackAnalysisStore(cache);
            Assert.True(reopened.TryGetBpm(1, out var bpm1));
            Assert.Equal(128, bpm1);
            Assert.True(reopened.TryGetBpm(2, out var bpm2));
            Assert.Null(bpm2);
            Assert.False(reopened.TryGetBpm(3, out _));
            Assert.Equal([1, 2, 3], reopened.TryGet(1)!.Peaks);

            reopened.Clear();
            Assert.False(reopened.TryGetBpm(1, out _));
            Assert.Null(reopened.TryGet(1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ManualBpmWinsAndSurvivesReanalysisAndClearing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bcb-analysis-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new DiskCache(dir);
            var store = new TrackAnalysisStore(cache);
            store.Save(1, new TrackAnalysis { Bpm = 105 });
            store.SetManualBpm(1, 210);
            store.SetManualBpm(2, 174); // まだ解析していない曲でも持てる

            var reopened = new TrackAnalysisStore(cache);
            Assert.True(reopened.TryGetBpm(1, out var bpm));
            Assert.Equal(210, bpm);
            Assert.Equal(210, reopened.GetManualBpm(1));

            // 解析し直しても手動の値が優先
            reopened.Save(1, new TrackAnalysis { Bpm = 104, Version = TrackAnalysis.CurrentVersion });
            Assert.True(reopened.TryGetBpm(1, out bpm));
            Assert.Equal(210, bpm);

            // 解析のキャッシュを消しても手動の値は残る
            reopened.Clear();
            Assert.Null(reopened.TryGet(1));
            Assert.Equal(210, new TrackAnalysisStore(cache).GetManualBpm(1));

            // 自動の推定に戻す
            reopened.SetManualBpm(1, null);
            Assert.Null(new TrackAnalysisStore(cache).GetManualBpm(1));
            Assert.False(new TrackAnalysisStore(cache).TryGetBpm(1, out _));
            Assert.Equal(174, new TrackAnalysisStore(cache).GetManualBpm(2));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadersWaitForBytesThatHaveNotArrivedYet()
    {
        using var download = ProgressiveDownload.CreateForTest(6);
        using var a = download.OpenReader();
        using var b = download.OpenReader();
        Assert.Equal(6, a.Length);

        var buffer = new byte[6];
        var pending = a.ReadAsync(buffer, 0, 6);
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        download.Append([1, 2, 3]);
        Assert.Equal(3, await pending);

        // 別の読み手は自分の位置から読む。末尾近くへのシークは届くまで待つ
        b.Seek(4, SeekOrigin.Begin);
        var tail = b.ReadAsync(buffer, 0, 2);
        await Task.Delay(50);
        Assert.False(tail.IsCompleted);
        download.Append([4, 5, 6]);
        download.Complete();
        Assert.Equal(2, await tail);
        Assert.Equal([5, 6], buffer[..2]);
        Assert.Equal(0, await b.ReadAsync(buffer, 0, 2));

        Assert.Equal(3, a.Read(buffer, 0, 6));
        Assert.Equal([4, 5, 6], buffer[..3]);
    }

    [Fact]
    public async Task ReadersFailWhenTheDownloadBreaks()
    {
        using var download = ProgressiveDownload.CreateForTest(10);
        using var reader = download.OpenReader();
        download.Append([1, 2]);
        download.Fail(new HttpRequestException("切れた"));

        var buffer = new byte[10];
        Assert.Equal(2, await reader.ReadAsync(buffer, 0, 10));
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(buffer, 0, 10));
    }
}
