using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BandcampBenrify.Core;

public sealed record ResolveProgress(int Done, int Total, string? Current, string? Error = null, bool NoAudio = false);

/// <summary>キャッシュの種類 (設定画面で種類ごとに使用量を出し、消せるようにする)。</summary>
public enum CacheKind
{
    /// <summary>アルバム / 曲ページ (サポーター数・値段・曲目。数えた正確なサポーター数も含む)。</summary>
    Pages,

    /// <summary>レーベル / アーティストの作品一覧。</summary>
    Discographies,

    /// <summary>自分のコレクション (購入済みマーク)。</summary>
    Collection,
}

/// <summary>
/// Bandcamp のデータ取得窓口。すべてのリクエストは <see cref="ThrottledHttpClient"/> を経由し、結果は <see cref="DiskCache"/> に保存する。
/// </summary>
public sealed class BandcampClient
{
    // 保存形式を変えたら名前を変える (古いキャッシュは使わずに取り直す)
    private const string TralbumNs = "tralbum-v2";
    private const string DiscographyNs = "discography";
    private const string CollectionNs = "collection";

    // 古い版の保存形式 (もう読まない)。作品ページのキャッシュとして数え、一緒に消す
    private const string LegacyTralbumNs = "tralbum";

    private readonly ThrottledHttpClient _http;
    private readonly DiskCache _cache;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, Lazy<Task<TralbumPage>>> _inflight = new();

    // ディスクキャッシュの前に置くメモリキャッシュ (キューの各曲から同じアルバムページを何度も引くため)
    private readonly ConcurrentDictionary<string, CacheEntry<TralbumPage>?> _memory = new();

