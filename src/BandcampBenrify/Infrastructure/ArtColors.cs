using System.Windows.Media;
using System.Windows.Media.Imaging;
using BandcampBenrify.Core;

namespace BandcampBenrify.Infrastructure;

/// <summary>表示したジャケット画像から色を取る (色のために画像を取り直すことはしない)。</summary>
public static class ArtColors
{
    /// <summary>ジャケットの代表色。色味のないジャケットや読めない画像は null。</summary>
    public static Rgb? Pick(BitmapSource bitmap)
    {
        try
        {
            var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = bgra.PixelWidth * 4;
            var pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);
            return ArtPalette.Dominant(pixels);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.IO.IOException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>画像の読み込みが終わったら (済んでいればすぐ) 代表色を渡す。</summary>
    public static void WhenLoaded(BitmapSource bitmap, Action<Rgb?> onColor)
    {
        if (bitmap.IsDownloading)
        {
            bitmap.DownloadCompleted += (_, _) => onColor(Pick(bitmap));
            bitmap.DownloadFailed += (_, _) => onColor(null);
        }
        else
        {
            onColor(Pick(bitmap));
        }
    }

    public static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);
}
