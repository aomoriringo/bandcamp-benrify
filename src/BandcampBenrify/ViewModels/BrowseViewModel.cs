using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Input;
using BandcampBenrify.Core;
using BandcampBenrify.Infrastructure;

namespace BandcampBenrify.ViewModels;

/// <summary>「探す」の左側の 1 行 (アルバム / 曲 / アーティスト・レーベル)。</summary>
public sealed class BrowseItemViewModel : ObservableObject
{
    private TralbumPage? _page;
    private PurchaseState _purchase;
    private bool _isLoading;
    private string? _error;

    public BrowseItemViewModel(SearchKind kind, long id, string title, string artist, string url, string? artUrl)
    {
        Kind = kind;
        Id = id;
        Title = title;
        Artist = artist;
        Url = url;
        ArtUrl = string.IsNullOrEmpty(artUrl) ? null : artUrl;
    }

    public SearchKind Kind { get; }

    public long Id { get; private set; }

    public string Title { get; }

    public string Artist { get; }

    public string Url { get; }

    public string? ArtUrl { get; }

    public bool IsRelease => Kind != SearchKind.Band;

    public string KindLabel => Kind switch
    {
        SearchKind.Album => "アルバム",
        SearchKind.Track => "曲",
        _ => "アーティスト",
    };

    public TralbumPage? Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                if (value is not null && Id == 0)
                {
                    Id = value.Id;
                }

