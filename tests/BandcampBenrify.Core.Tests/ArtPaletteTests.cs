using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

public sealed class ArtPaletteTests
{
    /// <summary>指定した色のピクセルを並べた BGRA 画像。</summary>
    private static byte[] Image(params (Rgb Color, int Count)[] parts)
    {
        var list = new List<byte>();
        foreach (var (c, n) in parts)
        {
            for (var i = 0; i < n; i++)
            {
                list.AddRange([c.B, c.G, c.R, 255]);
            }
        }

        return [.. list];
    }

    [Fact]
    public void Dominant_PicksTheVividColor_OverGrayBackground()
    {
        var img = Image((new Rgb(30, 30, 30), 600), (new Rgb(200, 40, 40), 300), (new Rgb(240, 240, 240), 100));

        var c = ArtPalette.Dominant(img, step: 1);

        Assert.NotNull(c);
        Assert.True(c.Value.R > 150 && c.Value.G < 80 && c.Value.B < 80, c.Value.Hex);
    }

    [Fact]
    public void Dominant_PicksTheLargerHue()
    {
        var img = Image((new Rgb(40, 90, 200), 700), (new Rgb(220, 180, 40), 300));

        var c = ArtPalette.Dominant(img, step: 1);

        Assert.NotNull(c);
        Assert.True(c.Value.B > c.Value.R, c.Value.Hex);
    }

    [Fact]
    public void Dominant_ReturnsNull_ForMonochromeArt()
    {
        var img = Image((new Rgb(0, 0, 0), 500), (new Rgb(255, 255, 255), 400), (new Rgb(128, 128, 128), 100));

        Assert.Null(ArtPalette.Dominant(img, step: 1));
    }

    [Fact]
    public void AccentAndBackground_AreReadableOnDarkUi()
    {
        var dark = new Rgb(40, 10, 10); // 暗くくすんだ赤

        var accent = ArtPalette.Accent(dark);
        var bg = ArtPalette.Background(dark);

        Assert.True(accent.R > 180, accent.Hex);         // 明るく持ち上がる
        Assert.True(accent.R > accent.G, accent.Hex);    // 色相 (赤) は保つ
        Assert.True(bg.R < 60 && bg.G < 40, bg.Hex);     // 背景は暗いまま
        Assert.True(bg.R > bg.G, bg.Hex);
    }
}
