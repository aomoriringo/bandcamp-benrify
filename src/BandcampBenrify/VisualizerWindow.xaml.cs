using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BandcampBenrify.Core;
using BandcampBenrify.Infrastructure;
using BandcampBenrify.ViewModels;

namespace BandcampBenrify;

/// <summary>
/// 再生中の曲のジャケットを大きく出し、周りの輪を音域ごとの音量に合わせて動かすウィンドウ。
/// 音は再生のためにダウンロードしたものを解析した結果 (<see cref="MainViewModel.NowSpectrum"/>) を、再生位置に合わせて読む。
/// </summary>
public partial class VisualizerWindow : Window
{
    private const int RingColorCount = 6;

    /// <summary>舞台の幅の下限 (これより細いウィンドウでは幅で縮める)。</summary>
    private const double StageMinWidth = 1000;

    /// <summary>ジャケットの下端と曲名の上端の間 (舞台の座標)。</summary>
    private const double ArtGap = 4;

    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private float[] _bands = new float[32];
    private TimeSpan _lastRender;
    private int _lastSecond = -1;
    private string? _artUrl;
    private bool _fullScreen;
    private Rect _windowedBounds;
    private WindowState _windowedState = WindowState.Normal;
    private Point? _lastMouse;

    public VisualizerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        ApplySavedBounds(vm.Settings.VisualizerWindow);

