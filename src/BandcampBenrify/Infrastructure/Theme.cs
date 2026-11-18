using System.Windows;
using System.Windows.Media;

namespace BandcampBenrify.Infrastructure;

/// <summary>
/// テーマカラー (アクセント色) を画面に反映する。
/// アプリ全体には設定の色を、再生バーにはジャケットの色を (設定でオンなら) 使う。
/// </summary>
public static class Theme
{
    public const string DefaultAccent = "#3CC8E8";

    /// <summary>設定画面に並べる色。</summary>
    public static IReadOnlyList<(string Name, string Hex)> Presets { get; } =
    [
        ("水色", "#3CC8E8"),
        ("琥珀", "#F2A541"),
        ("ライム", "#B5E853"),
        ("ローズ", "#E86A8A"),
        ("ラベンダー", "#A78BFA"),
        ("ホワイト", "#E8EAED"),
    ];

    /// <summary>画面の地の色 (アクセントを薄めるときに混ぜる)。</summary>
    private static readonly Color Background = Color.FromRgb(0x12, 0x14, 0x17);

    /// <summary>アクセント色の上に置く文字の色 (アクセントは明るい色なので暗い文字)。</summary>
    private static readonly Color OnAccent = Color.FromRgb(0x12, 0x14, 0x17);

    /// <summary>テーマカラーが変わったとき (設定の保存・プレビュー)。</summary>
    public static event Action? Changed;

    public static Color Accent { get; private set; } = Parse(DefaultAccent)!.Value;

    public static bool PlayerUsesArtColor { get; private set; } = true;

    public static Color? Parse(string? hex)
    {
        try
        {
            return hex is { Length: 7 } && hex[0] == '#' ? (Color)ColorConverter.ConvertFromString(hex) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>アプリ全体のテーマカラーを変える。</summary>
    public static void Apply(Color accent, bool playerUsesArtColor)
    {
        Accent = accent;
        PlayerUsesArtColor = playerUsesArtColor;
        SetAccent(Application.Current.Resources, accent);
        Changed?.Invoke();
    }

    /// <summary>
    /// ある要素の中だけアクセント色を変える (再生バーにジャケットの色を使うとき)。
    /// 自前のブラシと、Windows 11 テーマのボタン・スライダーが使うブラシの両方を置き換える。
    /// </summary>
    public static void SetAccent(ResourceDictionary target, Color accent)
    {
        var soft = Mix(accent, Background, 0.2);
        target["AccentColor"] = accent;
        target["AccentBrush"] = Frozen(accent);
        target["AccentSoftBrush"] = Frozen(soft);
        target["AccentBarBrush"] = Frozen(Mix(accent, Background, 0.5));

        // Windows 11 (Fluent) テーマのアクセント
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3" })
        {
            target[key] = accent;
        }

        foreach (var key in new[] { "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
        {
            target[key] = Mix(accent, Background, 0.7);
        }

        target["AccentFillColorDefaultBrush"] = Frozen(accent);
        target["AccentFillColorSecondaryBrush"] = Frozen(Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
        target["AccentFillColorTertiaryBrush"] = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        target["AccentTextFillColorPrimaryBrush"] = Frozen(accent);
        target["AccentTextFillColorSecondaryBrush"] = Frozen(accent);
        target["AccentTextFillColorTertiaryBrush"] = Frozen(accent);
        target["TextOnAccentFillColorPrimaryBrush"] = Frozen(OnAccent);
        target["TextOnAccentFillColorSecondaryBrush"] = Frozen(OnAccent);
    }

    /// <summary><see cref="SetAccent"/> で置いた色を外して、外側 (アプリ全体) の色に戻す。</summary>
    public static void ClearAccent(ResourceDictionary target)
    {
        foreach (var key in target.Keys.OfType<string>().Where(k => k.StartsWith("Accent", StringComparison.Ordinal) || k.StartsWith("SystemAccent", StringComparison.Ordinal) || k.StartsWith("TextOnAccent", StringComparison.Ordinal)).ToList())
        {
            target.Remove(key);
        }
    }

    public static Color Mix(Color a, Color b, double amountOfA) => Color.FromRgb(
        (byte)Math.Round(a.R * amountOfA + b.R * (1 - amountOfA)),
        (byte)Math.Round(a.G * amountOfA + b.G * (1 - amountOfA)),
        (byte)Math.Round(a.B * amountOfA + b.B * (1 - amountOfA)));

    public static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
