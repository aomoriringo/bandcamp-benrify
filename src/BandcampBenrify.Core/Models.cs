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

    /// <summary>レーベル名。自主リリースは ""、古いデータで不明なら null。</summary>
    public string? Label { get; init; }

    /// <summary>この曲が収録されているアルバムの ID / URL (単独リリースの曲は null)。</summary>
    public long? AlbumId { get; init; }

    public string? AlbumUrl { get; init; }

    public string ArtUrl => BandcampUrls.ArtUrl(ArtId, ArtSize.Small);
}

/// <summary>デジタル版の値段。</summary>
public sealed record PriceInfo
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$",
        ["EUR"] = "€",
        ["GBP"] = "£",
        ["JPY"] = "¥",
        ["CAD"] = "CA$",
        ["AUD"] = "A$",
        ["NZD"] = "NZ$",
    };

    public decimal Amount { get; init; }

    public string Currency { get; init; } = "";

    /// <summary>「またはそれ以上」(金額を上乗せできる) か。</summary>
    public bool OrMore { get; init; }

    public string Display
    {
        get
        {
            if (Amount == 0)
            {
                return OrMore ? "自由価格" : "無料";
            }

            var number = Amount.ToString(Amount == decimal.Truncate(Amount) ? "0" : "0.00", System.Globalization.CultureInfo.InvariantCulture);
            return Symbols.TryGetValue(Currency, out var sym) ? sym + number : $"{number} {Currency}".Trim();
        }
    }
}

/// <summary>ファンのコレクション (購入済みの作品)。</summary>
public sealed record FanCollection
{
    public string Username { get; init; } = "";
    public long FanId { get; init; }

    /// <summary>Bandcamp 上の公開コレクション件数。</summary>
    public int ItemCount { get; init; }

    public IReadOnlyList<long> AlbumIds { get; init; } = [];
    public IReadOnlyList<long> TrackIds { get; init; } = [];

    /// <summary>途中で中止した場合、続き (より古い購入) を読み込むためのトークン。全件読み込み済みなら null。</summary>
    public string? ResumeToken { get; init; }

    public bool Complete => ResumeToken is null;

    public DateTimeOffset FetchedAt { get; init; }

    public int Count => AlbumIds.Count + TrackIds.Count;

    public IEnumerable<CollectionItem> Items() =>
        AlbumIds.Select(id => new CollectionItem(ItemType.Album, id)).Concat(TrackIds.Select(id => new CollectionItem(ItemType.Track, id)));
}

/// <summary>コレクション読み込みの進み具合。Partial は途中経過 (購入済みの印を先に付けるため)。</summary>
public sealed record CollectionProgress(int Loaded, int Total, FanCollection? Partial = null);

/// <summary>ページに埋め込まれている「サポーター」(購入したファン) の情報。</summary>
public sealed record SupporterInfo
{
    /// <summary>ページから見えるファン (重複除去済み)。</summary>
    public IReadOnlyList<long> FanIds { get; init; } = [];

    public bool MoreThumbs { get; init; }
    public bool MoreReviews { get; init; }

    /// <summary>続きを数えるためのトークン (最後に数えたファンのもの)。</summary>
    public string? LastThumbToken { get; init; }
    public string? LastReviewToken { get; init; }

    public int Count => FanIds.Count;

    /// <summary>ページに載りきらないサポーターがいる (実際は Count より多い)。</summary>
    public bool HasMore => MoreThumbs || MoreReviews;

    public string Display => HasMore ? $"{Count}+" : Count.ToString();

    /// <summary>ソート用キー。「N+」は N より少し大きいものとして扱う。</summary>
    public double SortKey => Count + (HasMore ? 0.5 : 0);
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

    /// <summary>デジタル版の値段。単品で買えない曲などは null。</summary>
    public PriceInfo? Price { get; init; }

    /// <summary>
    /// レーベル名。アーティストのページで売られている作品は JSON-LD の recordLabel、
    /// レーベルのページで売られている作品はページの持ち主 (publisher)。自主リリースは ""。
    /// 古い版で保存したキャッシュでは null (不明)。
    /// </summary>
    public string? Label { get; init; }

    /// <summary>曲ページの場合、収録アルバムの ID / URL。</summary>
    public long? AlbumId { get; init; }

    public string? AlbumUrl { get; init; }

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

/// <summary>Bandcamp がアクセス確認 (ボット対策) のページを返した。回避はしない。</summary>
public sealed class BandcampBlockedException(string message) : HttpRequestException(message);

public sealed record CollectionItem(ItemType Type, long Id);

public enum SearchKind
{
    Album,
    Track,
    /// <summary>アーティスト / レーベル</summary>
    Band,
}


/// <summary>ファンページの解析結果。Items は最初の 20 件程度だけで、続きは API で取る。</summary>
public sealed record FanPage(long FanId, int ItemCount, string? LastToken, IReadOnlyList<CollectionItem> Items);
