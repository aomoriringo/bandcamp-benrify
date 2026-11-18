using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace BandcampBenrify.Core;

/// <summary>
/// Bandcamp の HTML ページから必要な情報を取り出す。ネットワークアクセスはしない (純粋関数)。
/// </summary>
public static partial class BandcampParser
{
    private static readonly HtmlParser Parser = new();

    public static bool ContainsTralbum(string html) => html.Contains("data-tralbum", StringComparison.Ordinal);

    public static bool ContainsMusicGrid(string html) => html.Contains("music-grid", StringComparison.Ordinal);

    /// <summary>アルバム / 単曲ページを解析する。</summary>
    public static TralbumPage ParseTralbum(string html, string pageUrl, DateTimeOffset fetchedAt)
    {
        var doc = Parser.ParseDocument(html);
        var script = doc.QuerySelector("script[data-tralbum]")
            ?? throw new BandcampParseException("ページに data-tralbum が見つかりません (アルバム / 曲のページではない可能性があります)");

        using var tralbumDoc = JsonDocument.Parse(script.GetAttribute("data-tralbum")!);
        var root = tralbumDoc.RootElement;
        var current = root.TryGetProperty("current", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;

        using var embedDoc = TryParseJson(script.GetAttribute("data-embed"));
        var embed = embedDoc?.RootElement ?? default;

        var type = GetString(root, "item_type") == "track" ? ItemType.Track : ItemType.Album;
        var pageUri = new Uri(pageUrl);
        var url = ToAbsolute(pageUri, GetString(root, "url")) ?? BandcampUrls.Normalize(pageUrl);
        var baseUri = new Uri(url);

        var title = GetString(current, "title")
            ?? doc.QuerySelector("h2.trackTitle")?.TextContent.Trim()
            ?? "";
        var artist = GetString(root, "artist") ?? GetString(embed, "artist") ?? "";
        var albumTitle = type == ItemType.Album ? title : GetString(embed, "album_title") ?? "";
        var artId = GetLong(root, "art_id") ?? GetLong(current, "art_id");
        var releaseDate = ParseBandcampDate(GetString(root, "album_release_date"))
            ?? ParseBandcampDate(GetString(current, "release_date"))
            ?? ParseBandcampDate(GetString(current, "publish_date"));
        var id = GetLong(root, "id") ?? GetLong(current, "id") ?? 0;

        // 収録アルバム: アルバムページなら自分自身、曲ページなら current.album_id / album_url
        long? albumId = type == ItemType.Album ? id : GetLong(current, "album_id");
        var albumUrl = type == ItemType.Album ? url : StripQuery(ToAbsolute(baseUri, GetString(root, "album_url")));
        if (albumId is 0)
        {
            albumId = null;
        }

        var tracks = new List<TrackInfo>();
        if (root.TryGetProperty("trackinfo", out var trackinfo) && trackinfo.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in trackinfo.EnumerateArray())
            {
                var trackId = GetLong(t, "track_id") ?? GetLong(t, "id") ?? 0;
                if (trackId == 0)
                {
                    continue;
                }

                string? stream = null;
                if (t.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
                {
                    stream = GetString(file, "mp3-128");
                }

                var trackUrl = type == ItemType.Track
                    ? url
                    : ToAbsolute(baseUri, GetString(t, "title_link")) ?? url;

                tracks.Add(new TrackInfo
                {
                    TrackId = trackId,
                    Title = GetString(t, "title") ?? "",
                    Artist = GetString(t, "artist") ?? artist,
                    AlbumTitle = albumTitle,
                    TrackUrl = trackUrl,
                    SourcePageUrl = url,
                    DurationSeconds = GetDouble(t, "duration") ?? 0,
                    ArtId = artId,
                    StreamUrl = stream,
                    AlbumId = albumId,
                    AlbumUrl = albumUrl,
                });
            }
        }

        return new TralbumPage
        {
            Type = type,
            Id = id,
            Title = title,
            Artist = artist,
            Url = url,
            ArtId = artId,
            ReleaseDate = releaseDate,
            Tracks = tracks,
            Supporters = ParseSupporters(doc),
            Price = ParsePrice(doc, url, current),
            AlbumId = type == ItemType.Track ? albumId : null,
            AlbumUrl = type == ItemType.Track ? albumUrl : null,
            FetchedAt = fetchedAt,
        };
    }

    /// <summary>
    /// デジタル版の値段。JSON-LD のうち @id がこのページの URL で offers を持つもの
    /// (アルバム: albumRelease[], 曲: inAlbum.albumRelease[]) から取る。単品で買えない曲は見つからず null。
    /// </summary>
    private static PriceInfo? ParsePrice(IDocument doc, string url, JsonElement current)
    {
        var orMore = GetBool(current, "is_set_price") != true;
        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            using var ld = TryParseJson(script.TextContent);
            if (ld is null)
            {
                continue;
            }

            if (FindOffers(ld.RootElement, url) is { } offers)
            {
                var amount = GetDecimal(offers, "price");
                if (offers.TryGetProperty("priceSpecification", out var spec) && GetDecimal(spec, "minPrice") is decimal min)
                {
                    amount = min;
                }

                if (amount is decimal a)
                {
                    return new PriceInfo { Amount = a, Currency = GetString(offers, "priceCurrency") ?? "", OrMore = orMore };
                }
            }
        }

        return null;
    }

