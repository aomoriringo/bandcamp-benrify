using System.Windows;
using System.Windows.Media;
using BandcampBenrify.Core;

namespace BandcampBenrify.Infrastructure;

/// <summary>
/// シークバーの下に敷く波形。再生済みの部分はアクセント色で塗る。
/// 波形がまだ無い (解析前・解析できない) ときは、普通のシークバーと同じ細い線を描く。
/// クリックやドラッグは上に重ねた透明な Slider が受け取る。
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const double BarWidth = 2;
    private const double BarGap = 1;

    public static readonly DependencyProperty PeaksProperty = Register(nameof(Peaks), typeof(byte[]), null);
    public static readonly DependencyProperty DurationProperty = Register(nameof(Duration), typeof(double), 0.0);
    public static readonly DependencyProperty PositionProperty = Register(nameof(Position), typeof(double), 0.0);
    public static readonly DependencyProperty PlayedBrushProperty = Register(nameof(PlayedBrush), typeof(Brush), Brushes.White);
    public static readonly DependencyProperty RestBrushProperty = Register(nameof(RestBrush), typeof(Brush), Brushes.Gray);

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(WaveformView), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    public byte[]? Peaks
    {
        get => (byte[]?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    /// <summary>曲の長さ (秒)。</summary>
    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>再生位置 (秒)。</summary>
    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public Brush PlayedBrush
    {
        get => (Brush)GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public Brush RestBrush
    {
        get => (Brush)GetValue(RestBrushProperty);
        set => SetValue(RestBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var duration = Duration > 0 ? Duration : 0;
        var played = duration > 0 ? Math.Clamp(Position / duration, 0, 1) : 0;
        var peaks = Peaks;
        var mid = height / 2;

        if (peaks is not { Length: > 0 } || duration <= 0)
        {
            // 普通のシークバーと同じ見た目
            dc.DrawRoundedRectangle(RestBrush, null, new Rect(0, mid - 2, width, 4), 2, 2);
            if (played > 0)
            {
                dc.DrawRoundedRectangle(PlayedBrush, null, new Rect(0, mid - 2, width * played, 4), 2, 2);
            }

            return;
        }

        // 一番大きい所が上下いっぱいになるようにそろえる
        var max = 1;
        foreach (var p in peaks)
        {
            max = Math.Max(max, p);
        }

        var step = BarWidth + BarGap;
        var bars = Math.Max(1, (int)((width + BarGap) / step));
        var perSecond = (double)TrackAnalysis.PeaksPerSecond;
        for (var i = 0; i < bars; i++)
        {
            // この棒が受け持つ時間の範囲の最大値
            var from = (int)(i * duration / bars * perSecond);
            var to = Math.Max(from + 1, (int)((i + 1) * duration / bars * perSecond));
            if (from >= peaks.Length)
            {
                break; // まだ届いていない (解析中)
            }

            var value = 0;
            for (var k = from; k < Math.Min(to, peaks.Length); k++)
            {
                value = Math.Max(value, peaks[k]);
            }

            var h = Math.Max(2, height * value / max);
            var x = i * step;
            var brush = (x + BarWidth / 2) / width <= played ? PlayedBrush : RestBrush;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, mid - h / 2, BarWidth, h), 1, 1);
        }
    }
}
