using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BandcampBenrify.Core;

public sealed record ResolveProgress(int Done, int Total, string? Current, string? Error = null, bool NoAudio = false);

/// <summary>
/// Bandcamp のデータ取得窓口。すべてのリクエストは <see cref="ThrottledHttpClient"/> を経由し、結果は <see cref="DiskCache"/> に保存する。
/// </summary>
public sealed class BandcampClient
{
    private const string TralbumNs = "tralbum";
    private const string DiscographyNs = "discography";

    private readonly ThrottledHttpClient _http;
    private readonly DiskCache _cache;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, Lazy<Task<TralbumPage>>> _inflight = new();

    public BandcampClient(ThrottledHttpClient http, DiskCache cache, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _cache = cache;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>アルバム / 曲ページのキャッシュ有効期間 (サポーター数・曲目)。</summary>
    public TimeSpan TralbumTtl { get; set; } = TimeSpan.FromDays(3);

    /// <summary>作品一覧ページのキャッシュ有効期間。</summary>
    public TimeSpan DiscographyTtl { get; set; } = TimeSpan.FromHours(12);

    /// <summary>正確なサポーター数を数えるとき、1 作品あたりに追加で送るリクエストの上限。</summary>
    public int ExactCountMaxRequestsPerItem { get; set; } = 30;

    /// <summary>正確なサポーター数を数えるときの 1 リクエストあたりの取得件数。</summary>
    public int ExactCountPageSize { get; set; } = 100;

    public TralbumPage? TryGetCachedTralbum(string url) =>
        _cache.TryGet<TralbumPage>(TralbumNs, BandcampUrls.Normalize(url))?.Value;

    /// <summary>有効期限内のキャッシュがあれば返す (リクエストは送らない)。</summary>
    public TralbumPage? TryGetFreshTralbum(string url) =>
        _cache.TryGetFresh<TralbumPage>(TralbumNs, BandcampUrls.Normalize(url), TralbumTtl, _now());

    /// <summary>リクエスト 1 件あたりの目安時間 (残り時間の見積もり用)。</summary>
    public TimeSpan EstimatedTimePerRequest => _http.MinInterval + TimeSpan.FromSeconds(0.5);

    public async Task<TralbumPage> GetTralbumAsync(string url, RequestPriority priority, bool forceRefresh = false, CancellationToken ct = default)
    {
        url = BandcampUrls.Normalize(url);
        if (!forceRefresh && _cache.TryGetFresh<TralbumPage>(TralbumNs, url, TralbumTtl, _now()) is { } cached)
        {
            return cached;
        }

        // 同じページへの同時要求は 1 回のリクエストにまとめる
        var key = url + (forceRefresh ? "#force" : "");
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<TralbumPage>>(() => FetchTralbumAsync(url, priority, ct)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 別の呼び出し元がキャンセルした共有タスクだった場合は自分でやり直す
            return await FetchTralbumAsync(url, priority, ct).ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<TralbumPage>>>(key, lazy));
        }
    }

    private async Task<TralbumPage> FetchTralbumAsync(string url, RequestPriority priority, CancellationToken ct)
    {
        var result = await _http.GetAsync(url, priority, ct).ConfigureAwait(false);
        var page = BandcampParser.ParseTralbum(result.Body, result.FinalUri.ToString(), _now());

        // 以前に数えた正確なサポーター数は、ページ再取得で失わないよう引き継ぐ
        var previous = _cache.TryGet<TralbumPage>(TralbumNs, url)?.Value;
        if (previous?.Supporters.ExactCount is int exact && page.Supporters.HasMore && exact >= page.Supporters.VisibleCount)
        {
            page = page with { Supporters = page.Supporters with { ExactCount = exact } };
        }

        Store(url, page);
        return page;
    }

    private void Store(string requestedUrl, TralbumPage page)
    {
        var now = _now();
        _cache.Set(TralbumNs, requestedUrl, page, now);
        var canonical = BandcampUrls.Normalize(page.Url);
        if (canonical != requestedUrl)
        {
            _cache.Set(TralbumNs, canonical, page, now);
        }
    }

    /// <summary>アーティスト / レーベルの作品一覧を取得する (リクエスト 1 回)。</summary>
    public async Task<Discography> GetDiscographyAsync(string url, RequestPriority priority, bool forceRefresh = false, CancellationToken ct = default)
    {
        var musicUrl = BandcampUrls.MusicPage(url);
        if (!forceRefresh && _cache.TryGetFresh<Discography>(DiscographyNs, musicUrl, DiscographyTtl, _now()) is { } cached)
        {
            return cached;
        }

        var result = await _http.GetAsync(musicUrl, priority, ct).ConfigureAwait(false);
        var now = _now();
        Discography disco;
        if (BandcampParser.ContainsMusicGrid(result.Body))
        {
            disco = BandcampParser.ParseDiscography(result.Body, musicUrl, now);
        }
        else if (BandcampParser.ContainsTralbum(result.Body))
        {
            // 作品が 1 つしかないアーティストは /music がそのアルバムに飛ぶ
            var page = BandcampParser.ParseTralbum(result.Body, result.FinalUri.ToString(), now);
            Store(BandcampUrls.Normalize(page.Url), page);
            disco = new Discography
            {
                BandName = page.Artist,
                Url = musicUrl,
                FetchedAt = now,
                Items =
                [
                    new DiscographyItem
                    {
                        Type = page.Type,
                        Id = page.Id,
                        Title = page.Title,
                        Artist = page.Artist,
                        Url = page.Url,
                        ArtUrl = BandcampUrls.ArtUrl(page.ArtId, ArtSize.Small),
                    },
                ],
            };
        }
        else
        {
            throw new BandcampParseException("作品一覧が見つかりませんでした。Bandcamp のアーティスト / レーベルの URL か確認してください。");
        }

        _cache.Set(DiscographyNs, musicUrl, disco, now);
        return disco;
    }

