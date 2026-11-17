using System.Net;
using System.Text.Json;
using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

public class ParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    private static string Attr(object o) => WebUtility.HtmlEncode(JsonSerializer.Serialize(o));

    private static string AlbumHtml(bool moreThumbs, int thumbs = 3, int reviews = 1)
    {
        var tralbum = new
        {
            current = new { title = "Mount Crystal", release_date = "11 Jun 2025 00:00:00 GMT" },
            artist = "Joviale",
            item_type = "album",
            id = 2189607369L,
            art_id = 665037794L,
            album_release_date = "11 Jun 2025 00:00:00 GMT",
            url = "https://joviale.bandcamp.com/album/mount-crystal",
            trackinfo = new object[]
            {
                new { id = 1L, track_id = 1L, title = "The Mountain", artist = (string?)null, duration = 61.5, title_link = "/track/the-mountain", file = new Dictionary<string, string> { ["mp3-128"] = "https://t4.bcbits.com/stream/x/mp3-128/1?p=0&ts=1795011718&t=a" } },
                new { id = 2L, track_id = 2L, title = "Feat Song", artist = (string?)"Guest", duration = 200.0, title_link = "/track/feat-song", file = (Dictionary<string, string>?)null },
            },
        };
        var blob = new
        {
            thumbs = Enumerable.Range(1, thumbs).Select(i => new { fan_id = i, username = $"u{i}", token = $"tok{i}" }).ToArray(),
            reviews = Enumerable.Range(100, reviews).Select(i => new { fan_id = i, username = $"r{i}", why = "good", token = $"rtok{i}" })
                .Append(new { fan_id = 1, username = "u1", why = "dup", token = "rdup" }).ToArray(),
            more_thumbs_available = moreThumbs,
            more_reviews_available = false,
        };
        return $"""
            <html><head><script type="text/javascript" data-tralbum="{Attr(tralbum)}" data-embed="{Attr(new { album_title = "Mount Crystal" })}"></script></head>
            <body><div id="collectors-data" data-blob="{Attr(blob)}"></div></body></html>
            """;
    }

    [Fact]
    public void ParsesAlbumTracksAndSupporters()
    {
        var page = BandcampParser.ParseTralbum(AlbumHtml(moreThumbs: false), "https://joviale.bandcamp.com/album/mount-crystal?from=x", Now);

        Assert.Equal(ItemType.Album, page.Type);
        Assert.Equal("Mount Crystal", page.Title);
        Assert.Equal("Joviale", page.Artist);
        Assert.Equal(2189607369L, page.Id);
        Assert.Equal(new DateTimeOffset(2025, 6, 11, 0, 0, 0, TimeSpan.Zero), page.ReleaseDate);
        Assert.Equal(2, page.Tracks.Count);

        var t1 = page.Tracks[0];
        Assert.Equal("Joviale", t1.Artist); // null → アルバムアーティスト
        Assert.Equal("https://joviale.bandcamp.com/track/the-mountain", t1.TrackUrl);
        Assert.Equal("https://joviale.bandcamp.com/album/mount-crystal", t1.SourcePageUrl);
        Assert.NotNull(t1.StreamUrl);
        Assert.Equal("Mount Crystal", t1.AlbumTitle);

        Assert.Equal("Guest", page.Tracks[1].Artist);
        Assert.Null(page.Tracks[1].StreamUrl);

        // thumbs 3 + reviews 1 + 重複 1 → 4 人
        Assert.Equal(4, page.Supporters.Count);
        Assert.False(page.Supporters.HasMore);
        Assert.Equal("4", page.Supporters.Display);
    }

    [Fact]
    public void SupportersWithMoreShowPlus()
    {
        var page = BandcampParser.ParseTralbum(AlbumHtml(moreThumbs: true, thumbs: 80, reviews: 6), "https://x.bandcamp.com/album/a", Now);

        Assert.True(page.Supporters.HasMore);
        Assert.Equal("86+", page.Supporters.Display);
        Assert.True(page.Supporters.SortKey > 86);
    }

    [Fact]
    public void NoCollectorsMeansZero()
    {
        var html = AlbumHtml(false).Replace("collectors-data", "something-else");
        var page = BandcampParser.ParseTralbum(html, "https://x.bandcamp.com/album/a", Now);
        Assert.Equal(0, page.Supporters.Count);
        Assert.Equal("0", page.Supporters.Display);
    }

    [Fact]
    public void ThrowsOnNonTralbumPage()
    {
        Assert.Throws<BandcampParseException>(() => BandcampParser.ParseTralbum("<html></html>", "https://x.bandcamp.com/", Now));
    }

    [Fact]
    public void ParsesDiscographyGridAndClientItems()
    {
        var band = Attr(new { id = 1560012315L, name = "Ghostly International" });
        var clientItems = Attr(new object[]
        {
            new { art_id = 1605627515L, artist = "Kate Bollinger", band_id = 1L, id = 3377248023L, page_url = "https://katebollinger.bandcamp.com/album/songs?label=1&tab=music", title = "Songs", type = "album" },
            new { art_id = 1L, artist = "Dup", band_id = 1L, id = 3345549482L, page_url = "/album/dup", title = "Dup", type = "album" },
        });
        var html = $"""
            <html><head><title>Music | Ghostly International</title><script data-band="{band}"></script></head><body>
            <ol id="music-grid" class="music-grid" data-client-items="{clientItems}">
              <li data-item-id="album-3345549482" data-band-id="2444076357" class="music-grid-item square">
                <a href="https://shop.christopherwillits.com/album/new-moon-remixes?label=1560012315&amp;tab=music">
                  <div class="art"><img src="https://f4.bcbits.com/img/a3201697339_2.jpg" alt=""></div>
                  <p class="title">
                    New Moon   Remixes
                    <br><span class="artist-override">
                    Christopher Willits
                    </span>
                  </p>
                </a>
              </li>
              <li data-item-id="track-42" class="music-grid-item">
                <a href="/track/single"><div class="art"><img class="lazy" src="/img/0.gif" data-original="https://f4.bcbits.com/img/a1_2.jpg"></div>
                <p class="title">Single</p></a>
              </li>
            </ol></body></html>
            """;

        var disco = BandcampParser.ParseDiscography(html, "https://ghostly.bandcamp.com/music", Now);

        Assert.Equal("Ghostly International", disco.BandName);
        Assert.Equal(3, disco.Items.Count);

        var first = disco.Items[0];
        Assert.Equal(ItemType.Album, first.Type);
        Assert.Equal("New Moon Remixes", first.Title);
        Assert.Equal("Christopher Willits", first.Artist);
        Assert.Equal("https://shop.christopherwillits.com/album/new-moon-remixes", first.Url);

        var second = disco.Items[1];
        Assert.Equal(ItemType.Track, second.Type);
        Assert.Equal("Ghostly International", second.Artist);
        Assert.Equal("https://ghostly.bandcamp.com/track/single", second.Url);
        Assert.Equal("https://f4.bcbits.com/img/a1_2.jpg", second.ArtUrl);

        Assert.Equal("Kate Bollinger", disco.Items[2].Artist);
        Assert.Equal("https://katebollinger.bandcamp.com/album/songs", disco.Items[2].Url);
    }

    [Theory]
    [InlineData("https://ghostly.bandcamp.com/album/x?from=y", UrlKind.Album)]
    [InlineData("ghostly.bandcamp.com/track/abc", UrlKind.Track)]
    [InlineData("https://ghostly.bandcamp.com/", UrlKind.Band)]
    [InlineData("https://ghostly.bandcamp.com/music", UrlKind.Band)]
    [InlineData("https://shop.example.com/album/foo", UrlKind.Album)]
    public void ClassifiesUrls(string url, UrlKind expected)
    {
        Assert.Equal(expected, BandcampUrls.Classify(url));
    }

    [Fact]
    public void NormalizesUrls()
    {
        Assert.Equal("https://ghostly.bandcamp.com/album/x", BandcampUrls.Normalize(" http://Ghostly.bandcamp.com/album/x/?a=1#t "));
        Assert.Equal("https://ghostly.bandcamp.com/music", BandcampUrls.MusicPage("https://ghostly.bandcamp.com/album/x"));
    }

    [Fact]
    public void DetectsExpiredStreamUrls()
    {
        var url = "https://t4.bcbits.com/stream/x/mp3-128/1?p=0&ts=" + Now.AddHours(5).ToUnixTimeSeconds() + "&t=a";
        Assert.False(StreamUrls.IsExpired(url, Now, Now));
        Assert.True(StreamUrls.IsExpired(url, Now, Now.AddHours(5)));
        Assert.True(StreamUrls.IsExpired("https://example.com/no-ts", Now, Now.AddHours(1)));
        Assert.False(StreamUrls.IsExpired("https://example.com/no-ts", Now, Now.AddMinutes(10)));
    }
}
