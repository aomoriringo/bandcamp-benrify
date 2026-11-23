#if DEBUG
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BandcampBenrify.Infrastructure;

namespace BandcampBenrify;

/// <summary>
/// 開発用: ビジュアライザの「柔らかい輪郭」の値をその場で調整するパネル (T キーで表示 / 非表示)。
/// Debug ビルドだけに入り、正式版 (Release ビルド) には入らない。
/// 調整した値はデータフォルダの blob-tuning.json に保存し、次に開いたときも使う。
/// </summary>
public partial class VisualizerWindow
{
    private static readonly JsonSerializerOptions TuningJson = new() { WriteIndented = true };

    private readonly DispatcherTimer _tuningSaveTimer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    private Border? _tuningPanel;
    private StackPanel? _tuningRows;

    private static string TuningFile => Path.Combine(AppPaths.DataDir, "blob-tuning.json");

    partial void InitTuning()
    {
        try
        {
            if (File.Exists(TuningFile) && JsonSerializer.Deserialize<BlobTuning>(File.ReadAllText(TuningFile)) is { } saved)
            {
                Motion.Tuning = saved;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        _tuningSaveTimer.Tick += (_, _) =>
        {
            _tuningSaveTimer.Stop();
            try
            {
                File.WriteAllText(TuningFile, JsonSerializer.Serialize(Motion.Tuning, TuningJson));
            }
            catch (IOException)
            {
            }
        };

        BuildTuningPanel();
    }

    partial void OnTuningKey(KeyEventArgs e)
    {
        if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.None && _tuningPanel is { } panel)
        {
            panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            e.Handled = true;
        }
    }

    private void BuildTuningPanel()
    {
        _tuningRows = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var title = new TextBlock { Text = "調整 (開発用・T で閉じる)", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(MakeButton("値をコピー", (_, _) =>
        {
            Clipboard.SetText(DescribeTuning(Motion.Tuning));
            _vm.StatusText = "調整値をクリップボードにコピーしました";
        }));
        buttons.Children.Add(MakeButton("記録した値に戻す", (_, _) =>
        {
            Motion.Tuning = new BlobTuning();
            FillTuningRows();
            TuningChanged();
        }));
        header.Children.Add(buttons);
        header.Children.Add(title);

        var content = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);
        content.Children.Add(new ScrollViewer { Content = _tuningRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _tuningPanel = new Border
        {
            Width = 340,
            Margin = new Thickness(12),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x17, 0x19, 0x1C)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = content,
            Visibility = Visibility.Collapsed,
        };

        // パネルの上のクリックを、後ろ (ダブルクリックで全画面など) に渡さない
        _tuningPanel.MouseLeftButtonDown += (_, e) => e.Handled = true;
        Root.Children.Add(_tuningPanel);
        FillTuningRows();
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3), FontSize = 11 };
        b.Click += click;
        return b;
    }

    private void FillTuningRows()
    {
        var rows = _tuningRows!;
        rows.Children.Clear();
        var t = Motion.Tuning;

        void Section(string name) =>
            rows.Children.Add(new TextBlock { Text = name, FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 10, 0, 2) });