    /// <summary>
    /// URL (曲 / アルバム / アーティスト・レーベル) に含まれる再生可能な曲を順に返す。
    /// レーベルの場合は作品ごとにページを取得するので時間がかかる (キャッシュ済みのものは即時)。
    /// </summary>
    public async IAsyncEnumerable<TrackInfo> ResolveTracksAsync(
        string url,
        IProgress<ResolveProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var kind = BandcampUrls.Classify(url);
        if (kind is UrlKind.Album or UrlKind.Track)
        {
            progress?.Report(new ResolveProgress(0, 1, url));
            var page = await GetTralbumAsync(url, RequestPriority.Interactive, ct: ct).ConfigureAwait(false);
            progress?.Report(new ResolveProgress(1, 1, page.Title));
            foreach (var t in page.Tracks.Where(t => t.StreamUrl is not null))
            {
                yield return t;
            }

            yield break;
        }

        var disco = await GetDiscographyAsync(url, RequestPriority.Interactive, ct: ct).ConfigureAwait(false);
        var total = disco.Items.Count;
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = disco.Items[i];
            progress?.Report(new ResolveProgress(i, total, item.Title));

            TralbumPage? page = null;
            string? error = null;
            try
            {
                page = await GetTralbumAsync(item.Url, RequestPriority.Background, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException)
            {
                error = $"{item.Title}: {ex.Message}";
            }

            progress?.Report(new ResolveProgress(i + 1, total, item.Title, error, NoAudio: page is not null && !page.Tracks.Any(t => t.StreamUrl is not null)));
            if (page is null)
            {
                continue;
            }

            foreach (var t in page.Tracks.Where(t => t.StreamUrl is not null))
            {
                yield return t;
            }
        }
    }

    /// <summary>
    /// 再生用のストリーム URL を返す。キャッシュの URL が期限切れならページを取り直す。再生できない曲は null。
    /// </summary>
    public async Task<string?> GetStreamUrlAsync(TrackInfo track, bool forceRefresh, CancellationToken ct = default)
    {
        var page = await GetTralbumAsync(track.SourcePageUrl, RequestPriority.Interactive, forceRefresh, ct).ConfigureAwait(false);
        var found = page.Tracks.FirstOrDefault(t => t.TrackId == track.TrackId);
        var stream = found?.StreamUrl;

        var expired = stream is null || StreamUrls.IsExpired(stream, page.FetchedAt, _now());
        if (expired && !forceRefresh)
        {
            return await GetStreamUrlAsync(track, forceRefresh: true, ct).ConfigureAwait(false);
        }

        return expired ? null : stream;
    }

    /// <summary>
    /// ページに載りきらないサポーターを Bandcamp の「もっと見る」と同じ API でページングして数える。
    /// 1 作品あたり最大 <see cref="ExactCountMaxRequestsPerItem"/> 回まで。数えきれなければ「N+」のまま返す。
    /// </summary>
    public async Task<TralbumPage> CountSupportersAsync(TralbumPage page, CancellationToken ct = default)
    {
        var s = page.Supporters;
        if (!s.HasMore)
        {
            return page;
        }

        var origin = BandcampUrls.Origin(page.Url);
        var ids = new List<long>(s.FanIds);
        var seen = new HashSet<long>(s.FanIds);
        var budget = ExactCountMaxRequestsPerItem;

        var thumbsDone = await PageAsync("thumbs", s.MoreThumbs, s.LastThumbToken).ConfigureAwait(false);
        var reviewsDone = await PageAsync("reviews", s.MoreReviews, s.LastReviewToken).ConfigureAwait(false);

        var updated = page with
        {
            Supporters = s with
            {
                FanIds = ids,
                MoreThumbs = !thumbsDone,
                MoreReviews = !reviewsDone,
                ExactCount = thumbsDone && reviewsDone ? ids.Count : null,
            },
        };
        Store(BandcampUrls.Normalize(page.Url), updated);
        return updated;

        async Task<bool> PageAsync(string kind, bool more, string? token)
        {
            while (more)
            {
                if (token is null || budget <= 0)
                {
                    return false;
                }

                budget--;
                var body = JsonSerializer.Serialize(new
                {
                    tralbum_type = page.Type == ItemType.Album ? "a" : "t",
                    tralbum_id = page.Id,
                    token,
                    count = ExactCountPageSize,
                });
                var result = await _http.PostJsonAsync($"{origin}/api/tralbumcollectors/2/{kind}", body, RequestPriority.Background, ct).ConfigureAwait(false);
                using var json = JsonDocument.Parse(result.Body);
                var root = json.RootElement;
                if (BandcampParser.GetBool(root, "error") == true)
                {
                    throw new HttpRequestException(BandcampParser.GetString(root, "error_message") ?? "Bandcamp API がエラーを返しました");
                }

                var before = ids.Count;
                token = BandcampParser.CollectFans(root, "results", ids, seen);
                more = BandcampParser.GetBool(root, "more_available") ?? false;
                if (ids.Count == before && more)
                {
                    return false; // 進まないなら打ち切る
                }
            }

            return true;
        }
    }
}
