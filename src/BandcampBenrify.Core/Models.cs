namespace BandcampBenrify.Core;

public enum ItemType
{
    Album,
    Track,
}

public enum UrlKind
{
    Album,
    Track,
    /// <summary>アーティスト / レーベルのページ (サブドメインのトップや /music)。</summary>
    Band,
}

/// <summary>1 曲分の情報。キューに入る単位。</summary>
public sealed record TrackInfo
{
    public long TrackId { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string AlbumTitle { get; init; } = "";
    public string TrackUrl { get; init; } = "";

    /// <summary>この曲の data-tralbum を含むページ (アルバムページ、または単曲ページ)。ストリームURLの再取得に使う。</summary>
    public string SourcePageUrl { get; init; } = "";

    public double DurationSeconds { get; init; }
    public long? ArtId { get; init; }

    /// <summary>mp3-128 のストリームURL。有効期限付きなので再生直前に <see cref="StreamUrls.IsExpired"/> で確認する。</summary>
    public string? StreamUrl { get; init; }

    public string ArtUrl => BandcampUrls.ArtUrl(ArtId, ArtSize.Small);
}

/// <summary>ページに埋め込まれている「サポーター」(購入したファン) の情報。</summary>
public sealed record SupporterInfo
{
    /// <summary>ページから見えるファン (重複除去済み)。</summary>
    public IReadOnlyList<long> FanIds { get; init; } = [];

    public bool MoreThumbs { get; init; }
    public bool MoreReviews { get; init; }
    public string? LastThumbToken { get; init; }
    public string? LastReviewToken { get; init; }

    /// <summary>追加ページングで全件数えきれた場合の正確な人数。</summary>
    public int? ExactCount { get; init; }

    public int VisibleCount => FanIds.Count;

    /// <summary>まだ数えきれていないサポーターがいるか。</summary>
    public bool HasMore => ExactCount is null && (MoreThumbs || MoreReviews);

    public int BestCount => ExactCount ?? VisibleCount;

    public string Display => HasMore ? $"{BestCount}+" : BestCount.ToString();

    /// <summary>ソート用キー。「N+」は N より少し大きいものとして扱う。</summary>
    public double SortKey => BestCount + (HasMore ? 0.5 : 0);
}

/// <summary>アルバム or 単曲ページの解析結果。</summary>
public sealed record TralbumPage
{
    public ItemType Type { get; init; }
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Url { get; init; } = "";
    public long? ArtId { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }
    public IReadOnlyList<TrackInfo> Tracks { get; init; } = [];
    public SupporterInfo Supporters { get; init; } = new();
    public DateTimeOffset FetchedAt { get; init; }
}

/// <summary>レーベル / アーティストの /music に並んでいる作品 1 件。</summary>
public sealed record DiscographyItem
{
    public ItemType Type { get; init; }
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Url { get; init; } = "";
    public string? ArtUrl { get; init; }
}

public sealed record Discography
{
    public string BandName { get; init; } = "";
    public string Url { get; init; } = "";
    public IReadOnlyList<DiscographyItem> Items { get; init; } = [];
    public DateTimeOffset FetchedAt { get; init; }
}

public sealed class BandcampParseException(string message) : Exception(message);
