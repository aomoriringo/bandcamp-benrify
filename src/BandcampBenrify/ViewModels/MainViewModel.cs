using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using BandcampBenrify.Core;
using BandcampBenrify.Infrastructure;
using BandcampBenrify.Services;

namespace BandcampBenrify.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxConsecutiveFailures = 5;

    /// <summary>これ以上のリクエストになりそうなら、実行前に確認する。</summary>
    private const int ConfirmRequestThreshold = 300;

    private readonly AppSettings _settings;
    private readonly ThrottledHttpClient _http;
    private readonly DiskCache _cache;
    private readonly BandcampClient _client;
    private readonly AudioPlayer _player = new(Dispatcher.CurrentDispatcher);
    private readonly PlaybackOrder<QueueItemViewModel> _order = new();
    private readonly HashSet<long> _queuedTrackIds = [];
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _pendingTimer;
    private CancellationTokenSource _jobsCts = new();
    private CancellationTokenSource? _playCts;
    private string? _lastHttpMessage;
    private int _consecutiveFailures;
    private bool _retriedCurrent;
    private int _nextSeq;
    private int _selectedTab;
    private bool _fetchingDetails;
    private bool _loadingCollection;
    private HashSet<long> _ownedAlbums = [];
    private HashSet<long> _ownedTracks = [];

    private string _inputUrl = "";
    private string _statusText = "準備完了";
    private string _requestStatus = "";
    private QueueItemViewModel? _current;
    private bool _isPlaying;
    private double _position;
    private double _duration;
    private int _activeJobs;
    private string _jobText = "";
    private double _jobProgress = -1;
    private string _detailsButtonText = "";
    private string _collectionStatus = "";

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _http = new ThrottledHttpClient(TimeSpan.FromSeconds(settings.RequestIntervalSeconds));
        _http.StatusChanged += msg =>
        {
            if (msg is not null)
            {
                _lastHttpMessage = msg;
            }
        };
        _cache = new DiskCache(AppPaths.CacheDir);
        _client = new BandcampClient(_http, _cache)
        {
            TralbumTtl = TimeSpan.FromHours(settings.PageCacheHours),
            DiscographyTtl = TimeSpan.FromHours(settings.DiscographyCacheHours),
        };

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Queue);
        view.IsLiveSorting = true;
        foreach (var p in new[]
                 {
                     nameof(QueueItemViewModel.TotalSort), nameof(QueueItemViewModel.AlbumSupSort), nameof(QueueItemViewModel.TrackSupSort),
                     nameof(QueueItemViewModel.TrackPriceSort), nameof(QueueItemViewModel.AlbumPriceSort), nameof(QueueItemViewModel.ReleaseDate),
                     nameof(QueueItemViewModel.PurchaseText),
                 })
        {
            view.LiveSortingProperties.Add(p);
        }

        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(QueueItemViewModel.IsPurchased));
        view.Filter = o => !HidePurchased || o is QueueItemViewModel { IsPurchased: false };
        QueueView = view;

        _player.Volume = settings.Volume;
        _player.IsLooping = settings.RepeatOne;
        _order.Shuffle = settings.Shuffle;
        _player.Opened += () =>
        {
            _consecutiveFailures = 0;
            Duration = _player.Duration?.TotalSeconds ?? Current?.Track.DurationSeconds ?? 0;
            StatusText = $"{(IsPreview ? "試聴中" : "再生中")}: {Current?.Artist} - {Current?.Title}";
        };
        _player.Ended += () =>
        {
            // 試聴 (キューに無い曲) は 1 曲で止める。キューの曲なら次へ
            if (IsPreview)
            {
                StopPlayback();
            }
            else
            {
                _ = RunSafeAsync(NextAsync());
            }
        };
        _player.Failed += msg => _ = RunSafeAsync(OnPlaybackFailedAsync(msg));
        _player.PlayingChanged += playing =>
        {
            if (Current is not null)
            {
                IsPlaying = playing;
            }
        };
        _player.NextRequested += () => _ = RunSafeAsync(NextAsync());
        _player.PreviousRequested += () => _ = RunSafeAsync(PreviousAsync());

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _pendingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _pendingTimer.Tick += (_, _) => UpdatePendingCount();

        AsyncCommand.ErrorHandler = ex => StatusText = "エラー: " + ex.Message;

        Browse = new BrowseViewModel(this, _client);

        AddUrlCommand = new AsyncCommand(
            async () =>
            {
                var url = InputUrl;
                if (await AddUrlAsync(url) > 0 && InputUrl == url)
                {
                    InputUrl = ""; // 成功したら次の URL を貼りやすいように空にする
                }
            },
            () => LooksLikeUrl(InputUrl));
        OpenInBrowseCommand = new AsyncCommand(
            () =>
            {
                SelectedTab = 0;
                return Browse.OpenAsync(InputUrl);
            },
            () => !string.IsNullOrWhiteSpace(InputUrl));
        PlayPauseCommand = new AsyncCommand(PlayPauseAsync, () => Queue.Count > 0 || Current is not null);
        NextCommand = new AsyncCommand(NextAsync, () => Queue.Count > 0);
        PreviousCommand = new AsyncCommand(PreviousAsync, () => Queue.Count > 0);
        PlayItemCommand = new AsyncCommand(p => p is QueueItemViewModel item ? PlayItemAsync(item, userInitiated: true) : Task.CompletedTask);
        RemoveSelectedCommand = new RelayCommand(p => RemoveItems(Selected(p)), p => p is IList { Count: > 0 });
        ClearQueueCommand = new RelayCommand(() =>
        {
            if (MessageBox.Show($"キューの {Queue.Count} 曲をすべて削除しますか？", "Bandcamp Benrify", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                RemoveItems(Queue.ToList());
            }
        }, () => Queue.Count > 0);
        FetchDetailsCommand = new AsyncCommand(
            p => FetchDetailsAsync(Selected(p) is { Count: > 0 } sel ? sel : QueueView.Cast<QueueItemViewModel>().ToList()),
            _ => (PendingPageCount > 0 || PendingPlusCount > 0) && !_fetchingDetails);
        RefreshCollectionCommand = new AsyncCommand(RefreshCollectionAsync, () => HasProfile && !_loadingCollection);
        CancelJobsCommand = new RelayCommand(CancelJobs, () => ActiveJobs > 0);
        OpenInBrowserCommand = new RelayCommand(p => OpenUrl((p as QueueItemViewModel ?? Current)?.Track.TrackUrl));
        OpenAlbumCommand = new RelayCommand(p => OpenUrl(p is QueueItemViewModel q ? TrackDetailsResolver.AlbumUrlOf(q.Track) : null));

        LoadQueue();
        LoadCachedCollection();
        UpdatePendingCount();
        _selectedTab = Queue.Count > 0 ? 1 : 0;
    }

    /// <summary>「探す」タブ。</summary>
    public BrowseViewModel Browse { get; }

    public ICommand OpenInBrowseCommand { get; }

    /// <summary>0: 探す / 1: キュー</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, value);
    }

    public static bool LooksLikeUrl(string? s)
    {
        s = s?.Trim() ?? "";
        return s.Contains("://", StringComparison.Ordinal) || s.Contains(".bandcamp.com", StringComparison.OrdinalIgnoreCase)
               || (s.Contains('.') && s.Contains('/') && !s.Contains(' '));
    }

    public bool IsQueued(long trackId) => _queuedTrackIds.Contains(trackId);

    /// <summary>キューに入っていない曲 (試聴) を再生中か。</summary>
    public bool IsPreview => Current is not null && !_queuedTrackIds.Contains(Current.Track.TrackId);

    /// <summary>「探す」から: キューに入れずに試聴する。</summary>
    public Task PreviewAsync(QueueItemViewModel item) => PlayItemAsync(item, userInitiated: true);

    public bool RepeatOne
    {
        get => _settings.RepeatOne;
        set
        {
            if (_settings.RepeatOne != value)
            {
                _settings.RepeatOne = value;
                _settings.Save();
                _player.IsLooping = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlayModeText));
            }
        }
    }

    public ObservableCollection<QueueItemViewModel> Queue { get; } = [];

    /// <summary>並び替え・絞り込み済みのキュー。シャッフルしないときはこの順に再生する。</summary>
    public ICollectionView QueueView { get; }

    public ICommand AddUrlCommand { get; }

    public ICommand PlayPauseCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand PreviousCommand { get; }

    public ICommand PlayItemCommand { get; }

    public ICommand RemoveSelectedCommand { get; }

    public ICommand ClearQueueCommand { get; }

    public ICommand FetchDetailsCommand { get; }

    public ICommand RefreshCollectionCommand { get; }

    public ICommand CancelJobsCommand { get; }

    public ICommand OpenInBrowserCommand { get; }

    public ICommand OpenAlbumCommand { get; }

    public AppSettings Settings => _settings;

    public string InputUrl
    {
        get => _inputUrl;
        set => Set(ref _inputUrl, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public string RequestStatus
    {
        get => _requestStatus;
        private set => Set(ref _requestStatus, value);
    }

    public string QueueSummary
    {
        get
        {
            var shown = HidePurchased ? Queue.Count(q => !q.IsPurchased) : Queue.Count;
            var total = TimeSpan.FromSeconds(Queue.Sum(q => q.DurationSeconds));
            var text = $"{Queue.Count} 曲 ({(total.TotalHours >= 1 ? $"{(int)total.TotalHours}時間{total.Minutes}分" : $"{total.Minutes}分")})";
            return HidePurchased && shown != Queue.Count ? $"{text} / 表示 {shown} 曲" : text;
        }
    }

    public bool IsQueueEmpty => Queue.Count == 0;

    public bool HasCurrent => Current is not null;

    public QueueItemViewModel? Current
    {
        get => _current;
        private set
        {
            if (_current is not null)
            {
                _current.IsPlaying = false;
            }

            if (Set(ref _current, value))
            {
                OnPropertyChanged(nameof(HasCurrent));
                OnPropertyChanged(nameof(NowTitle));
                OnPropertyChanged(nameof(NowSubtitle));
                OnPropertyChanged(nameof(NowArtUrl));
                OnPropertyChanged(nameof(NowStats));
                OnPropertyChanged(nameof(WindowTitle));
            }

            if (_current is not null)
            {
                _current.IsPlaying = true;
            }
        }
    }

    public string NowTitle => Current?.Title ?? "再生していません";

    public string NowSubtitle => Current is null ? "" : $"{(IsPreview ? "試聴中 · " : "")}{Current.Artist} — {Current.Album}";

    /// <summary>再生中の曲のサポーター数と値段。</summary>
    public string NowStats => Current is null
        ? ""
        : $"サポーター {Current.TotalText} (アルバム {Current.AlbumSupText} / 曲 {Current.TrackSupText})   曲 {Current.TrackPriceText}   アルバム {Current.AlbumPriceText}"
          + (Current.IsPurchased ? $"   {Current.PurchaseText}購入済み" : "");

    public string? NowArtUrl => Current?.Track.ArtId is long id ? BandcampUrls.ArtUrl(id, ArtSize.Medium) : null;

    public string WindowTitle => Current is null ? "Bandcamp Benrify" : $"{Current.Title} - {Current.Artist} | Bandcamp Benrify";

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (Set(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseGlyph));
            }
        }
    }

    /// <summary>Segoe MDL2 Assets の再生 / 一時停止アイコン。</summary>
    public string PlayPauseGlyph => IsPlaying ? "" : "";

    public bool Shuffle
    {
        get => _order.Shuffle;
        set
        {
            if (_order.Shuffle != value)
            {
                _order.Shuffle = value;
                _settings.Shuffle = value;
                _settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlayModeText));
            }
        }
    }

    public string PlayModeText => RepeatOne ? "1 曲リピート" : Shuffle ? "ランダム再生" : "表示順に再生";

    public bool HidePurchased
    {
        get => _settings.HidePurchased;
        set
        {
            if (_settings.HidePurchased != value)
            {
                _settings.HidePurchased = value;
                _settings.Save();
                OnPropertyChanged();
                QueueView.Refresh();
                OnPropertyChanged(nameof(QueueSummary));
            }
        }
    }

    public string BandcampUsername => _settings.BandcampUsername;

    public bool HasProfile => !string.IsNullOrEmpty(BandcampUsername);

    public long CacheSizeBytes => _cache.SizeBytes();

    public void ClearCache()
    {
        _cache.Clear();
        StatusText = "キャッシュを削除しました";
    }

    /// <summary>設定画面から。プロフィールが変わったらコレクションを読み込み直す。</summary>
    public void ApplySettings(string username, double requestIntervalSeconds)
    {
        _settings.RequestIntervalSeconds = requestIntervalSeconds;
        _http.MinInterval = TimeSpan.FromSeconds(requestIntervalSeconds);

        var changed = !string.Equals(_settings.BandcampUsername, username, StringComparison.OrdinalIgnoreCase);
        _settings.BandcampUsername = username;
        _settings.Save();
        OnPropertyChanged(nameof(BandcampUsername));
        OnPropertyChanged(nameof(HasProfile));
        UpdatePendingCount();

        if (changed)
        {
            ApplyCollection(_client.TryGetCachedCollection(username));
            if (HasProfile && RefreshCollectionCommand.CanExecute(null))
            {
                RefreshCollectionCommand.Execute(null);
            }
        }
    }

    public string CollectionStatus
    {
        get => _collectionStatus;
        private set => Set(ref _collectionStatus, value);
    }

    public double Volume
    {
        get => _player.Volume;
        set
        {
            _player.Volume = value;
            _settings.Volume = value;
            OnPropertyChanged();
        }
    }

    /// <summary>シークバーをドラッグ中はタイマーで位置を上書きしない。</summary>
    public bool IsSeeking { get; set; }

    public double Position
    {
        get => _position;
        private set
        {
            if (Set(ref _position, value))
            {
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public double Duration
    {
        get => _duration;
        private set
        {
            if (Set(ref _duration, value))
            {
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(SeekMaximum));
            }
        }
    }

    /// <summary>長さ 0 だとつまみが右端に張り付くので、停止中は 1 にしておく。</summary>
    public double SeekMaximum => Duration > 0 ? Duration : 1;

    public string PositionText => Format.Duration(Position) is { Length: > 0 } s ? s : "0:00";

    public string DurationText => Format.Duration(Duration) is { Length: > 0 } s ? s : "0:00";

    public int ActiveJobs
    {
        get => _activeJobs;
        private set
        {
            if (Set(ref _activeJobs, value))
            {
                OnPropertyChanged(nameof(HasJobs));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasJobs => ActiveJobs > 0;

    public string JobText
    {
        get => _jobText;
        private set => Set(ref _jobText, value);
    }

    /// <summary>0〜1。不明なら -1 (バーは不定表示)。</summary>
    public double JobProgress
    {
        get => _jobProgress;
        private set
        {
            if (Set(ref _jobProgress, value))
            {
                OnPropertyChanged(nameof(JobProgressIndeterminate));
            }
        }
    }

    public bool JobProgressIndeterminate => JobProgress < 0;

    /// <summary>未取得の曲ページ / アルバムページの数。</summary>
    public int PendingPageCount { get; private set; }

    public string DetailsButtonText
    {
        get => _detailsButtonText;
        private set => Set(ref _detailsButtonText, value);
    }

    public void SeekTo(double seconds)
    {
        if (Current is not null)
        {
            _player.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
            Position = seconds;
        }
    }

    // ---- キューへの追加 ----

    /// <summary>URL の曲をキューに追加する。見つかった曲数 (追加済みを含む) を返す。</summary>
    public async Task<int> AddUrlAsync(string url)
    {
        url = url.Trim();
        if (url.Length == 0)
        {
            return 0;
        }

        string normalized;
        try
        {
            normalized = BandcampUrls.Normalize(url);
        }
        catch (ArgumentException ex)
        {
            StatusText = ex.Message;
            return 0;
        }

        var kind = BandcampUrls.Classify(normalized);
        var ct = _jobsCts.Token;
        ActiveJobs++;
        var added = 0;
        var found = 0;
        var errors = 0;
        var noAudio = 0;
        var progress = new Progress<ResolveProgress>(p =>
        {
            if (p.Error is not null)
            {
                errors++;
            }

            if (p.NoAudio)
            {
                noAudio++;
            }

            if (kind == UrlKind.Band)
            {
                var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * Math.Max(0, p.Total - p.Done));
                JobText = $"追加中 {p.Done}/{p.Total} 作品 (取得済みの作品はすぐ終わります。最大で残り約 {Format.Eta(eta)}): {p.Current}";
                JobProgress = p.Total > 0 ? (double)p.Done / p.Total : -1;
            }
            else
            {
                JobText = $"取得中: {p.Current}";
                JobProgress = -1;
            }
        });

        try
        {
            StatusText = kind == UrlKind.Band
                ? "レーベル / アーティストの全作品を取得しています (未取得の作品は 1 件ずつ間隔を空けて取得します)"
                : "取得中…";
            await foreach (var track in _client.ResolveTracksAsync(normalized, progress, ct))
            {
                found++;
                added += AddTracks([track]);
            }

            var skipped = found - added;
            StatusText = found == 0
                ? "再生できる曲が見つかりませんでした (試聴が公開されていない作品の可能性があります)"
                : $"{added} 曲をキューに追加しました"
                    + (skipped > 0 ? $" ({skipped} 曲は追加済みのためスキップ)" : "")
                    + (noAudio > 0 ? $" (試聴できない作品: {noAudio})" : "")
                    + (errors > 0 ? $" (取得に失敗した作品: {errors})" : "")
                    + (added > 0 ? "。曲単体のサポーター数・値段は「曲の情報を取得」で取得できます" : "");
        }
        catch (OperationCanceledException)
        {
            StatusText = $"追加を中止しました ({added} 曲追加済み)";
        }
        catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException or ArgumentException)
        {
            StatusText = "追加できませんでした: " + ex.Message;
        }
        finally
        {
            EndJob();
            UpdatePendingCount();
            SaveQueue();
        }

        return found;
    }

    /// <summary>キューに曲を追加する。既に入っている曲はスキップ。追加数を返す。</summary>
    public int AddTracks(IEnumerable<TrackInfo> tracks)
    {
        var added = new List<QueueItemViewModel>();
        foreach (var track in tracks)
        {
            if (_queuedTrackIds.Add(track.TrackId))
            {
                var vm = new QueueItemViewModel(track, ++_nextSeq);
                RefreshItem(vm);
                Queue.Add(vm);
                added.Add(vm);
            }
        }

        if (added.Count > 0)
        {
            _order.OnAdded(added);
            OnQueueChanged();
        }

        return added.Count;
    }

    private void RemoveItems(IReadOnlyCollection<QueueItemViewModel> items)
    {
        foreach (var item in items)
        {
            if (ReferenceEquals(item, Current))
            {
                StopPlayback();
            }

            Queue.Remove(item);
            _queuedTrackIds.Remove(item.Track.TrackId);
        }

        if (Queue.Count == 0)
        {
            _order.Reset();
        }

        // 「#」を追加順のまま 1 から詰め直す
        _nextSeq = 0;
        foreach (var item in Queue)
        {
            item.Seq = ++_nextSeq;
        }

        OnQueueChanged();
        UpdatePendingCount();
        SaveQueue();
    }

    private void OnQueueChanged()
    {
        SchedulePendingUpdate();
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(QueueHeader));
        OnPropertyChanged(nameof(IsQueueEmpty));
        OnPropertyChanged(nameof(NowSubtitle));
        Browse.RefreshQueuedFlags();
        CommandManager.InvalidateRequerySuggested();
    }

    public string QueueHeader => $"キュー ({Queue.Count})";

    // ---- 曲ごとのサポーター数・値段 ----

    /// <summary>キャッシュ済みのページからサポーター数・値段・購入済みを計算し直す (リクエストは送らない)。</summary>
    public void RefreshItem(QueueItemViewModel item)
    {
        var details = TrackDetailsResolver.Resolve(item.Track, _client.TryGetCachedTralbum);
        item.Update(details);
        item.Purchase = TrackDetailsResolver.Purchased(item.Track, details, _ownedAlbums, _ownedTracks);
        item.IsQueued = _queuedTrackIds.Contains(item.Track.TrackId);
        if (ReferenceEquals(item, Current))
        {
            OnPropertyChanged(nameof(NowStats));
        }
    }

    public bool OwnsAlbum(long id) => _ownedAlbums.Contains(id);

    public bool OwnsTrack(long id) => _ownedTracks.Contains(id);

    /// <summary>どこかでページを取得したら、キューと「探す」の該当行を更新する。</summary>
    public void OnPageFetched(string url)
    {
        url = BandcampUrls.Normalize(url);
        foreach (var item in Queue.Where(q => BandcampUrls.Normalize(q.Track.TrackUrl) == url
                                              || (q.Details.AlbumUrl is { } a && BandcampUrls.Normalize(a) == url)))
        {
            RefreshItem(item);
        }

        Browse.OnPageFetched(url);
        SchedulePendingUpdate();
    }

    // ---- バックグラウンド処理の表示 (キュー・探す 共通) ----

    public CancellationToken BeginJob(string text)
    {
        ActiveJobs++;
        JobText = text;
        JobProgress = -1;
        return _jobsCts.Token;
    }

    public void ReportJob(string text, double progress)
    {
        JobText = text;
        JobProgress = progress;
    }

    /// <summary>
    /// 未取得ページ数の再計算を少し後にまとめて行う (レーベル全曲の追加などで 1 曲ずつ追加されても重くならないように)。
    /// </summary>
    private void SchedulePendingUpdate()
    {
        _pendingTimer.Stop();
        _pendingTimer.Start();
    }

    private void UpdatePendingCount()
    {
        _pendingTimer.Stop();
        PendingPageCount = Queue.SelectMany(q => q.Details.MissingPages(q.Track)).Select(BandcampUrls.Normalize).Distinct().Count();
        PendingPlusCount = PagesWithMoreSupporters(Queue).Count;
        var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * (PendingPageCount + PendingPlusCount));
        var parts = new List<string>();
        if (PendingPageCount > 0)
        {
            parts.Add($"未取得 {PendingPageCount} ページ");
        }

        if (PendingPlusCount > 0)
        {
            parts.Add($"人数未確定 {PendingPlusCount} 作品");
        }

        DetailsButtonText = parts.Count == 0
            ? "曲の情報: すべて取得済み"
            : $"曲の情報を取得 ({string.Join("・", parts)}・約 {Format.Eta(eta)}{(PendingPlusCount > 0 ? "〜" : "")})";
        OnPropertyChanged(nameof(PendingPageCount));
        OnPropertyChanged(nameof(PendingPlusCount));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>サポーターがページに載りきらず「N+」になっているページ (アルバム / 曲)。</summary>
    private static List<TralbumPage> PagesWithMoreSupporters(IEnumerable<QueueItemViewModel> items) =>
        items.SelectMany(q => new[] { q.Details.AlbumPage, q.Details.TrackPage })
            .OfType<TralbumPage>()
            .Where(p => p.Supporters.HasMore)
            .DistinctBy(p => BandcampUrls.Normalize(p.Url))
            .ToList();

    /// <summary>「N+」の作品の数 (正確な人数を数える対象)。</summary>
    public int PendingPlusCount { get; private set; }

    public bool IsFetchingDetails => _fetchingDetails;

    /// <summary>
    /// 曲ページ (曲単体のサポーター数・値段) と未取得のアルバムページを取得し (1 ページ 1 リクエスト)、
    /// そのあと「N+」になっている作品のサポーターを「もっと見る」と同じ方法で数える (100 人 1 リクエスト、1 作品あたり上限あり)。
    /// キューからも「探す」からも使う。
    /// </summary>
    public async Task FetchDetailsAsync(IReadOnlyList<QueueItemViewModel> targets)
    {
        var urls = targets.SelectMany(q => q.Details.MissingPages(q.Track)).Select(BandcampUrls.Normalize).Distinct().ToList();
        var plusCount = PagesWithMoreSupporters(targets).Count;
        if (urls.Count == 0 && plusCount == 0)
        {
            StatusText = "曲の情報はすべて取得済みです";
            return;
        }

        // 量が多いときは確認する (人数を数える分は 1 作品 1 回以上なので最低限の見積もり)
        var estimate = urls.Count + plusCount;
        if (estimate > ConfirmRequestThreshold)
        {
            var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * estimate);
            if (MessageBox.Show(
                    $"{urls.Count} ページの取得と {plusCount} 作品のサポーター集計で、少なくとも {estimate} 回 Bandcamp にアクセスします (約 {Format.Eta(eta)} 以上)。\n途中で中止でき、取得した分は保存されます。続けますか？",
                    "Bandcamp Benrify", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }
        }

        var seen = new HashSet<string>(urls);
        _fetchingDetails = true;
        var ct = BeginJob("曲の情報を取得中…");
        var done = 0;
        var errors = 0;
        try
        {
            for (var i = 0; i < urls.Count; i++)
            {
                var url = urls[i];
                ct.ThrowIfCancellationRequested();
                var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * (urls.Count - done));
                ReportJob($"曲の情報を取得中 {done}/{urls.Count} (残り約 {Format.Eta(eta)})", (double)done / urls.Count);
                try
                {
                    await _client.GetTralbumAsync(url, RequestPriority.Background, ct: ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException)
                {
                    errors++;
                    Debug.WriteLine($"{url}: {ex.Message}");
                }

                done++;
                OnPageFetched(url);

                // 曲ページを取って初めて収録アルバムが分かった場合は、そのアルバムページも続けて取る
                foreach (var target in targets.Where(t => BandcampUrls.Normalize(t.Track.TrackUrl) == url))
                {
                    RefreshItem(target);
                    foreach (var missing in target.Details.MissingPages(target.Track).Select(BandcampUrls.Normalize))
                    {
                        if (seen.Add(missing))
                        {
                            urls.Add(missing);
                        }
                    }
                }
            }

            // 「N+」の作品の正確なサポーター数を数える
            var plusPages = PagesWithMoreSupporters(targets);
            var counted = 0;
            var capped = 0;
            foreach (var page in plusPages)
            {
                ct.ThrowIfCancellationRequested();
                var label = $"正確なサポーター数を集計中 {counted}/{plusPages.Count} 作品: {page.Title}";
                ReportJob(label, (double)counted / plusPages.Count);
                var progress = new Progress<int>(n => ReportJob($"{label} ({n} 人まで数えました)", (double)counted / plusPages.Count));
                try
                {
                    var result = await _client.CountSupportersAsync(page, _settings.SupportersMaxRequestsPerItem, progress, ct);
                    if (result.Supporters.HasMore)
                    {
                        capped++;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException)
                {
                    errors++;
                    Debug.WriteLine($"{page.Url}: {ex.Message}");
                }

                counted++;
                OnPageFetched(page.Url);
            }

            StatusText = (done > 0 ? $"{done} ページを取得" : "")
                         + (done > 0 && counted > 0 ? "、" : "")
                         + (counted > 0 ? $"{counted} 作品のサポーター数を集計" : "")
                         + "しました"
                         + (capped > 0 ? $" (サポーターが特に多い {capped} 作品は上限まで数えたので「+」のまま。もう一度押すと続きから数えます)" : "")
                         + (errors > 0 ? $" (失敗 {errors})" : "");
        }
        catch (OperationCanceledException)
        {
            StatusText = "取得を中止しました (取得・集計した分は保存されています)";
        }
        finally
        {
            _fetchingDetails = false;
            EndJob();
            UpdatePendingCount();
        }
    }

    // ---- コレクション (購入済み) ----

    private void LoadCachedCollection() => ApplyCollection(_client.TryGetCachedCollection(BandcampUsername));

    private async Task RefreshCollectionAsync()
    {
        _loadingCollection = true;
        ActiveJobs++;
        JobProgress = -1;
        JobText = "コレクションを確認中…";
        try
        {
            var progress = new Progress<CollectionProgress>(p =>
            {
                var remaining = Math.Max(0, p.Total - p.Loaded);
                var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * (long)Math.Ceiling(remaining / (double)BandcampClient.CollectionBatchSize));
                JobText = remaining == 0
                    ? $"コレクションを読み込み中 {p.Loaded} 件"
                    : $"コレクションを読み込み中 {p.Loaded}/{p.Total} 件 (Bandcamp は 1 回 20 件ずつなので残り約 {Format.Eta(eta)}。中止しても次回は続きから読み込みます)";
                JobProgress = p.Total > 0 ? Math.Min(1, (double)p.Loaded / p.Total) : -1;
                if (p.Partial is { } partial)
                {
                    ApplyCollection(partial);
                }
            });
            var collection = await _client.GetCollectionAsync(BandcampUsername, progress, _jobsCts.Token);
            ApplyCollection(collection);
            StatusText = $"コレクション {collection.Count} 件を読み込みました。キュー内の購入済み: {Queue.Count(q => q.IsPurchased)} 曲";
        }
        catch (OperationCanceledException)
        {
            var partial = _client.TryGetCachedCollection(BandcampUsername);
            ApplyCollection(partial);
            StatusText = $"コレクションの読み込みを中止しました ({partial?.Count ?? 0} 件まで読み込み済み。「コレクション更新」で続きから読み込めます)";
        }
        catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException or ArgumentException)
        {
            StatusText = "コレクションを取得できませんでした: " + ex.Message;
        }
        finally
        {
            _loadingCollection = false;
            EndJob();
        }
    }

    private void ApplyCollection(FanCollection? collection)
    {
        _ownedAlbums = collection is null ? [] : [.. collection.AlbumIds];
        _ownedTracks = collection is null ? [] : [.. collection.TrackIds];
        foreach (var item in Queue)
        {
            RefreshItem(item);
        }

        Browse.RefreshAll();
        CollectionStatus = !HasProfile ? "プロフィール未設定 (設定から登録すると購入済みに印が付きます)"
            : collection is null ? $"{BandcampUsername} のコレクション: 未読み込み"
            : collection.Complete ? $"{BandcampUsername} のコレクション: {collection.Count} 件 ({collection.FetchedAt.ToLocalTime():M/d HH:mm} 時点)"
            : $"{BandcampUsername} のコレクション: {collection.Count}/{collection.ItemCount} 件 (読み込み途中)";
        OnPropertyChanged(nameof(QueueSummary));
    }

    public void EndJob()
    {
        ActiveJobs--;
        if (ActiveJobs == 0)
        {
            JobText = "";
            JobProgress = -1;
        }
    }

    private void CancelJobs()
    {
        _jobsCts.Cancel();
        _jobsCts = new CancellationTokenSource();
    }

    // ---- 再生 ----

    /// <summary>今の表示順 (並び替え・絞り込み後) のキュー。</summary>
    private List<QueueItemViewModel> PlaybackList() => QueueView.Cast<QueueItemViewModel>().ToList();

    private async Task PlayPauseAsync()
    {
        if (Current is null)
        {
            await NextAsync();
            return;
        }

        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
        }
        else
        {
            _player.Play();
            IsPlaying = true;
        }
    }

    private async Task NextAsync()
    {
        var next = _order.Next(PlaybackList(), Current);
        if (next is null)
        {
            StopPlayback();
            return;
        }

        await PlayItemAsync(next, userInitiated: false);
    }

    private async Task PreviousAsync()
    {
        if (Current is not null && Position > 3)
        {
            SeekTo(0);
            return;
        }

        var prev = _order.Previous(PlaybackList(), Current);
        if (prev is not null)
        {
            await PlayItemAsync(prev, userInitiated: false);
        }
    }

    private async Task PlayItemAsync(QueueItemViewModel item, bool userInitiated)
    {
        if (userInitiated)
        {
            _order.MarkPlayed(item, Current);
        }

        _playCts?.Cancel();
        var cts = _playCts = new CancellationTokenSource();
        _player.Stop();
        Current = item;
        IsPlaying = false;
        Position = 0;
        Duration = item.Track.DurationSeconds;
        _retriedCurrent = false;
        StatusText = $"読み込み中: {item.Title}";

        string? url;
        try
        {
            url = await _client.GetStreamUrlAsync(item.Track, forceRefresh: false, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException)
        {
            StatusText = $"取得に失敗しました: {item.Title} ({ex.Message})";
            await SkipAfterFailureAsync(cts.Token);
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        if (url is null)
        {
            StatusText = $"ストリーミングできない曲です: {item.Title}";
            await SkipAfterFailureAsync(cts.Token);
            return;
        }

        OpenAndPlay(item, url);
    }

    private void OpenAndPlay(QueueItemViewModel item, string url)
    {
        _player.Open(url, new NowPlayingInfo(item.Title, item.Artist, item.Album, NowArtUrl));
        _player.Play();
        IsPlaying = true;
    }

    private async Task OnPlaybackFailedAsync(string message)
    {
        var item = Current;
        if (item is null)
        {
            return;
        }

        var ct = _playCts?.Token ?? CancellationToken.None;

        // ストリーム URL の期限切れの可能性があるので 1 回だけ取り直す。
        // ただしページを取得したばかりなら取り直しても変わらないので、無駄なリクエストは送らない。
        var fetchedAt = _client.TryGetCachedTralbum(item.Track.SourcePageUrl)?.FetchedAt;
        var recentlyFetched = fetchedAt is { } f && DateTimeOffset.UtcNow - f < TimeSpan.FromMinutes(30);
        if (!_retriedCurrent && !recentlyFetched)
        {
            _retriedCurrent = true;
            var url = await _client.GetStreamUrlAsync(item.Track, forceRefresh: true, ct);
            if (url is not null && !ct.IsCancellationRequested && ReferenceEquals(item, Current))
            {
                OpenAndPlay(item, url);
                return;
            }
        }

        StatusText = $"再生に失敗しました: {item.Title} ({message})";
        await SkipAfterFailureAsync(ct);
    }

    private async Task SkipAfterFailureAsync(CancellationToken ct)
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            StatusText += $" — {MaxConsecutiveFailures} 曲連続で失敗したので停止しました";
            _consecutiveFailures = 0;
            IsPlaying = false;
            return;
        }

        await Task.Delay(1500, ct);
        if (!ct.IsCancellationRequested)
        {
            await NextAsync();
        }
    }

    private void StopPlayback()
    {
        _playCts?.Cancel();
        _player.Stop();
        Current = null;
        IsPlaying = false;
        Position = 0;
        Duration = 0;
    }

    private void Tick()
    {
        if (Current is not null && IsPlaying && !IsSeeking)
        {
            Position = _player.Position.TotalSeconds;
            if (_player.Duration is { } d)
            {
                Duration = d.TotalSeconds;
            }
        }

        var pending = _http.PendingCount;
        var text = $"Bandcamp へのリクエスト: 待ち {pending} 件 / 累計 {_http.TotalRequests} 件 / 間隔 {_http.MinInterval.TotalSeconds:0.0} 秒";
        if (_lastHttpMessage is not null)
        {
            text += " — " + _lastHttpMessage;
        }

        RequestStatus = text;
    }

    private static async Task RunSafeAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static List<QueueItemViewModel> Selected(object? parameter) =>
        parameter is IList list ? list.OfType<QueueItemViewModel>().ToList() : [];

    private static void OpenUrl(string? url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    // ---- Chrome 拡張から ----

    public Task BrowseFromExtensionAsync(string url) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            InputUrl = url;
            SelectedTab = 0;
            if (Application.Current.MainWindow is { } w)
            {
                if (w.WindowState == WindowState.Minimized)
                {
                    w.WindowState = WindowState.Normal;
                }

                w.Activate();
            }

            _ = RunSafeAsync(Browse.OpenAsync(url));
        }).Task;

    public Task EnqueueFromExtensionAsync(string url) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusText = "拡張機能から追加: " + url;
            _ = RunSafeAsync(AddUrlAsync(url));
        }).Task;

    // ---- 永続化 ----

    private void LoadQueue()
    {
        try
        {
            if (File.Exists(AppPaths.QueueFile))
            {
                var tracks = JsonSerializer.Deserialize<List<TrackInfo>>(File.ReadAllText(AppPaths.QueueFile)) ?? [];
                AddTracks(tracks);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            StatusText = "キューの読み込みに失敗しました: " + ex.Message;
        }
    }

    public void SaveQueue()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tracks = Queue.OrderBy(q => q.Seq).Select(q => q.Track with { StreamUrl = null }).ToList();
            File.WriteAllText(AppPaths.QueueFile, JsonSerializer.Serialize(tracks));
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        SaveQueue();
        _settings.Save();
        _jobsCts.Cancel();
        _player.Stop();
        _player.Dispose();
        _http.Dispose();
    }
}
