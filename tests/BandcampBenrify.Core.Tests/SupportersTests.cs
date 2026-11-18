using System.Net;
using System.Text.Json;
using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

public sealed class SupportersTests : IDisposable
{
    private const string AlbumUrl = "https://x.bandcamp.com/album/a";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bcb-sup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>サポーター 1..total を持つ偽のアルバム。ページには先頭 shown 人、API は 1 回 perPage 人ずつ返す。</summary>
    private sealed class FakeAlbum(int total, int shown, int perPage, int newest = 0) : HttpMessageHandler
    {
        public List<string> Tokens { get; } = [];

        public int Newest { get; set; } = newest;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // 新しく買った人 (fan_id 1000+) はページの先頭に出る
            var fans = Enumerable.Range(1000, Newest).Concat(Enumerable.Range(1, total)).ToList();
            string body;
            if (request.Method == HttpMethod.Get)
            {
                var tralbum = new { item_type = "album", id = 7L, url = AlbumUrl, current = new { title = "A" }, trackinfo = Array.Empty<object>() };
                var blob = new
                {
                    thumbs = fans.Take(shown).Select(f => new { fan_id = f, token = $"t:{f}" }),
                    reviews = Array.Empty<object>(),
                    more_thumbs_available = fans.Count > shown,
                    more_reviews_available = false,
                };
                body = $"""
                    <html><script data-tralbum="{WebUtility.HtmlEncode(JsonSerializer.Serialize(tralbum))}"></script>
                    <div id="collectors-data" data-blob="{WebUtility.HtmlEncode(JsonSerializer.Serialize(blob))}"></div></html>
                    """;
            }
            else
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var token = json.RootElement.GetProperty("token").GetString()!;
                Tokens.Add(token);
                var after = int.Parse(token.Split(':')[1]);
                var start = fans.IndexOf(after) + 1;
                var batch = fans.Skip(start).Take(perPage).ToList();
                body = JsonSerializer.Serialize(new
                {
                    results = batch.Select(f => new { fan_id = f, token = $"t:{f}" }),
                    more_available = start + batch.Count < fans.Count,
                });
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request };
        }
    }

    private (BandcampClient, ThrottledHttpClient) Create(HttpMessageHandler handler)
    {
        var http = new ThrottledHttpClient(TimeSpan.FromSeconds(1), handler, (_, _) => Task.CompletedTask);
        return (new BandcampClient(http, new DiskCache(_dir)), http);
    }

    [Fact]
    public async Task CountsAllSupporters()
    {
        var fake = new FakeAlbum(total: 313, shown: 85, perPage: 100);
        var (client, http) = Create(fake);
        using (http)
        {
            var page = await client.GetTralbumAsync(AlbumUrl, RequestPriority.Interactive);
            Assert.Equal("85+", page.Supporters.Display);

            var counted = await client.CountSupportersAsync(page, maxRequests: 30);
            Assert.Equal("313", counted.Supporters.Display);
            Assert.Equal(3, fake.Tokens.Count);

            // キャッシュにも反映されている
            Assert.Equal("313", client.TryGetCachedTralbum(AlbumUrl)!.Supporters.Display);
        }
    }

    [Fact]
    public async Task StopsAtLimitAndResumes()
    {
        var fake = new FakeAlbum(total: 500, shown: 80, perPage: 100);
        var (client, http) = Create(fake);
        using (http)
        {
            var page = await client.GetTralbumAsync(AlbumUrl, RequestPriority.Interactive);
            var partial = await client.CountSupportersAsync(page, maxRequests: 2);
            Assert.Equal("280+", partial.Supporters.Display);

            var rest = await client.CountSupportersAsync(client.TryGetCachedTralbum(AlbumUrl)!, maxRequests: 30);
            Assert.Equal("500", rest.Supporters.Display);
            Assert.Equal(["t:80", "t:180", "t:280", "t:380", "t:480"], fake.Tokens);
        }
    }

    [Fact]
    public async Task OldCacheWithoutTokenRefetchesPageFirst()
    {
        var fake = new FakeAlbum(total: 150, shown: 80, perPage: 100);
        var (client, http) = Create(fake);
        using (http)
        {
            var page = await client.GetTralbumAsync(AlbumUrl, RequestPriority.Interactive);
            var old = page with { Supporters = page.Supporters with { LastThumbToken = null } };

            var counted = await client.CountSupportersAsync(old, maxRequests: 30);
            Assert.Equal("150", counted.Supporters.Display);
            Assert.Equal(["t:80"], fake.Tokens);
        }
    }

    [Fact]
    public async Task RefetchKeepsCountedSupportersAndAddsNewOnes()
    {
        var fake = new FakeAlbum(total: 200, shown: 80, perPage: 100);
        var (client, http) = Create(fake);
        using (http)
        {
            var page = await client.GetTralbumAsync(AlbumUrl, RequestPriority.Interactive);
            await client.CountSupportersAsync(page, maxRequests: 30);

            // 3 人新しく買った後にページを取り直しても、数えた 200 人は失わず 203 人になる
            fake.Newest = 3;
            var refetched = await client.GetTralbumAsync(AlbumUrl, RequestPriority.Interactive, forceRefresh: true);
            Assert.Equal("203", refetched.Supporters.Display);
        }
    }
}