        void Row(string label, double min, double max, Func<double> get, Action<double> set)
        {
            var value = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 44 };
            var slider = new Slider { Minimum = min, Maximum = max, Value = get(), IsMoveToPointEnabled = true, Margin = new Thickness(0, 0, 0, 4) };
            value.Text = Num(get());
            slider.ValueChanged += (_, e) =>
            {
                set(e.NewValue);
                value.Text = Num(e.NewValue);
                TuningChanged();
            };
            var top = new DockPanel();
            DockPanel.SetDock(value, Dock.Right);
            top.Children.Add(value);
            top.Children.Add(new TextBlock { Text = label, FontSize = 12 });
            rows.Children.Add(top);
            rows.Children.Add(slider);
        }

        Section("音の拾い方");
        Row("しきい値 (これより小さい音は無視)", 0, 0.9, () => t.Threshold, v => t.Threshold = v);
        Row("強調 (大きい音だけ目立たせる)", 0.5, 3, () => t.Curve, v => t.Curve = v);
        Row("音量が下がる速さ /秒", 0.5, 8, () => t.LevelDecay, v => t.LevelDecay = v);
        Row("立ち上がりの基準にする平均の時間 秒", 0.05, 2, () => t.AverageSeconds, v => t.AverageSeconds = v);
        Row("弾けが消える速さ /秒", 1, 20, () => t.PunchDecay, v => t.PunchDecay = v);
        Row("重力の基準 BPM (これより速い曲ほど速く戻る)", 60, 200, () => t.GravityReferenceBpm, v => t.GravityReferenceBpm = v);
        Row("BPM による重力の効き方", 0, 3, () => t.GravityStrength, v => t.GravityStrength = v);
        Row("重力で鳴り続けの分を抑える強さ", 0, 5, () => t.GravitySustainDamping, v => t.GravitySustainDamping = v);
        Row("立ち上がりで鳴り続けの分を戻す基準 (小さいほど戻りやすい)", 0.01, 0.5, () => t.SustainHitReference, v => t.SustainHitReference = v);

        Section("形");
        Row("立ち上がりの波 (倍率)", 0, 3, () => t.PunchWave, v => t.PunchWave = v);
        Row("鳴り続けの波 (倍率)", 0, 1.5, () => t.SustainWave, v => t.SustainWave = v);
        string[] bandNames = ["低音", "中低音", "中音", "中高音", "高音"];
        for (var k = 0; k < t.BandWave.Length && k < bandNames.Length; k++)
        {
            var index = k;
            Row($"{bandNames[k]}の波の大きさ", 0, 60, () => t.BandWave[index], v => t.BandWave[index] = v);
        }

        Row("キックでふくらむ大きさ", 0, 120, () => t.KickSwell, v => t.KickSwell = v);
        Row("低音でふくらむ大きさ", 0, 60, () => t.BassSwell, v => t.BassSwell = v);
        Row("うねりの速さ", 0, 2, () => t.DriftSpeed, v => t.DriftSpeed = v);
        Row("静かなときの揺れ", 0, 15, () => t.IdleWobble, v => t.IdleWobble = v);
        Row("ジャケットからの余白", 0, 60, () => t.BaseGap, v => t.BaseGap = v);
        Row("層の間隔", 0, 40, () => t.LayerGap, v => t.LayerGap = v);

        Section("色");
        Row("濃さ (音が無いとき)", 0, 1, () => t.AlphaBase, v => t.AlphaBase = v);
        Row("鳴っている間に濃くなる分", 0, 1, () => t.AlphaLevel, v => t.AlphaLevel = v);
        Row("立ち上がりで濃くなる分", 0, 1, () => t.AlphaPunch, v => t.AlphaPunch = v);
        Row("色相の広がり 度", 0, 360, () => t.HueSpread, v => t.HueSpread = v);
        Row("色相がひと回りする秒数", 10, 600, () => t.HueCycleSeconds, v => t.HueCycleSeconds = v);
    }

    private static string Num(double v) => v.ToString(Math.Abs(v) >= 10 ? "0" : "0.00", CultureInfo.InvariantCulture);

    private void TuningChanged()
    {
        ApplyRingColors(); // 色相の広がりはすぐ反映する
        _tuningSaveTimer.Stop();
        _tuningSaveTimer.Start();
    }

    private static string DescribeTuning(BlobTuning t)
    {
        var sb = new StringBuilder();
        foreach (var p in typeof(BlobTuning).GetProperties())
        {
            var v = p.GetValue(t);
            var text = v is double[] arr
                ? "[" + string.Join(", ", arr.Select(Num)) + "]"
                : Num((double)v!);
            sb.AppendLine($"{p.Name} = {text}");
        }

        return sb.ToString();
    }
}
#endif
