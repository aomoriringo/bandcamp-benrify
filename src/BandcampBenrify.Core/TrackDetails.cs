namespace BandcampBenrify.Core;

public enum PurchaseState
{
    None,
    /// <summary>曲を単品で購入済み。</summary>
    Track,
    /// <summary>収録アルバムを購入済み。</summary>
    Album,
}

/// <summary>
/// キューの 1 曲について、アルバムページと曲ページから集めた情報。
/// サポーター合計 = アルバムを買った人 + その曲を単品で買った人。
/// </summary>
public sealed record TrackDetails
{
    /// <summary>収録アルバムの URL (単独リリースの曲、または曲ページ未取得で不明なら null)。</summary>
    public string? AlbumUrl { get; init; }

    /// <summary>アルバムに収録されている曲か (単独リリースの曲は false)。</summary>
    public bool HasAlbum => AlbumUrl is not null;

    public TralbumPage? AlbumPage { get; init; }

    public TralbumPage? TrackPage { get; init; }

    public SupporterInfo? AlbumSupporters => AlbumPage?.Supporters;

    public SupporterInfo? TrackSupporters => TrackPage?.Supporters;

    /// <summary>キューに入れたときの曲データが持っていたレーベル (古いデータでは null)。</summary>
    public string? TrackLabel { get; init; }

    /// <summary>レーベル名 ("" は自主リリース、null は不明 = 古いキャッシュでページの取り直しが必要)。</summary>
    public string? Label => AlbumPage?.Label ?? TrackPage?.Label ?? TrackLabel;

    public PriceInfo? AlbumPrice => AlbumPage?.Price;

    public PriceInfo? TrackPrice => TrackPage?.Price;

    /// <summary>合計に必要なページがすべて取得済みか。</summary>
    public bool IsComplete => TrackPage is not null && (!HasAlbum || AlbumPage is not null);

    public bool HasAnyData => TrackPage is not null || AlbumPage is not null;

    public int Total => (AlbumSupporters?.Count ?? 0) + (TrackSupporters?.Count ?? 0);

    public bool TotalHasMore => AlbumSupporters?.HasMore == true || TrackSupporters?.HasMore == true;

    /// <summary>例: "48" / "86+" (ページに載りきらない) / "45?" (曲ページ未取得)。</summary>
    public string TotalDisplay => !HasAnyData ? "…" : $"{Total}{(TotalHasMore ? "+" : "")}{(IsComplete ? "" : "?")}";

    public double TotalSortKey => HasAnyData ? Total + (TotalHasMore ? 0.5 : 0) : -1;

    /// <summary>
    /// 曲ページ / アルバムページのうち未取得の URL。
    /// 取得済みでも古い版で保存したためレーベルが分からないページは、取り直し対象として含める。
    /// </summary>
    public IEnumerable<string> MissingPages(TrackInfo track)
    {
        if (TrackPage is null)
        {
            yield return track.TrackUrl;
        }

        if (AlbumUrl is not null && AlbumPage is null)
        {
            yield return AlbumUrl;
        }

        if (Label is null && (AlbumUrl is not null ? AlbumPage : TrackPage) is { } labelPage)
        {
            yield return labelPage.Url;
        }
    }
}

public static class TrackDetailsResolver
{
    /// <summary>収録アルバムの URL。古いキューのデータで AlbumUrl が無い場合は取得元ページから推定する。</summary>
    public static string? AlbumUrlOf(TrackInfo track)
    {
        if (!string.IsNullOrEmpty(track.AlbumUrl))
        {
            return track.AlbumUrl;
        }

        return !string.IsNullOrEmpty(track.SourcePageUrl) && BandcampUrls.Classify(track.SourcePageUrl) == UrlKind.Album
            ? track.SourcePageUrl
            : null;
    }

    /// <param name="lookup">URL からキャッシュ済みページを引く関数 (リクエストは送らないこと)。</param>
    public static TrackDetails Resolve(TrackInfo track, Func<string, TralbumPage?> lookup)
    {
        var trackPage = lookup(track.TrackUrl);
        if (trackPage is { Type: not ItemType.Track })
        {
            trackPage = null; // 曲 URL がアルバムに転送されていた等
        }

        var albumUrl = AlbumUrlOf(track) ?? trackPage?.AlbumUrl;
        var albumPage = albumUrl is null ? null : lookup(albumUrl);
        if (albumPage is { Type: not ItemType.Album })
        {
            albumPage = null;
        }

        return new TrackDetails
        {
            TrackLabel = track.Label,
            AlbumUrl = albumUrl,
            AlbumPage = albumPage,
            TrackPage = trackPage,
        };
    }

    public static PurchaseState Purchased(TrackInfo track, TrackDetails details, IReadOnlySet<long> albumIds, IReadOnlySet<long> trackIds)
    {
        var albumId = track.AlbumId ?? details.AlbumPage?.Id ?? details.TrackPage?.AlbumId;
        if (albumId is long a && albumIds.Contains(a))
        {
            return PurchaseState.Album;
        }

        return trackIds.Contains(track.TrackId) ? PurchaseState.Track : PurchaseState.None;
    }
}
