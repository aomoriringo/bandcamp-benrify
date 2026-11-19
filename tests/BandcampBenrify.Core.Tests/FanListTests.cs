using System.Net;
using System.Text.Json;
using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

/// <summary>「探す」で開くほかのファンのコレクション / ウィッシュリスト。</summary>
public sealed class FanListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bcb-fan-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://bandcamp.com/mosa_o", "mosa_o", FanListKind.Collection)]
    [InlineData("bandcamp.com/mosa_o/", "mosa_o", FanListKind.Collection)]
    [InlineData("https://bandcamp.com/mosa_o/wishlist", "mosa_o", FanListKind.Wishlist)]
    [InlineData("https://www.bandcamp.com/mosa_o/wishlist?from=menubar", "mosa_o", FanListKind.Wishlist)]
    public void ParsesFanListUrls(string url, string user, FanListKind kind)
    {
        Assert.Equal((user, kind), BandcampUrls.ParseFanList(url));
    }

    [Theory]
    [InlineData("https://ghostly.bandcamp.com")]
    [InlineData("https://ghostly.bandcamp.com/album/x")]
    [InlineData("https://bandcamp.com/discover")]
    [InlineData("https://bandcamp.com/tag/ambient")]
    [InlineData("https://bandcamp.com/mosa_o/followers")]
    [InlineData("https://bandcamp.com/")]
    public void OtherUrlsAreNotFanLists(string url)
    {
        Assert.Null(BandcampUrls.ParseFanList(url));
    }

    private static object Item(string type, long id) => new
    {
        tralbum_id = id,
        tralbum_type = type,
        item_title = $"Title {id}",
        band_name = $"Band {id}",
        item_url = $"https://b{id}.bandcamp.com/{(type == "a" ? "album" : "track")}/x",
        item_art_id = 12345L,
    };

    /// <summary>ウィッシュリスト全体 (新しい順) を持つ偽の Bandcamp。ファンページは先頭 2 件、API は 2 件ずつ返す。</summary>
    private sealed class FakeFan(List<(string Type, long Id)> wishlist) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<(string Type, long Id)> Wishlist { get; set; } = wishlist;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body;
            if (request.Method == HttpMethod.Get)
            {
                Requests.Add(request.RequestUri!.AbsolutePath);
                var blob = new
                {
                    fan_data = new { fan_id = 42, username = "someone", name = "Some One" },
                    collection_data = new { item_count = 999, last_token = "c:2" },
                    wishlist_data = new { item_count = Wishlist.Count, last_token = "tok:2" },
                    item_cache = new
                    {
                        // グッズ (URL なし) は除かれる
                        wishlist = Wishlist.Take(2).Select(i => (i.Type + i.Id, Item(i.Type, i.Id)))
                            .Append(("p1", (object)new { tralbum_id = 1L, tralbum_type = "p" }))
                            .ToDictionary(p => p.Item1, p => p.Item2),
                    },
                };
                body = $"<html><div id=\"pagedata\" data-blob=\"{WebUtility.HtmlEncode(JsonSerializer.Serialize(blob))}\"></div></html>";
            }
            else
            {
                Assert.EndsWith("/wishlist_items", request.RequestUri!.AbsolutePath);
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var token = json.RootElement.GetProperty("older_than_token").GetString()!;
                Requests.Add(token);
                var start = int.Parse(token.Split(':')[1]);
                var batch = Wishlist.Skip(start).Take(2).ToList();
                var end = start + batch.Count;
                body = JsonSerializer.Serialize(new
                {
                    items = batch.Select(i => Item(i.Type, i.Id)),
                    more_available = end < Wishlist.Count,
                    last_token = $"tok:{end}",
                });
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request };
        }
    }

    private (BandcampClient Client, FakeFan Fake, ThrottledHttpClient Http) Create(int count)
    {
        var fake = new FakeFan(Enumerable.Range(1, count).Select(i => (i % 3 == 0 ? "t" : "a", (long)(1000 - i))).ToList());
        var http = new ThrottledHttpClient(TimeSpan.FromSeconds(1), fake, (_, _) => Task.CompletedTask);
        return (new BandcampClient(http, new DiskCache(_dir), () => _now), fake, http);
    }

    [Fact]
    public async Task OpensWithFirstItemsOnly()
    {
        var (client, fake, http) = Create(7);
        using (http)
        {
            var list = await client.GetFanListAsync("someone", FanListKind.Wishlist);
            Assert.Equal("Some One", list.FanName);
            Assert.Equal(7, list.ItemCount);
            Assert.Equal([999L, 998L], list.Items.Select(i => i.Id));
            Assert.Equal("Title 999", list.Items[0].Title);
            Assert.Equal("Band 999", list.Items[0].Artist);
            Assert.Equal("https://b999.bandcamp.com/album/x", list.Items[0].Url);
            Assert.Equal("https://f4.bcbits.com/img/a0000012345_7.jpg", list.Items[0].ArtUrl);
            Assert.True(list.HasMore);
            Assert.Equal(["/someone/wishlist"], fake.Requests);
        }
    }

    [Fact]
    public async Task LoadsMoreUpToTheLimitAndThenTheRest()
    {
        var (client, fake, http) = Create(7);
        using (http)
        {
            var list = await client.GetFanListAsync("someone", FanListKind.Wishlist);
            var more = await client.LoadMoreFanListAsync(list, maxRequests: 1);
            Assert.Equal(4, more.Items.Count);
            Assert.True(more.HasMore);

            var rest = await client.LoadMoreFanListAsync(more, maxRequests: 10);
            Assert.Equal(7, rest.Items.Count);
            Assert.False(rest.HasMore);
            Assert.Equal(["/someone/wishlist", "tok:2", "tok:4", "tok:6"], fake.Requests);
        }
    }

    [Fact]
    public async Task ReopeningUsesCacheAndKeepsLoadedItems()
    {
        var (client, fake, http) = Create(7);
        using (http)
        {
            var list = await client.GetFanListAsync("someone", FanListKind.Wishlist);
            await client.LoadMoreFanListAsync(list, maxRequests: 1);

            // キャッシュの有効期間内なら取りに行かず、読んだ続きも残っている
            var reopened = await client.GetFanListAsync("someone", FanListKind.Wishlist);
            Assert.Equal(4, reopened.Items.Count);
            Assert.Single(fake.Requests, r => r.StartsWith('/'));

            // 期限が切れたらページを取り直し、新しく増えた分を先頭に、読んだ続きと続きの位置は引き継ぐ
            _now += TimeSpan.FromDays(1);
            fake.Wishlist = [("a", 5000), .. fake.Wishlist];
            var refreshed = await client.GetFanListAsync("someone", FanListKind.Wishlist);
            Assert.Equal([5000L, 999L, 998L, 997L, 996L], refreshed.Items.Select(i => i.Id));
            Assert.Equal(8, refreshed.ItemCount);
            Assert.True(refreshed.HasMore);
        }
    }
}
