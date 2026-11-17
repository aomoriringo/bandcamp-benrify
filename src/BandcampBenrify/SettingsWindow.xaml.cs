using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BandcampBenrify.Core;
using BandcampBenrify.ViewModels;

namespace BandcampBenrify;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        ProfileBox.Text = string.IsNullOrEmpty(vm.Settings.BandcampUsername) ? "" : $"https://bandcamp.com/{vm.Settings.BandcampUsername}";
        IntervalBox.Text = vm.Settings.RequestIntervalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        UpdateCacheText();
        Loaded += (_, _) =>
        {
            ProfileBox.Focus();
            ProfileBox.SelectAll();
        };
    }

    private void ProfileBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = ProfileBox.Text.Trim();
        var name = BandcampUrls.ParseFanUsername(text);
        ProfileHint.Text = text.Length == 0
            ? "未設定 (購入済みマークは表示されません)"
            : name is null ? "プロフィールの URL として解釈できません。例: https://bandcamp.com/ユーザー名" : $"ユーザー名: {name}";
        OkButton.IsEnabled = text.Length == 0 || name is not null;
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

        _vm.ApplySettings(BandcampUrls.ParseFanUsername(ProfileBox.Text) ?? "", interval);
        DialogResult = true;
    }
}