    public BandcampClient(ThrottledHttpClient http, DiskCache cache, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _cache = cache;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>アルバム / 曲ページのキャッシュ有効期間 (サポーター数・曲目・値段)。</summary>
    public TimeSpan TralbumTtl { get; set; } = TimeSpan.FromDays(3);

    /// <summary>作品一覧ページのキャッシュ有効期間。</summary>
    public TimeSpan DiscographyTtl { get; set; } = TimeSpan.FromHours(12);

    /// <summary>キャッシュ済みのページ (期限切れでも返す)。リクエストは送らない。</summary>
    public TralbumPage? TryGetCachedTralbum(string url) => GetEntry(url)?.Value;

    /// <summary>有効期限内のキャッシュがあれば返す (リクエストは送らない)。</summary>
    public TralbumPage? TryGetFreshTralbum(string url) =>
        GetEntry(url) is { } e && _now() - e.SavedAt <= TralbumTtl ? e.Value : null;

    /// <summary>リクエスト 1 件あたりの目安時間 (残り時間の見積もり用)。</summary>
    public TimeSpan EstimatedTimePerRequest => _http.MinInterval + TimeSpan.FromSeconds(0.5);

    private static string[] NamespacesOf(CacheKind kind) => kind switch
    {
        CacheKind.Pages => [TralbumNs, LegacyTralbumNs],
        CacheKind.Discographies => [DiscographyNs],
        _ => [CollectionNs],
    };

    /// <summary>種類ごとのキャッシュの使用量 (バイト)。</summary>
    public long CacheSize(CacheKind kind) => NamespacesOf(kind).Sum(_cache.SizeBytes);

    /// <summary>種類を指定してキャッシュを消す (ほかの種類は残す)。</summary>
    public void ClearCache(CacheKind kind)
    {
        foreach (var ns in NamespacesOf(kind))
        {
            _cache.Clear(ns);
        }

        if (kind == CacheKind.Pages)
        {
            _memory.Clear();
        }
    }

    private CacheEntry<TralbumPage>? GetEntry(string url)
    {
        var key = BandcampUrls.Normalize(url);
        return _memory.GetOrAdd(key, k => _cache.TryGet<TralbumPage>(TralbumNs, k));
    }

    public async Task<TralbumPage> GetTralbumAsync(string url, RequestPriority priority, bool forceRefresh = false, CancellationToken ct = default)
    {
        url = BandcampUrls.Normalize(url);
        if (!forceRefresh && TryGetFreshTralbum(url) is { } cached)
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

        // 以前に「もっと見る」で数えたサポーターは、ページを取り直しても失わないよう引き継ぐ
        if (TryGetCachedTralbum(url)?.Supporters is { } previous && previous.Count > page.Supporters.Count)
        {
            page = page with { Supporters = MergeSupporters(page.Supporters, previous) };
        }

        Store(url, page);
        return page;
    }

    /// <summary>新しく取ったページのサポーター (新しい人が先頭) に、以前数えた分を足す。続きのトークンは以前のものを使う。</summary>
    private static SupporterInfo MergeSupporters(SupporterInfo fresh, SupporterInfo previous)
    {
        var ids = new List<long>(fresh.FanIds);
        var seen = new HashSet<long>(ids);
        ids.AddRange(previous.FanIds.Where(seen.Add));
        return previous with { FanIds = ids };
    }

    /// <summary>
    /// ページに載りきらない (「N+」の) サポーターを、Bandcamp の「もっと見る」と同じ API で続きから数える。
    /// 最大 <paramref name="maxRequests"/> 回で打ち切り、途中経過を保存するので、次に呼ぶと続きから数える。
    /// </summary>
    /// <param name="progress">リクエストごとに、その時点の人数を通知する。</param>
    public async Task<TralbumPage> CountSupportersAsync(TralbumPage page, int maxRequests, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var s = page.Supporters;
        if (!s.HasMore)
        {
            return page;
        }

        // 古い形式のキャッシュには続きを数えるためのトークンが無いので、ページを 1 回取り直す
        if ((s.MoreThumbs && s.LastThumbToken is null) || (s.MoreReviews && s.LastReviewToken is null))
        {
            page = await GetTralbumAsync(page.Url, RequestPriority.Background, forceRefresh: true, ct).ConfigureAwait(false);
            s = page.Supporters;
            if (!s.HasMore)
            {
                return page;
            }
        }

        var origin = BandcampUrls.Origin(page.Url);
        var ids = new List<long>(s.FanIds);
        var seen = new HashSet<long>(ids);
        var budget = maxRequests;
        var (thumbToken, moreThumbs) = (s.LastThumbToken, s.MoreThumbs);
        var (reviewToken, moreReviews) = (s.LastReviewToken, s.MoreReviews);

        TralbumPage Snapshot() => page with
        {
            Supporters = s with
            {
                FanIds = [.. ids],
                LastThumbToken = thumbToken,
                MoreThumbs = moreThumbs,
                LastReviewToken = reviewToken,
                MoreReviews = moreReviews,
            },
        };

        try
        {
            (thumbToken, moreThumbs) = await PageAsync("thumbs", thumbToken, moreThumbs).ConfigureAwait(false);
            (reviewToken, moreReviews) = await PageAsync("reviews", reviewToken, moreReviews).ConfigureAwait(false);
        }
        finally
        {
            // 中止・失敗しても、そこまで数えた分は保存する
            Store(BandcampUrls.Normalize(page.Url), Snapshot());
        }

        return Snapshot();

        async Task<(string? Token, bool More)> PageAsync(string kind, string? token, bool more)
        {
            while (more && token is not null && budget > 0)
            {
                budget--;
                var body = JsonSerializer.Serialize(new
                {
                    tralbum_type = page.Type == ItemType.Album ? "a" : "t",
                    tralbum_id = page.Id,
                    token,
                    count = SupportersPageSize,
                });
                var result = await _http.PostJsonAsync($"{origin}/api/tralbumcollectors/2/{kind}", body, RequestPriority.Background, ct).ConfigureAwait(false);
                var before = ids.Count;
                var (next, moreAvailable) = BandcampParser.ParseCollectorsPage(result.Body, ids, seen);
                progress?.Report(ids.Count);
                if (ids.Count == before)
                {
                    return (token, false); // 進まなければ終わり (同じ所を何度も取らない)
                }

                (token, more) = (next ?? token, moreAvailable);
                if (kind == "thumbs")
                {
                    (thumbToken, moreThumbs) = (token, more);
                }
                else
                {
                    (reviewToken, moreReviews) = (token, more);
                }
            }

            return (token, more);
        }
    }

    /// <summary>サポーター一覧の 1 リクエストあたりの件数。</summary>
    public int SupportersPageSize { get; set; } = 100;

    private void Store(string requestedUrl, TralbumPage page)
    {
        var now = _now();
        var entry = new CacheEntry<TralbumPage>(requestedUrl, now, page);
        _cache.Set(TralbumNs, requestedUrl, page, now);
        _memory[requestedUrl] = entry;
        var canonical = BandcampUrls.Normalize(page.Url);
        if (canonical != requestedUrl)
        {
            _cache.Set(TralbumNs, canonical, page, now);
            _memory[canonical] = entry with { Key = canonical };
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
            progress?.Report(new ResolveProgress(1, 1, page.Title, NoAudio: !page.Tracks.Any(t => t.StreamUrl is not null)));
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

    // ---- コレクション ----

    public FanCollection? TryGetCachedCollection(string username) =>
        BandcampUrls.ParseFanUsername(username) is { } u ? _cache.TryGet<FanCollection>(CollectionNs, u.ToLowerInvariant())?.Value : null;

    /// <summary>
    /// ファンの公開コレクションを読み込む。Bandcamp の API は 1 回 20 件ずつしか返さないので、
    /// 初回は件数 / 20 回のリクエストが必要 (中止しても途中まで保存し、次回は続きから読み込む)。
    /// 2 回目以降は新しく買った分だけを読む (通常はファンページ 1 回で済む)。
    /// </summary>
    public async Task<FanCollection> GetCollectionAsync(string username, IProgress<CollectionProgress>? progress = null, CancellationToken ct = default)
    {
        username = BandcampUrls.ParseFanUsername(username)
            ?? throw new ArgumentException("設定で Bandcamp のプロフィール (https://bandcamp.com/ユーザー名) を入力してください。");
        var key = username.ToLowerInvariant();
        var existing = _cache.TryGet<FanCollection>(CollectionNs, key)?.Value;

        var result = await _http.GetAsync($"https://bandcamp.com/{username}", RequestPriority.Interactive, ct).ConfigureAwait(false);
        var fan = BandcampParser.ParseFanPage(result.Body);
        if (existing is not null && existing.FanId != fan.FanId)
        {
            existing = null;
        }

        var known = new HashSet<CollectionItem>(existing?.Items() ?? []);
        var items = new HashSet<CollectionItem>(known);
        items.UnionWith(fan.Items);
        progress?.Report(new CollectionProgress(items.Count, fan.ItemCount));

        // 1) 前回以降に買った分: 既知の作品に行き当たるまで新しい順に読む
        if (existing is not null && fan.Items.Count > 0 && !fan.Items.Any(known.Contains))
        {
            var token = fan.LastToken;
            while (token is not null)
            {
                var (batch, more, next) = await FetchCollectionBatchAsync(fan.FanId, token, ct).ConfigureAwait(false);
                items.UnionWith(batch);
                progress?.Report(new CollectionProgress(items.Count, fan.ItemCount));
                token = more && batch.Count > 0 && !batch.Any(known.Contains) ? next : null;
            }
        }

        // 2) 古い分: 初回はファンページの続きから、前回中止していればその続きから
        var resume = existing is null ? fan.LastToken : existing.ResumeToken;
        if (fan.ItemCount <= items.Count)
        {
            resume = null;
        }

        var requests = 0;
        try
        {
            while (resume is not null)
            {
                var (batch, more, next) = await FetchCollectionBatchAsync(fan.FanId, resume, ct).ConfigureAwait(false);
                items.UnionWith(batch);
                resume = more && batch.Count > 0 ? next : null;
                if (++requests % 10 == 0)
                {
                    // 途中経過を保存 (中止・異常終了しても続きから読める) し、先に印を付けられるよう通知
                    var partial = BuildCollection(username, fan, items, resume);
                    _cache.Set(CollectionNs, key, partial, partial.FetchedAt);
                    progress?.Report(new CollectionProgress(items.Count, fan.ItemCount, partial));
                }
                else
                {
                    progress?.Report(new CollectionProgress(items.Count, fan.ItemCount));
                }
            }
        }
        catch (OperationCanceledException)
        {
            var partial = BuildCollection(username, fan, items, resume);
            _cache.Set(CollectionNs, key, partial, partial.FetchedAt);
            throw;
        }

        var collection = BuildCollection(username, fan, items, resumeToken: null);
        _cache.Set(CollectionNs, key, collection, collection.FetchedAt);
        return collection;
    }

    private FanCollection BuildCollection(string username, FanPage fan, HashSet<CollectionItem> items, string? resumeToken) => new()
    {
        Username = username,
        FanId = fan.FanId,
        ItemCount = fan.ItemCount,
        AlbumIds = items.Where(i => i.Type == ItemType.Album).Select(i => i.Id).ToList(),
        TrackIds = items.Where(i => i.Type == ItemType.Track).Select(i => i.Id).ToList(),
        ResumeToken = resumeToken,
        FetchedAt = _now(),
    };

    private async Task<(List<CollectionItem> Items, bool More, string? Next)> FetchCollectionBatchAsync(long fanId, string token, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { fan_id = fanId, older_than_token = token, count = 20 });
        var page = await _http.PostJsonAsync("https://bandcamp.com/api/fancollection/1/collection_items", body, RequestPriority.Background, ct).ConfigureAwait(false);
        return BandcampParser.ParseCollectionItems(page.Body);
    }

    /// <summary>コレクション 1 リクエストあたりの件数 (Bandcamp 側の上限)。</summary>
    public const int CollectionBatchSize = 20;
}
