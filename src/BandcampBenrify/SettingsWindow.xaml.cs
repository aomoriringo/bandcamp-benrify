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
        ColorHint.Text = color is null ? "#RRGGBB の形で入力してください (例: #3CC8E8)" : "";
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
        foreach (var (kind, text, button) in new[]
                 {
                     (CacheKind.Pages, PagesCacheText, ClearPagesCacheButton),
                     (CacheKind.Discographies, DiscographiesCacheText, ClearDiscographiesCacheButton),
                     (CacheKind.Collection, CollectionCacheText, ClearCollectionCacheButton),
                     (CacheKind.Analysis, AnalysisCacheText, ClearAnalysisCacheButton),
                 })
        {
            var bytes = _vm.CacheSize(kind);
            text.Text = FormatSize(bytes);
            button.IsEnabled = bytes > 0;
        }
    }

    private static string FormatSize(long bytes) =>
        bytes == 0 ? "なし"
        : bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB"
        : $"{bytes / 1024.0 / 1024.0:0.0} MB";

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<CacheKind>(tag, out var kind))
        {
            return;
        }

        var message = kind switch
        {
            CacheKind.Pages => "作品ページのキャッシュを削除します。\nサポーター数・値段などを表示するには、ページを取得し直す必要があります (1 ページ 1 回のアクセス)。数えた正確なサポーター数も消えます。\n削除しますか？",
            CacheKind.Discographies => "作品一覧のキャッシュを削除します。\nレーベル・アーティスト・ファンのページを次に開くとき、一覧を取得し直します (1 回のアクセス。ファンの一覧で読み込んだ続きも消えます)。\n削除しますか？",
            CacheKind.Analysis => "曲の解析結果 (波形・BPM) を削除します。\nBandcamp へのアクセスは増えません。次にその曲を再生したときに解析し直します。\n削除しますか？",
            _ => CollectionMessage(),
        };
        if (MessageBox.Show(this, message, "Bandcamp Benrify", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            _vm.ClearCache(kind);
            UpdateCacheText();
        }
    }

    private string CollectionMessage()
    {
        var count = _vm.CachedCollectionCount;
        var requests = (count + BandcampClient.CollectionBatchSize - 1) / BandcampClient.CollectionBatchSize;
        return "コレクションのキャッシュを削除します。\n購入済みマークが消えます。戻すには「コレクション更新」で全件を読み込み直します"
               + (count > 0 ? $" ({count} 件なので約 {requests} 回のアクセス)" : $" ({BandcampClient.CollectionBatchSize} 件ごとに 1 回のアクセス)")
               + "。\n削除しますか？";
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
