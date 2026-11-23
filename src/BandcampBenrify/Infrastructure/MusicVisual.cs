using System.Windows;
using System.Windows.Media;

namespace BandcampBenrify.Infrastructure;

/// <summary>
/// ジャケットの周り (ジャケットの後ろ) で、液体のようななめらかな形が音に合わせて揺れる。
/// 普段はゆっくりうねり、キックやスネアなど音が立ち上がった瞬間に大きく弾けてすぐ戻る (音との対応が見えるように)。
/// 音域を低音 → 高音の 5 つに分けた音量を <see cref="Update"/> で毎フレーム渡す。
/// 座標は 500x500 の枠の中心が原点、ジャケットの半径が <see cref="ArtRadius"/> の前提。
/// 大きさや速さなどの値は <see cref="Tuning"/>。
/// </summary>
public sealed class MusicVisual : FrameworkElement
{
    /// <summary>音域の分け方 (低音 → 高音)。</summary>
    public const int Bands = 5;

    /// <summary>音域ごとに受け持つ波の山の数 (低音はゆったり、高音は細かく)。</summary>
    private static readonly int[] Lobes = [2, 3, 4, 5, 7];

    private readonly double[] _levels = new double[Bands];
    private readonly double[] _average = new double[Bands];
    private readonly double[] _punch = new double[Bands];
    private readonly double[] _phases = [0, 1.3, 2.6, 3.9, 5.2];
    private Color[] _colors = [Colors.White];
    private double _time;

    /// <summary>ジャケット (丸) の半径。</summary>
    public double ArtRadius { get; set; } = 155;

    public BlobTuning Tuning { get; set; } = new();

    /// <summary>再生中の曲の推定 BPM (分からなければ null)。速い曲ほど「重力」を強くする。</summary>
    public double? Bpm { get; set; }

    /// <summary>
    /// BPM による重力の倍率: (BPM / 基準)^強さ。速い曲ほど大きく、ふくらみ・弾けが速く戻る。
    /// 極端にならないよう 0.6〜3 倍に収める。
    /// </summary>
    private double Gravity =>
        Bpm is > 0 and var bpm && Tuning.GravityReferenceBpm > 0
            ? Math.Clamp(Math.Pow(bpm / Tuning.GravityReferenceBpm, Tuning.GravityStrength), 0.6, 3)
            : 1;

    /// <summary>低音から高音への色の並び (並びの中から割合で選ぶ)。</summary>
    public void SetColors(IReadOnlyList<Color> colors)
    {
        _colors = colors.Count > 0 ? colors.ToArray() : [Colors.White];
        InvalidateVisual();
    }

    /// <summary>
    /// 音域ごとの音量 (0〜1、低い方から) を渡す。空なら音が無いものとして静めていく。
    /// 音量 (ゆっくり下がる) と、立ち上がり (直前の平均より急に大きくなった分。すぐ消える) を別々に追う。
    /// </summary>
    public void Update(ReadOnlySpan<float> bands, double dt, bool playing)
    {
        var t = Tuning;
        var gravity = Gravity;
        for (var k = 0; k < Bands; k++)
        {
            double target = 0;
            if (bands.Length >= Bands)
            {
                // この音域のうち一番大きいもの。静かな部分は小さく、大きい部分を目立たせる
                float v = 0;
                for (var i = k * bands.Length / Bands; i < (k + 1) * bands.Length / Bands; i++)
                {
                    v = Math.Max(v, bands[i]);
                }

                target = Math.Pow(Math.Clamp((v - t.Threshold) / Math.Max(0.01, 1 - t.Threshold), 0, 1), t.Curve);
            }

            _levels[k] = target >= _levels[k] ? target : Math.Max(target, _levels[k] - dt * t.LevelDecay * gravity);

            // 立ち上がり: 直前の平均より大きくなった分。出るときは一気に、消えるのは速く
            var rise = Math.Max(0, target - _average[k]);
            _punch[k] = rise >= _punch[k] ? rise : Math.Max(rise, _punch[k] - dt * t.PunchDecay * gravity);
            // (平均の時間は BPM で縮めない。縮めると、ベースが鳴り続けている上に乗るキックの立ち上がりが小さくなる)
            _average[k] += (target - _average[k]) * Math.Min(1, dt / Math.Max(0.01, t.AverageSeconds));
        }

        if (playing)
        {
            // 普段のうねりはゆっくり一定 (音の立ち上がりとの対比が分かるように)
            _time += dt;
            for (var k = 0; k < Bands; k++)
            {
                _phases[k] += dt * t.DriftSpeed * (k % 2 == 0 ? 1 : -1);
            }
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var t = Tuning;

        // 速い曲 (重力が強い) では、鳴り続けている分を抑える (ずっと鳴っているベースでふくらんだままにならないように)。
        // ただし音が立ち上がった瞬間 (キックなど) は元の大きさで出し、弾けが消えるのに合わせて速く抑えた大きさに戻る
        var damped = Math.Min(1, 1 / Math.Pow(Gravity, t.GravitySustainDamping));
        Span<double> sustain = stackalloc double[Bands];
        for (var k = 0; k < Bands; k++)
        {
            var hit = Math.Min(1, _punch[k] / Math.Max(0.01, t.SustainHitReference));
            sustain[k] = Math.Max(damped, hit);
        }

        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        // 外側の層から描く。層ごとに低音・中音・高音の色
        for (var layer = 2; layer >= 0; layer--)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                const int steps = 160;
                for (var i = 0; i <= steps; i++)
                {
                    var a = i * Math.PI * 2 / steps;

                    // 静かなときもかすかに揺れる
                    var wave = Math.Sin(a * 3 + _time * 0.5 + layer) * t.IdleWobble;

                    // 音域ごとの波: 鳴っている間は少し、音が立ち上がった瞬間に大きく出る
                    for (var k = 0; k < Bands; k++)
                    {
                        var size = (k < t.BandWave.Length ? t.BandWave[k] : 0) * (t.SustainWave * sustain[k] * _levels[k] + t.PunchWave * _punch[k]);
                        wave += size * Math.Sin(a * Lobes[k] + _phases[k] + layer * 0.9);
                    }

                    // 低音: 鳴っている間は少しふくらみ、キックの瞬間に大きく弾ける
                    var r = ArtRadius + t.BaseGap + layer * t.LayerGap + _levels[0] * t.BassSwell * sustain[0] + _punch[0] * t.KickSwell + wave;
                    var at = new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
                    if (i == 0)
                    {
                        ctx.BeginFigure(at, true, true);
                    }
                    else
                    {
                        ctx.LineTo(at, false, true);
                    }
                }
            }

            g.Freeze();

            // 層の濃さ: その層の音域が鳴っているほど濃く、立ち上がりで少し明るく
            var k0 = layer switch { 0 => 0, 1 => 2, _ => Bands - 1 };
            var color = _colors[Math.Clamp(layer * _colors.Length / 3, 0, _colors.Length - 1)];
            var alpha = Math.Clamp(t.AlphaBase + t.AlphaLevel * _levels[k0] + t.AlphaPunch * _punch[k0], 0, 0.95);
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B));
            brush.Freeze();
            dc.DrawGeometry(brush, null, g);
        }
    }
}
