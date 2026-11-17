using System.Globalization;
using System.Web;

namespace BandcampBenrify.Core;

public enum ArtSize
{
    /// <summary>160px</summary>
    Small = 7,
    /// <summary>350px</summary>
    Medium = 2,
}

public static class BandcampUrls
{
    /// <summary>入力された URL を正規化する (https 補完、クエリ・フラグメント・末尾スラッシュ除去)。</summary>
    public static string Normalize(string url)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"URL として解釈できません: {url}");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return $"https://{uri.Host.ToLowerInvariant()}{path}";
    }

    public static UrlKind Classify(string url)
    {
        var path = new Uri(Normalize(url)).AbsolutePath;
        if (path.Contains("/album/", StringComparison.OrdinalIgnoreCase))
        {
            return UrlKind.Album;
        }

        if (path.Contains("/track/", StringComparison.OrdinalIgnoreCase))
        {
            return UrlKind.Track;
        }

        return UrlKind.Band;
    }

    /// <summary>アーティスト / レーベルの作品一覧ページ (/music) の URL。</summary>
    public static string MusicPage(string url)
    {
        var uri = new Uri(Normalize(url));
        return $"https://{uri.Host}/music";
    }

    public static string Origin(string url)
    {
        var uri = new Uri(Normalize(url));
        return $"https://{uri.Host}";
    }

    /// <summary>
    /// ファンのプロフィール (「aomoriringo」「@aomoriringo」「https://bandcamp.com/aomoriringo?from=x」など) からユーザー名を取り出す。
    /// 解釈できなければ null。
    /// </summary>
    public static string? ParseFanUsername(string? input)
    {
        var s = input?.Trim() ?? "";
        if (s.Length == 0)
        {
            return null;
        }

        if (s.Contains("bandcamp.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!s.Contains("://", StringComparison.Ordinal))
            {
                s = "https://" + s;
            }

            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || !uri.Host.Equals("bandcamp.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.bandcamp.com", StringComparison.OrdinalIgnoreCase))
            {
                return null; // xxx.bandcamp.com はアーティストのページ
            }

            s = uri.AbsolutePath.Trim('/').Split('/')[0];
        }

        s = s.TrimStart('@');
        return s.Length > 0 && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.') ? s : null;
    }

    public static string ArtUrl(long? artId, ArtSize size) =>
        artId is long id && id > 0
            ? $"https://f4.bcbits.com/img/a{id.ToString("D10", CultureInfo.InvariantCulture)}_{(int)size}.jpg"
            : "";
}

public static class StreamUrls
{
    /// <summary>
    /// ストリーム URL が期限切れ (または間もなく切れる) か。
    /// Bandcamp の mp3-128 URL には ts= (unix 秒) が付いているのでそれを見る。無い場合は取得から 1 時間で切れたとみなす。
    /// </summary>
    public static bool IsExpired(string streamUrl, DateTimeOffset fetchedAt, DateTimeOffset now)
    {
        var margin = TimeSpan.FromMinutes(10);
        if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            var ts = query["ts"];
            if (long.TryParse(ts, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unix) - margin <= now;
            }
        }

        return fetchedAt + TimeSpan.FromHours(1) - margin <= now;
    }
}
