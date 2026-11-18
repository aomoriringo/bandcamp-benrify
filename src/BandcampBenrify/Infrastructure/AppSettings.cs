using System.IO;
using System.Text.Json;
using BandcampBenrify.Core;

namespace BandcampBenrify.Infrastructure;

public static class AppPaths
{
    /// <summary>%APPDATA%\BandcampBenrify (設定・キュー)</summary>
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BandcampBenrify");

    /// <summary>%LOCALAPPDATA%\BandcampBenrify\cache</summary>
    public static string CacheDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BandcampBenrify", "cache");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string QueueFile => Path.Combine(DataDir, "queue.json");

    /// <summary>チェック済みの曲 (キューとは別に保存するので、キューから消しても残る)。</summary>
    public static string CheckedFile => Path.Combine(DataDir, "checked.json");
}

/// <param name="Version">初期サイズの決め方を変えたら上げる (古い版で保存された大きさは使わない)。</param>
public sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized, int Version = 0)
{
    public const int CurrentVersion = 2;
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Bandcamp へのリクエスト間隔 (秒)。1 秒未満は 1 秒に切り上げる。</summary>
    public double RequestIntervalSeconds { get; set; } = 2.5;

    /// <summary>アルバム / 曲ページのキャッシュ有効時間。</summary>
    public double PageCacheHours { get; set; } = 72;

    /// <summary>作品一覧のキャッシュ有効時間。</summary>
    public double DiscographyCacheHours { get; set; } = 12;

    /// <summary>
    /// 正確なサポーター数を数えるときの 1 作品あたりのリクエスト上限 (1 回 100 人)。
    /// 上限に達した作品は「N+」のまま途中経過を保存し、次回は続きから数える。
    /// </summary>
    public int SupportersMaxRequestsPerItem { get; set; } = 30;

    /// <summary>Chrome 拡張から操作を受け付けるポート (localhost のみ)。0 で無効。</summary>
    public int LocalApiPort { get; set; } = 47823;

    public double Volume { get; set; } = 0.7;

    public bool Shuffle { get; set; } = true;

    public bool RepeatOne { get; set; }

    /// <summary>購入済みマーク用の Bandcamp ユーザー名 (bandcamp.com/○○)。</summary>
    public string BandcampUsername { get; set; } = "";

    /// <summary>購入済みの曲をキューの表示 (と再生) から外す。</summary>
    public bool HidePurchased { get; set; }

    /// <summary>チェック済みの曲をキューの表示 (と再生) から外す。</summary>
    public bool HideChecked { get; set; }

    public WindowBounds? Window { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            SafeFile.WriteAllTextAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException)
        {
        }
    }
}
