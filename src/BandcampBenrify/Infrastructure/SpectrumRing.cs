using System.Windows;
using System.Windows.Media;

namespace BandcampBenrify.Infrastructure;

/// <summary>
/// ジャケットの周りの輪。上から時計回りに低音 → 高音 (左半分は右半分の鏡映し) で、音域ごとの音量に合わせて棒が伸び縮みする。
/// 値は <see cref="Update"/> で毎フレーム渡す。
/// </summary>
public sealed class SpectrumRing : FrameworkElement
{
    /// <summary>片側の棒の数 (全体ではこの 2 倍)。</summary>
    private const int Half = 36;

    private readonly double[] _levels = new double[Half];
    private Pen[] _pens = [];

    /// <summary>棒の根元の半径 (ジャケットの外側)。</summary>
    public double InnerRadius { get; set; } = 174;

    /// <summary>棒の一番長いときの長さ。</summary>
    public double MaxLength { get; set; } = 60;

    public double BarThickness { get; set; } = 6;

    /// <summary>低音から高音への色の並び。</summary>
    public void SetColors(IReadOnlyList<Color> colors)
    {
        _pens = colors.Select(c =>
        {
            var pen = new Pen(new SolidColorBrush(c), BarThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            return pen;
        }).ToArray();
        InvalidateVisual();
    }

    /// <summary>
    /// 音域ごとの音量 (0〜1、低い方から) を渡す。null なら音が無いものとして縮める。
    /// 伸びるときはすぐ、縮むときはゆっくり (見た目のちらつきを抑える)。
    /// </summary>
    public void Update(ReadOnlySpan<float> bands, double dt)
    {
        var fall = dt * 2.2;
        for (var i = 0; i < Half; i++)
        {
            double target = 0;
            if (bands.Length > 1)
            {
                // 棒の位置を音域に割り当てる (間は補間)
                var pos = (double)i / (Half - 1) * (bands.Length - 1);
                var k = (int)pos;
                var t = pos - k;
                var v = k + 1 < bands.Length ? bands[k] * (1 - t) + bands[k + 1] * t : bands[k];

                // 静かな部分は短く、大きい部分を目立たせる
                target = Math.Pow(Math.Clamp((v - 0.42) / 0.58, 0, 1), 1.4);
            }

            _levels[i] = target >= _levels[i] ? target : Math.Max(target, _levels[i] - fall);
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_pens.Length == 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var total = Half * 2;
        for (var i = 0; i < total; i++)
        {
            // 右半分: 上 (0 度) から下へ低音 → 高音。左半分はその鏡映し
            var slot = i < Half ? i : total - 1 - i;
            var angle = (i + 0.5) / total * 2 * Math.PI - Math.PI / 2;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var len = 4 + MaxLength * _levels[slot];
            var pen = _pens[slot * _pens.Length / Half];
            dc.DrawLine(pen,
                new Point(center.X + cos * InnerRadius, center.Y + sin * InnerRadius),
                new Point(center.X + cos * (InnerRadius + len), center.Y + sin * (InnerRadius + len)));
        }
    }
}
