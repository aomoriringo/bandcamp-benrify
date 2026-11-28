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

    /// <summary>開いているファンのコレクション / ウィッシュリスト (それ以外を開いているときは null)。</summary>
    private FanList? _fanList;
    private bool _loadingFan;
    private CancellationTokenSource? _fanCts;

    /// <summary>一覧を開き直すたびに増やす (読み込み中に別の一覧を開いたら、古い結果を捨てるため)。</summary>
    private int _loadVersion;

    /// <summary>「続きを読み込む」1 回で送るリクエストの数 (1 回 20 件)。一度に大量に読まないよう小分けにする。</summary>
    private const int FanRequestsPerLoad = 10;

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
        LoadMoreFanCommand = new AsyncCommand(LoadMoreFanAsync, () => _fanList is { HasMore: true } && !_loadingFan);
        FetchReleaseInfoCommand = new AsyncCommand(FetchReleaseInfoAsync, () => (PendingReleaseCount > 0 || PendingPlusCount > 0) && !_fetchingReleases && !_host.IsFetchingDetails);
        OpenItemCommand = new AsyncCommand(p => p is BrowseItemViewModel { Kind: SearchKind.Band } b ? OpenAsync(b.Url) : Task.CompletedTask);
        AddReleasesCommand = new AsyncCommand(p => AddReleasesAsync(SelectedReleases(p), _host.QueueTarget), p => SelectedReleases(p).Count > 0);
        AddCurrentReleaseCommand = new AsyncCommand(() => AddReleasesAsync(SelectedItem is { IsRelease: true } s ? [s] : [], _host.QueueTarget), () => SelectedItem is { IsRelease: true, Page: not null });
        AddTracksCommand = new RelayCommand(p => AddTracksTo(SelectedTracks(p), _host.QueueTarget), p => SelectedTracks(p).Count > 0);
        PreviewCommand = new AsyncCommand(p => p is QueueItemViewModel { IsStreamable: true } t ? _host.PreviewAsync(t) : Task.CompletedTask);
        FetchTrackInfoCommand = new AsyncCommand(
            p => _host.FetchDetailsAsync(SelectedTracks(p) is { Count: > 0 } sel ? sel : Tracks.ToList()),
            _ => Tracks.Any(t => t.NeedsDetails) && !_host.IsFetchingDetails && !_fetchingReleases);
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

    /// <summary>ファンのコレクション / ウィッシュリストの続きを読む。</summary>
    public ICommand LoadMoreFanCommand { get; }

    /// <summary>ファンの一覧を開いていて、まだ続きがある。</summary>
    public bool HasMoreFanItems => _fanList is { HasMore: true };

    public string LoadMoreFanText
    {
        get
        {
            if (_fanList is not { HasMore: true } list)
            {
                return "";
            }

            var remaining = list.ItemCount - list.Items.Count;
            var requests = Math.Min(FanRequestsPerLoad, (remaining + BandcampClient.CollectionBatchSize - 1) / BandcampClient.CollectionBatchSize);
            var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * requests);
            return $"続きを読み込む (+{Math.Min(remaining, requests * BandcampClient.CollectionBatchSize)} 件・約 {Format.Eta(eta)})";
        }
    }

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

    /// <summary>サポーターがページに載りきらず「N+」になっている作品の数 (正確な人数を数える対象)。</summary>
    public int PendingPlusCount { get; private set; }

    /// <summary>「作品の情報を取得」の実行中 (キューの「曲の情報を取得」と同時には動かさない)。</summary>
    public bool IsFetchingReleases => _fetchingReleases;

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
            if (BandcampUrls.ParseFanList(url) is { } fan)
            {
                Title = "一覧を読み込み中…";
                var version = _loadVersion;
                var list = await _client.GetFanListAsync(fan.Username, fan.Kind, ct);
                if (version == _loadVersion)
                {
                    ShowFanList(list);
                }
            }
            else if (BandcampUrls.Classify(url) == UrlKind.Band)
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
        _loadVersion++;
        _fanCts?.Cancel();
        SetFanList(null);
        return _loadCts.Token;
    }

    // ---- ファンのコレクション / ウィッシュリスト ----

    private void SetFanList(FanList? list)
    {
        _fanList = list;
        OnPropertyChanged(nameof(HasMoreFanItems));
        OnPropertyChanged(nameof(LoadMoreFanText));
        CommandManager.InvalidateRequerySuggested();
        if (list is not null)
        {
            Title = $"{list.FanName} の{(list.Kind == FanListKind.Wishlist ? "ウィッシュリスト" : "コレクション")} — {list.Items.Count}/{list.ItemCount} 件";
        }
    }

    private static BrowseItemViewModel ToRow(DiscographyItem i) =>
        new(i.Type == ItemType.Album ? SearchKind.Album : SearchKind.Track, i.Id, i.Title, i.Artist, i.Url, i.ArtUrl);

    private void ShowFanList(FanList list)
    {
        SetItems(list.Items.Select(ToRow));
        SetFanList(list);
    }

    /// <summary>読み込んだ続きを一覧の末尾に足す (一覧は読み込んだ順のまま)。何度呼んでも同じ結果になる。</summary>
    private void AppendFanList(FanList list)
    {
        foreach (var item in list.Items.Skip(Items.Count))
        {
            var row = ToRow(item);
            Refresh(row);
            Items.Add(row);
        }

        SetFanList(list);
    }

    /// <summary>
    /// コレクション / ウィッシュリストの続きを読む。1 回あたり <see cref="FanRequestsPerLoad"/> リクエスト (200 件) までにして、
    /// 何千件もの一覧を一度に読みに行かないようにする。
    /// </summary>
    private async Task LoadMoreFanAsync()
    {
        if (_fanList is not { HasMore: true } list)
        {
            return;
        }

        var version = _loadVersion;
        var before = list.Items.Count;
        _loadingFan = true;
        CommandManager.InvalidateRequerySuggested();
        // 別の一覧を開いたら読み込みも止める (ジョブ欄の中止でも止まる)
        _fanCts = CancellationTokenSource.CreateLinkedTokenSource(_host.BeginJob("一覧の続きを読み込み中…"));
        var ct = _fanCts.Token;
        var progress = new Progress<FanList>(l =>
        {
            if (version == _loadVersion)
            {
                AppendFanList(l);
                _host.ReportJob($"一覧の続きを読み込み中 {l.Items.Count}/{l.ItemCount} 件", (double)l.Items.Count / Math.Max(1, l.ItemCount));
            }
        });
        try
        {
            var result = await _client.LoadMoreFanListAsync(list, FanRequestsPerLoad, progress, ct);
            if (version == _loadVersion)
            {
                AppendFanList(result);
                _host.StatusText = $"{result.Items.Count - before} 件を読み込みました ({result.Items.Count}/{result.ItemCount} 件)";
            }
        }
        catch (OperationCanceledException)
        {
            if (version == _loadVersion)
            {
                _host.StatusText = "読み込みを中止しました (読み込んだ分は保存されています)";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException)
        {
            _host.StatusText = "続きを読み込めませんでした: " + ex.Message;
        }
        finally
        {
            _loadingFan = false;
            _host.EndJob();
            UpdatePendingCount();
            CommandManager.InvalidateRequerySuggested();
        }
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
        PendingPlusCount = PlusPages(Items).Count;
        var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * (PendingReleaseCount + PendingPlusCount));
        var parts = new List<string>();
        if (PendingReleaseCount > 0)
        {
            parts.Add($"未取得 {PendingReleaseCount} 件");
        }

        if (PendingPlusCount > 0)
        {
            parts.Add($"人数未確定 {PendingPlusCount} 作品");
        }

        ReleaseInfoButtonText = parts.Count == 0
            ? "作品の情報: 取得済み"
            : $"作品の情報を取得 ({string.Join("・", parts)}・約 {Format.Eta(eta)}{(PendingPlusCount > 0 ? "〜" : "")})";
        OnPropertyChanged(nameof(PendingReleaseCount));
        OnPropertyChanged(nameof(PendingPlusCount));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>サポーターが「N+」になっている作品のページ。</summary>
    private static List<TralbumPage> PlusPages(IEnumerable<BrowseItemViewModel> rows) =>
        rows.Where(i => i.IsRelease && i.Page is { Supporters.HasMore: true })
            .Select(i => i.Page!)
            .DistinctBy(p => BandcampUrls.Normalize(p.Url))
            .ToList();

    /// <summary>
    /// 一覧 (絞り込み後) の作品ページをまとめて取得して、サポーター数・値段・リリース日を埋める (1 作品 1 リクエスト)。
    /// そのあと「N+」の作品の正確なサポーター数を、キューの「曲の情報を取得」と同じ方法で数える。
    /// </summary>
    private async Task FetchReleaseInfoAsync()
    {
        var visible = ItemsView.Cast<BrowseItemViewModel>().ToList();
        var targets = visible.Where(i => i.IsRelease && i.Page is null).ToList();
        var plusCount = PlusPages(visible).Count;
        if ((targets.Count == 0 && plusCount == 0) || !_host.ConfirmManyRequests(targets.Count, plusCount))
        {
            return;
        }

        _fetchingReleases = true;
        var ct = _host.BeginJob("作品の情報を取得中…");
        var done = 0;
        var errors = 0;
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
                    errors++;
                }
                finally
                {
                    row.IsLoading = false;
                }

                done++;
            }

            // 取得したページで新たに「N+」と分かった作品も含めて数える (絞り込み後の一覧だけ)
            var count = await _host.CountExactSupportersAsync(PlusPages(ItemsView.Cast<BrowseItemViewModel>()), ct);
            _host.StatusText = MainViewModel.FetchResultText(done, count.Counted, count.Capped, errors + count.Errors);
        }
        catch (OperationCanceledException)
        {
            _host.StatusText = "作品の情報の取得を中止しました (取得・集計した分は保存されています)";
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

    /// <summary>選んだ曲を指定したキューに入れる (右クリックの「キューに追加」からはどのキューにも入れられる)。</summary>
    public void AddTracksTo(IReadOnlyList<QueueItemViewModel> tracks, SavedQueue target)
    {
        var playable = tracks.Where(t => t.IsStreamable).Select(t => t.Track).ToList();
        _host.AddTracksTo(playable, target, tracks.Count > playable.Count ? $" ({tracks.Count - playable.Count} 曲は試聴不可のため除外)" : null);
    }

    /// <summary>
    /// 選んだ作品の全曲をキューに追加する。取得済みの作品はすぐに、未取得の作品はページが届いたものから順に追加する
    /// (1 作品 1 リクエストで間隔を空けるので、件数が多いと時間がかかる。進み具合はジョブ欄に出す)。
    /// </summary>
    public async Task AddReleasesAsync(IReadOnlyList<BrowseItemViewModel> releases, SavedQueue target)
    {
        var ordered = releases.OrderBy(r => r.Page is null ? 1 : 0).ToList();
        var toFetch = ordered.Count(r => r.Page is null);
        var ct = _host.BeginJob($"{releases.Count} 作品を{_host.QueueLabelOf(target)}に追加中…");
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
                        $"選んだ作品を{_host.QueueLabelOf(target)}に追加中: ページを取得 {fetched}/{toFetch} (残り約 {Format.Eta(eta)}) — {added} 曲追加済み: {r.Title}",
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
                added += _host.AddTracks(tracks, target);
            }

            _host.StatusText = $"{releases.Count} 作品から {added} 曲を{_host.QueueLabelOf(target)}に追加しました"
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
        UpdatePendingCount();
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
