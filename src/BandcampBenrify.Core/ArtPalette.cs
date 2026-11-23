namespace BandcampBenrify.Core;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// ジャケット画像から再生バーに使う色を選ぶ (画像は画面に表示するために読み込んだものを使い、色のために取得はしない)。
/// </summary>
public static class ArtPalette
{
    private const int HueBins = 24;

    /// <summary>
    /// 画像でいちばん目立つ「色味のある」色。白黒やグレーばかりの画像は null。
    /// </summary>
    /// <param name="bgra">32bit BGRA のピクセル列。</param>
    public static Rgb? Dominant(ReadOnlySpan<byte> bgra, int step = 3)
    {
        var weight = new double[HueBins];
        var sumR = new double[HueBins];
        var sumG = new double[HueBins];
        var sumB = new double[HueBins];
        int sampled = 0, vivid = 0;

        for (var i = 0; i + 3 < bgra.Length; i += 4 * step)
        {
            sampled++;
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            var (h, s, v) = ToHsv(r, g, b);
            if (v < 0.2 || s < 0.25)
            {
                continue;
            }

            vivid++;
            var w = s * v;
            var bin = (int)(h / 360 * HueBins) % HueBins;
            weight[bin] += w;
            sumR[bin] += r * w;
            sumG[bin] += g * w;
            sumB[bin] += b * w;
        }

        // 色味のあるピクセルがほとんど無い (モノクロのジャケットなど)
        if (sampled == 0 || vivid < sampled * 0.04)
        {
            return null;
        }

        // となりのビンと合わせて一番重い色相を選ぶ (境目で割れないように)
        var best = 0;
        var bestWeight = -1.0;
        for (var bin = 0; bin < HueBins; bin++)
        {
            var w = weight[bin] + 0.5 * (weight[(bin + 1) % HueBins] + weight[(bin + HueBins - 1) % HueBins]);
            if (w > bestWeight)
            {
                bestWeight = w;
                best = bin;
            }
        }

        double tw = 0, tr = 0, tg = 0, tb = 0;
        foreach (var bin in new[] { (best + HueBins - 1) % HueBins, best, (best + 1) % HueBins })
        {
            tw += weight[bin];
            tr += sumR[bin];
            tg += sumG[bin];
            tb += sumB[bin];
        }

        return new Rgb((byte)Math.Round(tr / tw), (byte)Math.Round(tg / tw), (byte)Math.Round(tb / tw));
    }

    /// <summary>暗い背景の上で目立つ明るさ・鮮やかさに揃えた色 (再生ボタンやシークバー用)。</summary>
    public static Rgb Accent(Rgb c)
    {
        var (h, s, l) = ToHsl(c);
        return FromHsl(h, Math.Max(s, 0.5), Math.Clamp(l, 0.6, 0.72));
    }

    /// <summary>
    /// アクセント色から色相を少しずつずらした色の並び (ビジュアライザの輪で、低音から高音へ色を変えるため)。
    /// </summary>
    /// <param name="hueShift">全体の色相をずらす角度 (度)。時間とともに増やすと、色がゆっくり移り変わる。</param>
    public static Rgb[] Shades(Rgb c, int count, double hueSpread = 50, double hueShift = 0)
    {
        var (h, s, l) = ToHsl(Accent(c));
        var result = new Rgb[count];
        for (var i = 0; i < count; i++)
        {
            var t = count == 1 ? 0 : (double)i / (count - 1) - 0.5;
            var hue = ((h + hueShift + t * hueSpread) % 360 + 360) % 360;
            result[i] = FromHsl(hue, s, Math.Clamp(l + t * 0.12, 0.5, 0.8));
        }

        return result;
    }

    /// <summary>ジャケットの色をほんのり残した暗い背景色 (再生バーの背景用)。</summary>
    public static Rgb Background(Rgb c)
    {
        var (h, s, _) = ToHsl(c);
        return FromHsl(h, Math.Min(s, 0.45), 0.13);
    }

    private static (double H, double S, double V) ToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        return (Hue(r, g, b, max, d), max == 0 ? 0 : d / max, max);
    }

    private static (double H, double S, double L) ToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        var l = (max + min) / 2;
        var s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return (Hue(r, g, b, max, d), s, l);
    }

    private static double Hue(double r, double g, double b, double max, double d)
    {
        if (d == 0)
        {
            return 0;
        }

        var h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static Rgb FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte To8(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new Rgb(To8(r + m), To8(g + m), To8(b + m));
    }
}
