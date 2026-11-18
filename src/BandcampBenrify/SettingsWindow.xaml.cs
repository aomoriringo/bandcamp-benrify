using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BandcampBenrify.Core;
using BandcampBenrify.Infrastructure;
using BandcampBenrify.ViewModels;

namespace BandcampBenrify;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>開いたときのテーマ (キャンセルしたら戻す)。</summary>
    private readonly Color _originalAccent = Theme.Accent;
    private readonly bool _originalPlayerArtColor = Theme.PlayerUsesArtColor;
    private bool _saved;
    private bool _syncing;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        VersionText.Text = $"Bandcamp Benrify v{typeof(App).Assembly.GetName().Version?.ToString(3)}";
        ProfileBox.Text = string.IsNullOrEmpty(vm.Settings.BandcampUsername) ? "" : $"https://bandcamp.com/{vm.Settings.BandcampUsername}";
        IntervalBox.Text = vm.Settings.RequestIntervalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        UpdateCacheText();

        foreach (var (name, hex) in Theme.Presets)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Background = Theme.Frozen(Theme.Parse(hex)!.Value),
                ToolTip = $"{name} ({hex})",
                Tag = hex,
                GroupName = "ThemeColor",
            };
            AutomationPropertiesHelper.SetName(swatch, name);
            swatch.Checked += (_, _) =>
            {
                if (!_syncing)
                {
                    ColorBox.Text = hex;
                }
            };
            SwatchPanel.Children.Add(swatch);
        }

        _syncing = true;
        PlayerArtColorCheck.IsChecked = Theme.PlayerUsesArtColor;
        _syncing = false;
        ColorBox.Text = Theme.ToHex(Theme.Accent);

        Loaded += (_, _) =>
        {
            ProfileBox.Focus();
            ProfileBox.SelectAll();
        };
        Closed += (_, _) =>
        {
            if (!_saved)
            {
                Theme.Apply(_originalAccent, _originalPlayerArtColor);
            }
        };
    }

    private Color? SelectedColor => Theme.Parse(ColorBox.Text.Trim().ToUpperInvariant());

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var color = SelectedColor;
        ColorHint.Text = color is null ? "#RRGGBB の形で入力してください (例: #3CC8E8)" : "#RRGGBB の形で好きな色も使えます";
        OkButton.IsEnabled = color is not null && IsProfileValid;
        if (color is not { } c)
        {
            return;
        }

        // 同じ色の見本に印を付ける
        _syncing = true;
        foreach (RadioButton swatch in SwatchPanel.Children)
        {
            swatch.IsChecked = string.Equals((string)swatch.Tag, Theme.ToHex(c), StringComparison.OrdinalIgnoreCase);
        }

        _syncing = false;
        Preview();
    }

    private void PlayerArtColor_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            Preview();
        }
    }

    /// <summary>選んだ色をすぐ画面に反映する (保存はまだ)。</summary>
    private void Preview()
    {
        if (SelectedColor is { } c)
        {
            Theme.Apply(c, PlayerArtColorCheck.IsChecked == true);
        }
    }

    private bool IsProfileValid
    {
        get
        {
            var text = ProfileBox.Text.Trim();
            return text.Length == 0 || BandcampUrls.ParseFanUsername(text) is not null;
        }
    }

    private void ProfileBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = ProfileBox.Text.Trim();
        var name = BandcampUrls.ParseFanUsername(text);
        ProfileHint.Text = text.Length == 0
            ? "未設定 (購入済みマークは表示されません)"
            : name is null ? "プロフィールの URL として解釈できません。例: https://bandcamp.com/ユーザー名" : $"ユーザー名: {name}";
        OkButton.IsEnabled = IsProfileValid && (ColorBox is null || SelectedColor is not null);
    }

    private void UpdateCacheText()
    {
        CacheText.Text = $"{_vm.CacheSizeBytes / 1024.0 / 1024.0:0.0} MB。取得したページを保存して、同じページを取りに行かないようにしています。";
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "キャッシュを削除すると、サポーター数などを表示するために再度ページを取得する必要があります。削除しますか？",
                "Bandcamp Benrify", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            _vm.ClearCache();
            UpdateCacheText();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(IntervalBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var interval) || interval < 1 || interval > 60)
        {
            MessageBox.Show(this, "リクエスト間隔は 1〜60 秒で入力してください。", "Bandcamp Benrify", MessageBoxButton.OK, MessageBoxImage.Warning);
            IntervalBox.Focus();
            return;
        }

        if (SelectedColor is not { } color)
        {
            ColorBox.Focus();
            return;
        }

        Theme.Apply(color, PlayerArtColorCheck.IsChecked == true);
        _vm.Settings.ThemeColor = Theme.ToHex(color);
        _vm.Settings.PlayerUsesArtColor = PlayerArtColorCheck.IsChecked == true;
        _vm.ApplySettings(BandcampUrls.ParseFanUsername(ProfileBox.Text) ?? "", interval); // ここで設定ファイルにも保存される
        _saved = true;
        DialogResult = true;
    }
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