                foreach (var p in new[] { nameof(Label), nameof(SupText), nameof(SupSort), nameof(PriceText), nameof(PriceSort), nameof(ReleaseDate), nameof(ReleaseDateText), nameof(TrackCount), nameof(StateText), nameof(HasAudio), nameof(CheckedText), nameof(IsAllChecked) })
                {
                    OnPropertyChanged(p);
                }
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (Set(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    /// <summary>レーベル名 (作品ページ取得後に分かる。自主リリースは空)。</summary>
    public string Label => Page?.Label ?? "";

    public string SupText => !IsRelease ? "" : Page?.Supporters.Display ?? "…";

    public double SupSort => Page?.Supporters.SortKey ?? -1;

    public string PriceText => !IsRelease ? "" : Page is null ? "…" : Page.Price?.Display ?? "—";

    public double PriceSort => Page?.Price is { } p ? (double)p.Amount : -1;

    public DateTimeOffset? ReleaseDate => Page?.ReleaseDate;

    public string ReleaseDateText => ReleaseDate?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";

    public int? TrackCount => Page?.Tracks.Count;

    public bool HasAudio => Page?.Tracks.Any(t => t.StreamUrl is not null) ?? true;

    public string StateText => IsLoading ? "取得中…" : Error is not null ? "エラー: " + Error : Page is not null && !HasAudio ? "試聴不可" : "";

    public PurchaseState Purchase
    {
        get => _purchase;
        set
        {
            if (Set(ref _purchase, value))
            {
                OnPropertyChanged(nameof(PurchaseText));
                OnPropertyChanged(nameof(IsPurchased));
            }
        }
    }

    public bool IsPurchased => Purchase != PurchaseState.None;

    public string PurchaseText => Purchase switch
    {
        PurchaseState.Album => "✓ アルバム",
        PurchaseState.Track => "✓ 曲",
        _ => "",
    };

    private int _checkedTracks;

    /// <summary>チェック済みの曲数。曲目が分かっている (ページ取得済みの) 作品だけ。</summary>
    public int CheckedTracks
    {
        get => _checkedTracks;
        set
        {
            if (Set(ref _checkedTracks, value))
            {
                OnPropertyChanged(nameof(CheckedText));
                OnPropertyChanged(nameof(IsAllChecked));
            }
        }
    }

    /// <summary>全曲チェック済みか。</summary>
    public bool IsAllChecked => TrackCount is > 0 && CheckedTracks >= TrackCount;

    public string CheckedText => CheckedTracks == 0 || TrackCount is not > 0 ? ""
        : IsAllChecked ? (TrackCount == 1 ? "✓" : "✓ 全曲")
        : $"{CheckedTracks}/{TrackCount} 曲";
}

/// <summary>
/// 「探す」タブ: レーベル / アーティストの作品一覧や検索結果から、作品と曲を選んでキューに入れる。
/// </summary>
public sealed class BrowseViewModel : ObservableObject
{
    private readonly MainViewModel _host;
    private readonly BandcampClient _client;
    private readonly Stack<string> _history = new();
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _selectCts;
    private string? _current;
    private string _title = "";
    private string _filterText = "";
    private bool _hidePurchased;
    private bool _hideChecked;
    private BrowseItemViewModel? _selectedItem;
    private bool _fetchingReleases;
    private string _releaseInfoButtonText = "";
    private bool _isBusy;

    public BrowseViewModel(MainViewModel host, BandcampClient client)
    {
        _host = host;
        _client = client;

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        view.IsLiveSorting = true;
        foreach (var p in new[] { nameof(BrowseItemViewModel.SupSort), nameof(BrowseItemViewModel.PriceSort), nameof(BrowseItemViewModel.ReleaseDate), nameof(BrowseItemViewModel.TrackCount), nameof(BrowseItemViewModel.PurchaseText) })
        {
            view.LiveSortingProperties.Add(p);
        }

        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(BrowseItemViewModel.IsPurchased));
        view.LiveFilteringProperties.Add(nameof(BrowseItemViewModel.IsAllChecked));
        view.LiveSortingProperties.Add(nameof(BrowseItemViewModel.CheckedTracks));
        view.Filter = o => o is BrowseItemViewModel item && MatchesFilter(item);
        ItemsView = view;

        BackCommand = new AsyncCommand(BackAsync, () => _history.Count > 0);
        FetchReleaseInfoCommand = new AsyncCommand(FetchReleaseInfoAsync, () => PendingReleaseCount > 0 && !_fetchingReleases);
        OpenItemCommand = new AsyncCommand(p => p is BrowseItemViewModel { Kind: SearchKind.Band } b ? OpenAsync(b.Url) : Task.CompletedTask);
        AddReleasesCommand = new AsyncCommand(p => AddReleasesAsync(SelectedReleases(p)), p => SelectedReleases(p).Count > 0);
        AddCurrentReleaseCommand = new AsyncCommand(() => AddReleasesAsync(SelectedItem is { IsRelease: true } s ? [s] : []), () => SelectedItem is { IsRelease: true, Page: not null });
        AddTracksCommand = new RelayCommand(p => AddTracks(SelectedTracks(p)), p => SelectedTracks(p).Count > 0);
        PreviewCommand = new AsyncCommand(p => p is QueueItemViewModel { IsStreamable: true } t ? _host.PreviewAsync(t) : Task.CompletedTask);
        FetchTrackInfoCommand = new AsyncCommand(
            p => _host.FetchDetailsAsync(SelectedTracks(p) is { Count: > 0 } sel ? sel : Tracks.ToList()),
            _ => Tracks.Any(t => t.NeedsDetails) && !_host.IsFetchingDetails);
        OpenInBrowserCommand = new RelayCommand(p =>
        {
            var url = p switch
            {
                BrowseItemViewModel b => b.Url,
                QueueItemViewModel q => q.Track.TrackUrl,
                _ => SelectedItem?.Url,
            };
            if (url is not null)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        });
        UpdatePendingCount();
    }

    public ObservableCollection<BrowseItemViewModel> Items { get; } = [];

    public ICollectionView ItemsView { get; }

    /// <summary>選択中の作品の曲目。</summary>
    public ObservableCollection<QueueItemViewModel> Tracks { get; } = [];

    public ICommand BackCommand { get; }

    public ICommand FetchReleaseInfoCommand { get; }

    public ICommand OpenItemCommand { get; }

    public ICommand AddReleasesCommand { get; }

    public ICommand AddCurrentReleaseCommand { get; }

    public ICommand AddTracksCommand { get; }

    public ICommand PreviewCommand { get; }

    public ICommand FetchTrackInfoCommand { get; }

    public ICommand OpenInBrowserCommand { get; }

    /// <summary>曲目の右クリック「チェック済みにする」(キューと同じ記録)。</summary>
    public ICommand CheckSelectedCommand => _host.CheckSelectedCommand;

    public ICommand UncheckSelectedCommand => _host.UncheckSelectedCommand;

    /// <summary>全曲チェック済みの作品を一覧から隠す。</summary>
    public bool HideChecked
    {
        get => _hideChecked;
        set
        {
            if (Set(ref _hideChecked, value))
            {
                ItemsView.Refresh();
            }
        }
    }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public bool IsEmpty => Items.Count == 0 && !IsBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (Set(ref _filterText, value))
            {
                ItemsView.Refresh();
            }
        }
    }

    public bool HidePurchased
    {
        get => _hidePurchased;
        set
        {
            if (Set(ref _hidePurchased, value))
            {
                ItemsView.Refresh();
            }
        }
    }

    public int PendingReleaseCount { get; private set; }

    public string ReleaseInfoButtonText
    {
        get => _releaseInfoButtonText;
        private set => Set(ref _releaseInfoButtonText, value);
    }

    public BrowseItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Set(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelectedRelease));
                OnPropertyChanged(nameof(SelectedArtUrl));
                OnPropertyChanged(nameof(SelectedSummary));
                _ = LoadTracksAsync(value);
            }
        }
    }

    public bool HasSelectedRelease => SelectedItem is { IsRelease: true };

    public string? SelectedArtUrl => SelectedItem?.Page?.ArtId is long id ? BandcampUrls.ArtUrl(id, ArtSize.Medium) : SelectedItem?.ArtUrl;

    public string SelectedSummary
    {
        get
        {
            if (SelectedItem is not { IsRelease: true } s)
            {
                return "";
            }

            if (s.Page is not { } p)
            {
                return s.Error is null ? "読み込み中…" : "エラー: " + s.Error;
            }

            var parts = new List<string>();
            if (p.Label is { Length: > 0 } label)
            {
                parts.Add($"レーベル: {label}");
            }

            parts.Add($"サポーター {p.Supporters.Display}");
            if (p.Price is { } price)
            {
                parts.Add($"{(p.Type == ItemType.Album ? "アルバム" : "曲")} {price.Display}");
            }

            if (p.ReleaseDate is { } d)
            {
                parts.Add(d.ToLocalTime().ToString("yyyy-MM-dd"));
            }

            parts.Add($"{p.Tracks.Count} 曲");
            if (s.IsPurchased)
            {
                parts.Add(s.PurchaseText + " 購入済み");
            }

            if (s.CheckedTracks > 0)
            {
                parts.Add(s.IsAllChecked ? "✓ 全曲チェック済み" : $"チェック済み {s.CheckedText}");
            }

            return string.Join("   ", parts);
        }
    }

    // ---- 開く / 検索 ----

    /// <summary>URL ならその作品一覧 / 作品を、それ以外は Bandcamp で検索する。</summary>
    public async Task OpenAsync(string input, bool pushHistory = true)
    {
        input = input.Trim();
        if (input.Length == 0)
        {
            return;
        }

        if (pushHistory && _current is not null && _current != input)
        {
            _history.Push(_current);
        }

        _current = input;
        CommandManager.InvalidateRequerySuggested();

        if (!MainViewModel.LooksLikeUrl(input))
        {
            // Bandcamp の検索ページはアクセス確認 (ボット対策) があり、アプリからは取得しない。ブラウザで開いてもらう
            var searchUrl = $"https://bandcamp.com/search?q={Uri.EscapeDataString(input)}";
            Process.Start(new ProcessStartInfo(searchUrl) { UseShellExecute = true });
            _host.StatusText = "キーワード検索はブラウザで開きました。気になる作品・アーティストの URL を上の欄に貼るか、Chrome 拡張の「Benrify の「探す」で開く」を使ってください";
            return;
        }

        string url;
        try
        {
            url = BandcampUrls.Normalize(input);
        }
        catch (ArgumentException ex)
        {
            _host.StatusText = ex.Message;
            return;
        }

        var ct = StartLoad();
        try
        {
            if (BandcampUrls.Classify(url) == UrlKind.Band)
            {
                Title = "作品一覧を読み込み中…";
                var disco = await _client.GetDiscographyAsync(url, RequestPriority.Interactive, ct: ct);
                SetItems(disco.Items.Select(i => new BrowseItemViewModel(
                    i.Type == ItemType.Album ? SearchKind.Album : SearchKind.Track, i.Id, i.Title, i.Artist, i.Url, i.ArtUrl)));
                Title = $"{disco.BandName} — {disco.Items.Count} 作品";
            }
            else
            {
                Title = "読み込み中…";
                var page = await _client.GetTralbumAsync(url, RequestPriority.Interactive, ct: ct);
                var item = new BrowseItemViewModel(
                    page.Type == ItemType.Album ? SearchKind.Album : SearchKind.Track, page.Id, page.Title, page.Artist, page.Url,
                    BandcampUrls.ArtUrl(page.ArtId, ArtSize.Small));
                SetItems([item]);
                Title = $"{page.Artist} — {page.Title}";
                SelectedItem = item;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException)
        {
            Title = "開けませんでした: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BackAsync()
    {
        if (_history.TryPop(out var previous))
        {
            _host.InputUrl = previous;
            await OpenAsync(previous, pushHistory: false);
        }
    }

    private CancellationToken StartLoad()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        IsBusy = true;
        SelectedItem = null;
        Items.Clear();
        Tracks.Clear();
        return _loadCts.Token;
    }

    private void SetItems(IEnumerable<BrowseItemViewModel> rows)
    {
        Items.Clear();
        foreach (var row in rows)
        {
            Refresh(row);
            Items.Add(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
        UpdatePendingCount();
    }

    private bool MatchesFilter(BrowseItemViewModel item)
    {
        if ((HidePurchased && item.IsPurchased) || (HideChecked && item.IsAllChecked))
        {
            return false;
        }

        var f = FilterText.Trim();
        return f.Length == 0
               || item.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
               || item.Artist.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 作品の情報 (サポーター数・値段) ----

    /// <summary>キャッシュ済みのページを反映し、購入済みを判定する (リクエストは送らない)。</summary>
    private void Refresh(BrowseItemViewModel row)
    {
        if (!row.IsRelease)
        {
            return;
        }

        row.Page = _client.TryGetCachedTralbum(row.Url) ?? row.Page;
        row.Purchase = row.Kind == SearchKind.Album
            ? (_host.OwnsAlbum(row.Id) ? PurchaseState.Album : PurchaseState.None)
            : _host.OwnsTrack(row.Id) ? PurchaseState.Track
            : row.Page?.AlbumId is long a && _host.OwnsAlbum(a) ? PurchaseState.Album
            : PurchaseState.None;
        row.CheckedTracks = row.Page?.Tracks.Count(t => _host.IsChecked(t.TrackId)) ?? 0;
    }

    /// <summary>チェック済みが変わったとき (作品ごとのチェック済み曲数を数え直す。リクエストは送らない)。</summary>
    public void RefreshCheckedCounts()
    {
        foreach (var row in Items.Where(i => i.Page is not null))
        {
            row.CheckedTracks = row.Page!.Tracks.Count(t => _host.IsChecked(t.TrackId));
        }

        OnPropertyChanged(nameof(SelectedSummary));
    }

    private void UpdatePendingCount()
    {
        PendingReleaseCount = Items.Count(i => i.IsRelease && i.Page is null);
        var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * PendingReleaseCount);
        ReleaseInfoButtonText = PendingReleaseCount == 0
            ? "作品の情報: 取得済み"
            : $"作品の情報を取得 (未取得 {PendingReleaseCount} 件・約 {Format.Eta(eta)})";
        OnPropertyChanged(nameof(PendingReleaseCount));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>一覧の作品ページをまとめて取得して、サポーター数・値段・リリース日を埋める (1 作品 1 リクエスト)。</summary>
    private async Task FetchReleaseInfoAsync()
    {
        var targets = ItemsView.Cast<BrowseItemViewModel>().Where(i => i.IsRelease && i.Page is null).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _fetchingReleases = true;
        var ct = _host.BeginJob("作品の情報を取得中…");
        var done = 0;
        try
        {
            foreach (var row in targets)
            {
                ct.ThrowIfCancellationRequested();
                var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * (targets.Count - done));
                _host.ReportJob($"作品の情報を取得中 {done}/{targets.Count} (残り約 {Format.Eta(eta)}): {row.Title}", (double)done / targets.Count);
                row.IsLoading = true;
                try
                {
                    row.Page = await _client.GetTralbumAsync(row.Url, RequestPriority.Background, ct: ct);
                    row.Error = null;
                    Refresh(row);
                    _host.OnPageFetched(row.Url);
                }
                catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException)
                {
                    row.Error = ex.Message;
                }
                finally
                {
                    row.IsLoading = false;
                }

                done++;
            }

            _host.StatusText = $"{done} 作品の情報を取得しました";
        }
        catch (OperationCanceledException)
        {
            _host.StatusText = $"作品の情報の取得を中止しました ({done}/{targets.Count})";
        }
        finally
        {
            _fetchingReleases = false;
            _host.EndJob();
            UpdatePendingCount();
        }
    }

    // ---- 曲目 ----

    private async Task LoadTracksAsync(BrowseItemViewModel? item)
    {
        _selectCts?.Cancel();
        Tracks.Clear();
        if (item is not { IsRelease: true })
        {
            return;
        }

        var cts = _selectCts = new CancellationTokenSource();
        try
        {
            if (item.Page is null)
            {
                item.IsLoading = true;
                item.Page = await _client.GetTralbumAsync(item.Url, RequestPriority.Interactive, ct: cts.Token);
                Refresh(item);
                UpdatePendingCount();
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(item, SelectedItem))
            {
                return;
            }

            var number = 1;
            foreach (var t in item.Page!.Tracks)
            {
                var row = new QueueItemViewModel(t, number++);
                _host.RefreshItem(row);
                Tracks.Add(row);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException)
        {
            item.Error = ex.Message;
        }
        finally
        {
            item.IsLoading = false;
            OnPropertyChanged(nameof(SelectedArtUrl));
            OnPropertyChanged(nameof(SelectedSummary));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---- キューへ ----

    private void AddTracks(IReadOnlyList<QueueItemViewModel> tracks)
    {
        var playable = tracks.Where(t => t.IsStreamable).Select(t => t.Track).ToList();
        var added = _host.AddTracks(playable);
        _host.SaveQueue();
        _host.StatusText = $"{added} 曲をキューに追加しました"
                           + (playable.Count > added ? $" ({playable.Count - added} 曲は追加済み)" : "")
                           + (tracks.Count > playable.Count ? $" ({tracks.Count - playable.Count} 曲は試聴不可のため除外)" : "");
    }

    /// <summary>
    /// 選んだ作品の全曲をキューに追加する。取得済みの作品はすぐに、未取得の作品はページが届いたものから順に追加する
    /// (1 作品 1 リクエストで間隔を空けるので、件数が多いと時間がかかる。進み具合はジョブ欄に出す)。
    /// </summary>
    private async Task AddReleasesAsync(IReadOnlyList<BrowseItemViewModel> releases)
    {
        var ordered = releases.OrderBy(r => r.Page is null ? 1 : 0).ToList();
        var toFetch = ordered.Count(r => r.Page is null);
        var ct = _host.BeginJob($"{releases.Count} 作品をキューに追加中…");
        var added = 0;
        var found = 0;
        var fetched = 0;
        var errors = 0;
        try
        {
            foreach (var r in ordered)
            {
                ct.ThrowIfCancellationRequested();
                if (r.Page is null)
                {
                    var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * (toFetch - fetched));
                    _host.ReportJob(
                        $"選んだ作品をキューに追加中: ページを取得 {fetched}/{toFetch} (残り約 {Format.Eta(eta)}) — {added} 曲追加済み: {r.Title}",
                        toFetch > 0 ? (double)fetched / toFetch : -1);
                    try
                    {
                        r.Page = await _client.GetTralbumAsync(r.Url, RequestPriority.Interactive, ct: ct);
                        Refresh(r);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException)
                    {
                        r.Error = ex.Message;
                        errors++;
                        continue;
                    }
                    finally
                    {
                        fetched++;
                    }
                }

                var tracks = r.Page!.Tracks.Where(t => t.StreamUrl is not null).ToList();
                found += tracks.Count;
                added += _host.AddTracks(tracks);
            }

            _host.StatusText = $"{releases.Count} 作品から {added} 曲をキューに追加しました"
                               + (found > added ? $" ({found - added} 曲は追加済み)" : "")
                               + (errors > 0 ? $" (取得に失敗した作品: {errors})" : "");
        }
        catch (OperationCanceledException)
        {
            _host.StatusText = $"追加を中止しました ({added} 曲追加済み)";
        }
        finally
        {
            _host.EndJob();
            _host.SaveQueue();
            UpdatePendingCount();
        }
    }

    private static List<BrowseItemViewModel> SelectedReleases(object? p) =>
        p is IList list ? list.OfType<BrowseItemViewModel>().Where(i => i.IsRelease).ToList() : [];

    private static List<QueueItemViewModel> SelectedTracks(object? p) =>
        p is IList list ? list.OfType<QueueItemViewModel>().ToList() : [];

    // ---- 他から呼ばれる ----

    public void OnPageFetched(string url)
    {
        foreach (var row in Items.Where(i => BandcampUrls.Normalize(i.Url) == url))
        {
            Refresh(row);
        }

        foreach (var t in Tracks.Where(t => BandcampUrls.Normalize(t.Track.TrackUrl) == url || (t.Details.AlbumUrl is { } a && BandcampUrls.Normalize(a) == url)))
        {
            _host.RefreshItem(t);
        }

        OnPropertyChanged(nameof(SelectedSummary));
    }

    /// <summary>コレクションが変わったとき。</summary>
    public void RefreshAll()
    {
        foreach (var row in Items)
        {
            Refresh(row);
        }

        foreach (var t in Tracks)
        {
            _host.RefreshItem(t);
        }

        OnPropertyChanged(nameof(SelectedSummary));
    }

    public void RefreshQueuedFlags()
    {
        foreach (var t in Tracks)
        {
            t.IsQueued = _host.IsQueued(t.Track.TrackId);
        }
    }
}
