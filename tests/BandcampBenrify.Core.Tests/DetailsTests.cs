using System.Net;
using System.Text.Json;
using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

public class DetailsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    private const string AlbumUrl = "https://joviale.bandcamp.com/album/mount-crystal";
    private const string SnowUrl = "https://joviale.bandcamp.com/track/snow";

    private static string Attr(object o) => WebUtility.HtmlEncode(JsonSerializer.Serialize(o));

    private static string Blob(int fans, bool more = false) => Attr(new
    {
        thumbs = Enumerable.Range(1, fans).Select(i => new { fan_id = i, token = $"t{i}" }).ToArray(),
        reviews = Array.Empty<object>(),
        more_thumbs_available = more,
        more_reviews_available = false,
    });

    private static string AlbumHtml(int fans, bool more = false)
    {
        var tralbum = new
        {
            current = new { title = "Mount Crystal", minimum_price = 10, is_set_price = (bool?)null },
            artist = "Joviale",
            item_type = "album",
            id = 2189607369L,
            url = AlbumUrl,
            trackinfo = new object[]
            {
                new { id = 1836575030L, track_id = 1836575030L, title = "Snow", duration = 153.0, title_link = "/track/snow", file = new Dictionary<string, string> { ["mp3-128"] = "https://t4.bcbits.com/x?ts=1" } },
            },
        };
        var ld = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@type"] = "MusicAlbum",
            ["albumRelease"] = new object[]
            {
                new Dictionary<string, object> { ["@id"] = AlbumUrl, ["musicReleaseFormat"] = "DigitalFormat", ["offers"] = new { price = 10.0, priceCurrency = "USD", priceSpecification = new { minPrice = 10.0 } } },
                new Dictionary<string, object> { ["@id"] = AlbumUrl + "#p123", ["musicReleaseFormat"] = "VinylFormat", ["offers"] = new { price = 27.0, priceCurrency = "USD" } },
            },
        });
        return $"""
            <html><head>
            <script type="application/ld+json">{ld}</script>
            <script data-tralbum="{Attr(tralbum)}"></script></head>
            <body><div id="collectors-data" data-blob="{Blob(fans, more)}"></div></body></html>
            """;
    }

    private static string TrackHtml(int fans, bool sellable = true)
    {
        var tralbum = new
        {
            current = new { title = "Snow", minimum_price = 1, set_price = 1.5, album_id = 2189607369L, is_set_price = (bool?)null },
            artist = "Joviale",
            item_type = "track",
            id = 1836575030L,
            url = SnowUrl,
            album_url = "/album/mount-crystal",
            trackinfo = new object[]
            {
                new { id = 1836575030L, track_id = 1836575030L, title = "Snow", duration = 153.0, file = new Dictionary<string, string> { ["mp3-128"] = "https://t4.bcbits.com/x?ts=1" } },
            },
        };
        var releases = new List<object>
        {
            new Dictionary<string, object> { ["@id"] = AlbumUrl },
        };
        if (sellable)
        {
            releases.Add(new Dictionary<string, object> { ["@id"] = SnowUrl, ["offers"] = new { price = 1.0, priceCurrency = "EUR", priceSpecification = new { minPrice = 1.0 } } });
        }

        var ld = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@type"] = "MusicRecording",
            ["inAlbum"] = new Dictionary<string, object> { ["albumRelease"] = releases },
        });
        return $"""
            <html><head>
            <script type="application/ld+json">{ld}</script>
            <script data-tralbum="{Attr(tralbum)}" data-embed="{Attr(new { album_title = "Mount Crystal" })}"></script></head>
            <body><div id="collectors-data" data-blob="{Blob(fans)}"></div></body></html>
            """;
    }

    [Fact]
    public void ParsesAlbumDigitalPriceNotVinyl()
    {
        var page = BandcampParser.ParseTralbum(AlbumHtml(45), AlbumUrl, Now);
        Assert.NotNull(page.Price);
        Assert.Equal(10m, page.Price!.Amount);
        Assert.Equal("$10", page.Price.Display);
        Assert.Equal(2189607369L, page.Tracks[0].AlbumId);
        Assert.Equal(AlbumUrl, page.Tracks[0].AlbumUrl);
    }

    [Fact]
    public void ParsesTrackPagePriceAndAlbum()
    {
        var page = BandcampParser.ParseTralbum(TrackHtml(2), SnowUrl, Now);
        Assert.Equal(ItemType.Track, page.Type);
        Assert.Equal("€1", page.Price!.Display);
        Assert.Equal(2189607369L, page.AlbumId);
        Assert.Equal(AlbumUrl, page.AlbumUrl);
        Assert.Equal(AlbumUrl, page.Tracks[0].AlbumUrl);
        Assert.Equal(2, page.Supporters.Count);
    }

    [Fact]
    public void TrackNotSoldSeparatelyHasNoPrice()
    {
        var page = BandcampParser.ParseTralbum(TrackHtml(0, sellable: false), SnowUrl, Now);
        Assert.Null(page.Price);
    }

    [Theory]
    [InlineData(10, "USD", true, "$10")]
    [InlineData(1.5, "GBP", false, "£1.50")]
    [InlineData(1000, "JPY", true, "¥1000")]
    [InlineData(7, "SEK", true, "7 SEK")]
    [InlineData(0, "USD", true, "自由価格")]
    [InlineData(0, "USD", false, "無料")]
    public void FormatsPrices(double amount, string currency, bool orMore, string expected)
    {
        Assert.Equal(expected, new PriceInfo { Amount = (decimal)amount, Currency = currency, OrMore = orMore }.Display);
    }

    [Fact]
    public void TotalIsAlbumPlusTrackSupporters()
    {
        var album = BandcampParser.ParseTralbum(AlbumHtml(45), AlbumUrl, Now);
        var trackPage = BandcampParser.ParseTralbum(TrackHtml(2), SnowUrl, Now);
        var queued = album.Tracks[0];

        // 曲ページ未取得: アルバム分だけ、「?」付き
        var partial = TrackDetailsResolver.Resolve(queued, url => url == AlbumUrl ? album : null);
        Assert.Equal(45, partial.Total);
        Assert.Equal("45?", partial.TotalDisplay);
        Assert.False(partial.IsComplete);
        Assert.Equal([SnowUrl], partial.MissingPages(queued));

        var full = TrackDetailsResolver.Resolve(queued, url => url == AlbumUrl ? album : url == SnowUrl ? trackPage : null);
        Assert.Equal(47, full.Total);
        Assert.Equal("47", full.TotalDisplay);
        Assert.True(full.IsComplete);
        Assert.Empty(full.MissingPages(queued));
        Assert.Equal("$10", full.AlbumPrice!.Display);
        Assert.Equal("€1", full.TrackPrice!.Display);
    }

    [Fact]
    public void PlusWhenAlbumHasMoreThanShown()
    {
        var album = BandcampParser.ParseTralbum(AlbumHtml(80, more: true), AlbumUrl, Now);
        var trackPage = BandcampParser.ParseTralbum(TrackHtml(3), SnowUrl, Now);
        var d = TrackDetailsResolver.Resolve(album.Tracks[0], url => url == AlbumUrl ? album : trackPage);
        Assert.Equal("83+", d.TotalDisplay);
        Assert.True(d.TotalSortKey > 83);
    }

    [Fact]
    public void StandaloneSingleHasNoAlbum()
    {
        var single = new TrackInfo { TrackId = 5, TrackUrl = "https://x.bandcamp.com/track/single", SourcePageUrl = "https://x.bandcamp.com/track/single" };
        var d = TrackDetailsResolver.Resolve(single, _ => null);
        Assert.False(d.HasAlbum);
        Assert.Equal("…", d.TotalDisplay);
        Assert.Equal(["https://x.bandcamp.com/track/single"], d.MissingPages(single));
    }

    [Fact]
    public void AlbumOfSingleIsDiscoveredFromTrackPage()
    {
        // 曲ページの URL から追加した曲: 最初はアルバム不明 → 曲ページ取得後にアルバムページが未取得として出てくる
        var trackPage = BandcampParser.ParseTralbum(TrackHtml(2), SnowUrl, Now);
        var queued = trackPage.Tracks[0] with { AlbumUrl = null, AlbumId = null };
        var d = TrackDetailsResolver.Resolve(queued, url => url == SnowUrl ? trackPage : null);
        Assert.True(d.HasAlbum);
        Assert.Equal([AlbumUrl], d.MissingPages(queued));
    }

    [Fact]
    public void OldQueueEntriesFallBackToSourceAlbumPage()
    {
        var old = new TrackInfo { TrackId = 1, TrackUrl = SnowUrl, SourcePageUrl = AlbumUrl };
        Assert.Equal(AlbumUrl, TrackDetailsResolver.AlbumUrlOf(old));
    }

    [Fact]
    public void PurchasedByAlbumOrTrack()
    {
        var album = BandcampParser.ParseTralbum(AlbumHtml(1), AlbumUrl, Now);
        var track = album.Tracks[0];
        var d = TrackDetailsResolver.Resolve(track, url => url == AlbumUrl ? album : null);
        Assert.Equal(PurchaseState.Album, TrackDetailsResolver.Purchased(track, d, new HashSet<long> { 2189607369L }, new HashSet<long>()));
        Assert.Equal(PurchaseState.Track, TrackDetailsResolver.Purchased(track, d, new HashSet<long>(), new HashSet<long> { 1836575030L }));
        Assert.Equal(PurchaseState.None, TrackDetailsResolver.Purchased(track, d, new HashSet<long>(), new HashSet<long>()));
    }

    [Fact]
    public void ParsesFanPageAndCollectionApi()
    {
        var blob = Attr(new
        {
            fan_data = new { fan_id = 1042131L, username = "cooki" },
            collection_data = new { item_count = 165, last_token = "1787408010:4051346330:a::", batch_size = 20 },
            item_cache = new
            {
                collection = new Dictionary<string, object>
                {
                    ["p3544834753"] = new { item_id = 3544834753L, item_type = "package", tralbum_id = 4257485151L, tralbum_type = "a" },
                    ["t221885243"] = new { item_id = 221885243L, item_type = "track", tralbum_id = 221885243L, tralbum_type = "t" },
                },
            },
        });
        var fan = BandcampParser.ParseFanPage($"""<html><body><div id="pagedata" data-blob="{blob}"></div></body></html>""");
        Assert.Equal(1042131L, fan.FanId);
        Assert.Equal(165, fan.ItemCount);
        Assert.Equal("1787408010:4051346330:a::", fan.LastToken);
        Assert.Contains(new CollectionItem(ItemType.Album, 4257485151L), fan.Items);
        Assert.Contains(new CollectionItem(ItemType.Track, 221885243L), fan.Items);

        var (items, more, token) = BandcampParser.ParseCollectionItems("""
            {"more_available":true,"last_token":"next","items":[{"tralbum_id":1,"tralbum_type":"a"},{"tralbum_id":2,"tralbum_type":"t"}]}
            """);
        Assert.Equal(2, items.Count);
        Assert.True(more);
        Assert.Equal("next", token);
    }

    [Theory]
    [InlineData("aomoriringo", "aomoriringo")]
    [InlineData("@aomoriringo", "aomoriringo")]
    [InlineData("https://bandcamp.com/aomoriringo", "aomoriringo")]
    [InlineData("bandcamp.com/aomoriringo/wishlist?from=menubar", "aomoriringo")]
    [InlineData("https://ghostly.bandcamp.com/", null)]
    [InlineData("not a name", null)]
    [InlineData("", null)]
    public void ParsesFanUsername(string input, string? expected)
    {
        Assert.Equal(expected, BandcampUrls.ParseFanUsername(input));
    }

    [Fact]
    public void FanPageWithoutDataThrows()
    {
        Assert.Throws<BandcampParseException>(() => BandcampParser.ParseFanPage("<html></html>"));
    }
}