        _hideTimer.Tick += (_, _) => HideControls();
        vm.PropertyChanged += OnVmChanged;
        Theme.Changed += OnThemeChanged;
        CompositionTarget.Rendering += OnRendering;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, _) => SaveBounds();

        // 操作ボタンも画面の大きさに合わせて大きくする (全画面の大きなモニターで小さくなりすぎないように)
        SizeChanged += (_, _) =>
        {
            var scale = Math.Clamp(ActualHeight / 760, 1, 2.5);
            Controls.LayoutTransform = new ScaleTransform(scale, scale);
            FitStage();
        };
        BottomPanel.SizeChanged += (_, _) => FitArt();
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            vm.PropertyChanged -= OnVmChanged;
            Theme.Changed -= OnThemeChanged;
            _hideTimer.Stop();
        };

        ApplyColors(null);
        ShowTrack();
        ShowAnalysis();
    }

    /// <summary>
    /// 舞台の幅をウィンドウの縦横比に合わせる (高さで拡大縮小して、横に余白を作らない)。
    /// ただし細長いウィンドウでは曲名が入るよう最低限の幅は取り、そのときは幅で縮める。
    /// </summary>
    private void FitStage()
    {
        if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
        {
            return;
        }

        Stage.Width = Math.Max(StageMinWidth, Stage.Height * Root.ActualWidth / Root.ActualHeight);
    }

    /// <summary>
    /// ジャケットは舞台のちょうど中央に置き、下の曲情報・波形にかからない一番大きな大きさにする。
    /// 周りの輪も同じ割合で拡大縮小する (輪が曲名に重なるのはかまわない)。
    /// </summary>
    private void FitArt()
    {
        var art = Stage.Height - 2 * (BottomPanel.ActualHeight + ArtGap);

        // 輪が舞台からはみ出さない大きさまで
        var scale = Math.Min(art / ArtFrame.Width, Math.Min(Stage.Height, Stage.Width) / ArtStage.Width);
        scale = Math.Max(0.3, scale);
        ArtScale.ScaleX = scale;
        ArtScale.ScaleY = scale;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Current):
            case nameof(MainViewModel.NowStats): // サポーター数・レーベルを取得し直したとき
                ShowTrack();
                break;
            case nameof(MainViewModel.NowPeaks):
            case nameof(MainViewModel.NowBpm):
            case nameof(MainViewModel.NowBpmText):
                ShowAnalysis();
                break;
        }
    }

    /// <summary>曲の情報とジャケット。何も再生していないときは、最後の曲を暗くして残す。</summary>
    private void ShowTrack()
    {
        if (_vm.Current is not { } item)
        {
            Stage.Opacity = 0.35;
            IdleText.Visibility = string.IsNullOrEmpty(TitleText.Text) ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        Stage.Opacity = 1;
        IdleText.Visibility = Visibility.Collapsed;
        TitleText.Text = item.Title;
        SubText.Text = $"{item.Artist} — {item.Album}";
        LabelText.Text = item.Details.Label ?? "";
        // レーベルが無いときも場所は空けておく (曲ごとにジャケットの大きさが変わらないように)
        LabelText.Visibility = string.IsNullOrEmpty(LabelText.Text) ? Visibility.Hidden : Visibility.Visible;

        // 未取得 (…) ならサポーター数は出さない
        SupportersText.Text = item.TotalText;
        SupportersStat.Visibility = item.TotalText is "…" or "" ? Visibility.Collapsed : Visibility.Visible;
        LoadArt(_vm.NowLargeArtUrl, _vm.NowArtUrl);
    }

    private void ShowAnalysis()
    {
        if (_vm.Current is null)
        {
            return;
        }

        Wave.Peaks = _vm.NowPeaks;
        BpmText.Text = _vm.NowBpm is { } bpm ? Math.Round(bpm).ToString("0") : _vm.NowBpmText.Contains('…') ? "…" : "—";
        BpmStat.Visibility = _vm.NowBpmText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>大きいジャケット (700px) を読み込み、色を取る。読めなければ普通の大きさ (350px) にする。</summary>
    private void LoadArt(string? url, string? fallbackUrl)
    {
        if (url == _artUrl)
        {
            return;
        }

        _artUrl = url;
        if (url is null)
        {
            ArtImage.Source = null;
            BackgroundArt.Source = null;
            ApplyColors(null);
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(url);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        ArtImage.Source = bitmap;
        BackgroundArt.Source = bitmap;
        bitmap.DownloadFailed += (_, _) =>
        {
            if (_artUrl == url && fallbackUrl is not null && fallbackUrl != url)
            {
                _artUrl = null;
                LoadArt(fallbackUrl, null);
            }
        };
        ArtColors.WhenLoaded(bitmap, color =>
        {
            if (_artUrl == url)
            {
                ApplyColors(color);
            }
        });
    }

    private Rgb? _artColor;

    /// <summary>輪と波形の色: ジャケットの色 (色味が無ければテーマカラー)。</summary>
    private void ApplyColors(Rgb? artColor)
    {
        _artColor = artColor;
        if (artColor is { } c)
        {
            Ring.SetColors(ArtPalette.Shades(c, RingColorCount).Select(ArtColors.ToColor).ToList());
            Wave.PlayedBrush = Theme.Frozen(ArtColors.ToColor(ArtPalette.Accent(c)));
        }
        else
        {
            var accent = (Application.Current.TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.White;
            Ring.SetColors([accent]);
            Wave.PlayedBrush = Theme.Frozen(accent);
        }
    }

    private void OnThemeChanged()
    {
        if (_artColor is null)
        {
            ApplyColors(null);
        }
    }

    /// <summary>毎フレーム: 再生位置の音域ごとの音量で輪を動かし、波形の再生位置を進める。</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastRender || WindowState == WindowState.Minimized)
        {
            return;
        }

        var dt = _lastRender == default ? 1 / 60.0 : Math.Min(0.1, (now - _lastRender).TotalSeconds);
        _lastRender = now;

        var seconds = _vm.PlaybackSeconds;
        var spectrum = _vm.NowSpectrum;
        var read = false;
        if (spectrum is not null && _vm.IsPlaying)
        {
            if (_bands.Length < spectrum.Bands)
            {
                _bands = new float[spectrum.Bands];
            }

            read = spectrum.TryRead(seconds, _bands.AsSpan(0, spectrum.Bands));
        }

        // 止めている / まだ解析が追いついていないときは輪を縮める
        Ring.Update(read ? _bands.AsSpan(0, spectrum!.Bands) : ReadOnlySpan<float>.Empty, dt);

        if (_vm.Current is not null)
        {
            // ドラッグ中は指している位置を見せる (離したときにシークする)
            if (_seekPreview is { } preview)
            {
                seconds = preview;
            }

            Wave.Duration = _vm.Duration;
            Wave.Position = seconds;
            var whole = (int)seconds;
            if (whole != _lastSecond)
            {
                _lastSecond = whole;
                PositionText.Text = Format.Duration(seconds) is { Length: > 0 } p ? p : "0:00";
                DurationText.Text = _vm.DurationText;
            }
        }
    }

    // ---- 波形でシーク (クリック / ドラッグ) ----

    /// <summary>ドラッグ中に指している位置 (秒)。</summary>
    private double? _seekPreview;

    private double? SecondsAt(MouseEventArgs e)
    {
        var duration = _vm.Duration;
        if (_vm.Current is null || duration <= 0 || Wave.ActualWidth <= 0)
        {
            return null;
        }

        return Math.Clamp(e.GetPosition(Wave).X / Wave.ActualWidth, 0, 1) * duration;
    }

    private void Wave_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 全画面切り替えのダブルクリックとして扱わない
        e.Handled = true;
        if (SecondsAt(e) is { } seconds)
        {
            _seekPreview = seconds;
            Wave.CaptureMouse();
        }
    }

    private void Wave_MouseMove(object sender, MouseEventArgs e)
    {
        if (_seekPreview is not null && Wave.IsMouseCaptured && SecondsAt(e) is { } seconds)
        {
            _seekPreview = seconds;
        }
    }

    private void Wave_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_seekPreview is not null && SecondsAt(e) is { } seconds)
        {
            _vm.SeekTo(seconds);
        }

        _seekPreview = null;
        Wave.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Wave_LostMouseCapture(object sender, MouseEventArgs e) => _seekPreview = null;

    // ---- 操作の表示 (マウスを動かしたときだけ) ----

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        // 中身が変わっただけで発生する MouseMove は無視する
        var p = e.GetPosition(this);
        if (_lastMouse is { } last && Math.Abs(last.X - p.X) < 2 && Math.Abs(last.Y - p.Y) < 2)
        {
            return;
        }

        _lastMouse = p;
        Controls.Opacity = 1;
        Controls.IsHitTestVisible = true;
        Root.Cursor = null;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideControls()
    {
        _hideTimer.Stop();
        if (Controls.IsMouseOver)
        {
            _hideTimer.Start();
            return;
        }

        Controls.Opacity = 0;
        Controls.IsHitTestVisible = false;
        if (_fullScreen)
        {
            Root.Cursor = Cursors.None;
        }
    }

    // ---- 全画面 ----

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SetFullScreen(!_fullScreen);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            SetFullScreen(!_fullScreen);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_fullScreen)
            {
                SetFullScreen(false);
            }
            else
            {
                Close();
            }

            e.Handled = true;
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(!_fullScreen);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen)
        {
            return;
        }

        if (on)
        {
            _windowedState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            _windowedBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;

            // 最大化中のままだと枠なしにしてもタスクバーが残るので、いったん戻してから最大化する
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _windowedState;
            Root.Cursor = null;
        }

        _fullScreen = on;
        FullScreenButton.Content = on ? "" : "";
        FullScreenButton.ToolTip = on ? "全画面をやめる (F11 / Esc / ダブルクリック)" : "全画面 (F11 / ダブルクリック)";
    }

    // ---- 位置と大きさ (前回のモニター・位置で開く) ----

    private void ApplySavedBounds(WindowBounds? saved)
    {
        // 2 台目のモニターも含めた画面全体のどこかに見えている位置なら使う
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        if (saved is not null && saved.Width >= MinWidth && saved.Height >= MinHeight
            && saved.Left < right - 100 && saved.Top < bottom - 100 && saved.Left + saved.Width > left + 100 && saved.Top >= top - 20)
        {
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
            if (saved.Maximized)
            {
                SetFullScreen(true);
            }

            return;
        }

        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width * 0.9);
        Height = Math.Min(Height, area.Height * 0.9);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void SaveBounds()
    {
        var r = _fullScreen ? _windowedBounds : WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.Width > 0 && r.Height > 0)
        {
            // Maximized は「全画面で開く」の意味で使う
            _vm.Settings.VisualizerWindow = new WindowBounds(r.Left, r.Top, r.Width, r.Height, _fullScreen, WindowBounds.CurrentVersion);
        }
    }
}