    private static JsonElement? FindOffers(JsonElement e, string url)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (GetString(e, "@id") is string id && SameUrl(id, url) && e.TryGetProperty("offers", out var offers))
                {
                    if (offers.ValueKind == JsonValueKind.Object)
                    {
                        return offers;
                    }

                    if (offers.ValueKind == JsonValueKind.Array && offers.GetArrayLength() > 0)
                    {
                        return offers[0];
                    }
                }

                foreach (var p in e.EnumerateObject())
                {
                    if (FindOffers(p.Value, url) is { } found)
                    {
                        return found;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    if (FindOffers(item, url) is { } found)
                    {
                        return found;
                    }
                }

                break;
        }

        return null;
    }

    private static bool SameUrl(string a, string b)
    {
        try
        {
            return BandcampUrls.Normalize(a) == BandcampUrls.Normalize(b) && !a.Contains('#');
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>ファンのコレクションページ (bandcamp.com/{username}) を解析する。</summary>
    public static FanPage ParseFanPage(string html)
    {
        var doc = Parser.ParseDocument(html);
        var blob = doc.GetElementById("pagedata")?.GetAttribute("data-blob")
            ?? throw new BandcampParseException("ファンページのデータが見つかりません。ユーザー名を確認してください。");
        using var json = JsonDocument.Parse(blob);
        var root = json.RootElement;
        var fanData = root.TryGetProperty("fan_data", out var f) ? f : default;
        var fanId = GetLong(fanData, "fan_id") ?? throw new BandcampParseException("ファン ID が見つかりません。ユーザー名を確認してください。");
        var collectionData = root.TryGetProperty("collection_data", out var cd) ? cd : default;

        var items = new List<CollectionItem>();
        if (root.TryGetProperty("item_cache", out var cache) && cache.TryGetProperty("collection", out var coll) && coll.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in coll.EnumerateObject())
            {
                if (ToCollectionItem(p.Value) is { } item)
                {
                    items.Add(item);
                }
            }
        }

        return new FanPage(
            fanId,
            (int)(GetLong(collectionData, "item_count") ?? items.Count),
            GetString(collectionData, "last_token"),
            items);
    }

    /// <summary>コレクションの続き (fancollection API の応答) を解析する。</summary>
    public static (List<CollectionItem> Items, bool MoreAvailable, string? LastToken) ParseCollectionItems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (GetBool(root, "error") == true)
        {
            throw new BandcampParseException(GetString(root, "error_message") ?? "コレクションの取得に失敗しました");
        }

        var items = new List<CollectionItem>();
        if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (ToCollectionItem(e) is { } item)
                {
                    items.Add(item);
                }
            }
        }

        return (items, GetBool(root, "more_available") ?? false, GetString(root, "last_token"));
    }

    private static CollectionItem? ToCollectionItem(JsonElement e)
    {
        var id = GetLong(e, "tralbum_id") ?? GetLong(e, "item_id");
        ItemType? type = GetString(e, "tralbum_type") switch
        {
            "a" => ItemType.Album,
            "t" => ItemType.Track,
            _ => null,
        };
        return id is long i && type is { } t ? new CollectionItem(t, i) : null;
    }

    private static SupporterInfo ParseSupporters(IDocument doc)
    {
        var blob = doc.GetElementById("collectors-data")?.GetAttribute("data-blob");
        if (string.IsNullOrEmpty(blob))
        {
            return new SupporterInfo();
        }

        using var json = JsonDocument.Parse(blob);
        var root = json.RootElement;
        var ids = new List<long>();
        var seen = new HashSet<long>();
        var lastThumb = CollectFans(root, "thumbs", ids, seen);
        var lastReview = CollectFans(root, "reviews", ids, seen);

        return new SupporterInfo
        {
            FanIds = ids,
            MoreThumbs = GetBool(root, "more_thumbs_available") ?? false,
            MoreReviews = GetBool(root, "more_reviews_available") ?? false,
            LastThumbToken = lastThumb,
            LastReviewToken = lastReview,
        };
    }

    /// <summary>thumbs / reviews / results 配列から fan_id を重複なく集め、最後のファンのトークンを返す。</summary>
    public static string? CollectFans(JsonElement root, string property, List<long> ids, HashSet<long> seen)
    {
        string? lastToken = null;
        if (root.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var fan in arr.EnumerateArray())
            {
                if (GetLong(fan, "fan_id") is long fanId && seen.Add(fanId))
                {
                    ids.Add(fanId);
                }

                lastToken = GetString(fan, "token") ?? lastToken;
            }
        }

        return lastToken;
    }

    /// <summary>サポーター一覧の続き (tralbumcollectors API の応答) を解析する。</summary>
    public static (string? LastToken, bool MoreAvailable) ParseCollectorsPage(string json, List<long> ids, HashSet<long> seen)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (GetBool(root, "error") == true)
        {
            throw new BandcampParseException(GetString(root, "error_message") ?? "サポーター一覧の取得に失敗しました");
        }

        var token = CollectFans(root, "results", ids, seen);
        return (token, GetBool(root, "more_available") ?? false);
    }

    /// <summary>アーティスト / レーベルの /music ページを解析する。</summary>
    public static Discography ParseDiscography(string html, string pageUrl, DateTimeOffset fetchedAt)
    {
        var doc = Parser.ParseDocument(html);
        var pageUri = new Uri(pageUrl);

        string? bandName = null;
        var bandScript = doc.QuerySelector("script[data-band]");
        using (var bandJson = TryParseJson(bandScript?.GetAttribute("data-band")))
        {
            if (bandJson is not null)
            {
                bandName = GetString(bandJson.RootElement, "name");
            }
        }

        bandName ??= doc.QuerySelector("#band-name-location .title")?.TextContent.Trim()
            ?? doc.Title?.Split('|').LastOrDefault()?.Trim()
            ?? pageUri.Host;

        var items = new List<DiscographyItem>();
        var seen = new HashSet<string>();

        foreach (var li in doc.QuerySelectorAll("li[data-item-id]"))
        {
            var (type, id) = ParseItemId(li.GetAttribute("data-item-id"));
            if (type is null || !seen.Add($"{type}-{id}"))
            {
                continue;
            }

            var href = li.QuerySelector("a[href]")?.GetAttribute("href");
            var itemUrl = StripQuery(ToAbsolute(pageUri, href));
            if (itemUrl is null)
            {
                continue;
            }

            var titleEl = li.QuerySelector(".title");
            var artist = Collapse(titleEl?.QuerySelector(".artist-override")?.TextContent) ?? bandName;
            string title = "";
            if (titleEl is not null)
            {
                var clone = (IElement)titleEl.Clone(deep: true);
                foreach (var a in clone.QuerySelectorAll(".artist-override").ToList())
                {
                    a.Remove();
                }

                title = Collapse(clone.TextContent) ?? "";
            }

            var img = li.QuerySelector(".art img");
            var art = img?.GetAttribute("data-original");
            if (string.IsNullOrEmpty(art))
            {
                art = img?.GetAttribute("src");
            }

            if (art is not null && art.EndsWith("/0.gif", StringComparison.Ordinal))
            {
                art = null;
            }

            items.Add(new DiscographyItem
            {
                Type = type.Value,
                Id = id,
                Title = title,
                Artist = artist,
                Url = itemUrl,
                ArtUrl = ToAbsolute(pageUri, art),
            });
        }

        // HTML に描画されていない分は data-client-items に入っていることがあるので補完する
        var grid = doc.QuerySelector("[data-client-items]");
        using (var clientItems = TryParseJson(grid?.GetAttribute("data-client-items")))
        {
            if (clientItems?.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var ci in clientItems.RootElement.EnumerateArray())
                {
                    var typeStr = GetString(ci, "type");
                    var id = GetLong(ci, "id") ?? 0;
                    ItemType? type = typeStr switch { "album" => ItemType.Album, "track" => ItemType.Track, _ => null };
                    var itemUrl = StripQuery(ToAbsolute(pageUri, GetString(ci, "page_url")));
                    if (type is null || id == 0 || itemUrl is null || !seen.Add($"{type}-{id}"))
                    {
                        continue;
                    }

                    items.Add(new DiscographyItem
                    {
                        Type = type.Value,
                        Id = id,
                        Title = GetString(ci, "title") ?? "",
                        Artist = GetString(ci, "artist") ?? bandName,
                        Url = itemUrl,
                        ArtUrl = BandcampUrls.ArtUrl(GetLong(ci, "art_id"), ArtSize.Small),
                    });
                }
            }
        }

        return new Discography
        {
            BandName = bandName,
            Url = BandcampUrls.MusicPage(pageUrl),
            Items = items,
            FetchedAt = fetchedAt,
        };
    }

    private static (ItemType? Type, long Id) ParseItemId(string? itemId)
    {
        if (itemId is null)
        {
            return (null, 0);
        }

        var dash = itemId.IndexOf('-');
        if (dash <= 0 || !long.TryParse(itemId.AsSpan(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return (null, 0);
        }

        return itemId[..dash] switch
        {
            "album" => (ItemType.Album, id),
            "track" => (ItemType.Track, id),
            _ => (null, 0),
        };
    }

    /// <summary>"15 Nov 2011 00:00:00 GMT" 形式の日付。</summary>
    public static DateTimeOffset? ParseBandcampDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            s.Replace(" GMT", " +00:00", StringComparison.Ordinal),
            "dd MMM yyyy HH:mm:ss zzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var d)
            ? d
            : null;
    }

    private static string? ToAbsolute(Uri baseUri, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        return Uri.TryCreate(baseUri, href, out var abs) ? abs.ToString() : null;
    }

    private static string? StripQuery(string? url)
    {
        if (url is null)
        {
            return null;
        }

        var uri = new Uri(url);
        return $"https://{uri.Host}{uri.AbsolutePath.TrimEnd('/')}";
    }

    private static string? Collapse(string? s)
    {
        if (s is null)
        {
            return null;
        }

        var collapsed = Whitespace().Replace(s, " ").Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static JsonDocument? TryParseJson(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(s);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    internal static long? GetLong(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var l) => l,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
            _ => null,
        };
    }

    internal static decimal? GetDecimal(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)
            ? d
            : null;

    internal static double? GetDouble(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    internal static bool? GetBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
