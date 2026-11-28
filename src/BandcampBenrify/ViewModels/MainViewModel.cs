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

    /// <summary>すべてのキュー (保存する中身)。表示しているキューの曲は <see cref="Queue"/> にあり、保存のときに書き戻す。</summary>
    private QueueLibrary _library = QueueLibrary.CreateDefault();

    /// <summary>表示している (曲を追加する先の) キュー。</summary>
    private QueueListItemViewModel _shownQueue = null!;

    /// <summary>再生中の曲を、そのとき表示していたキューから再生し始めたか (試聴ではない)。キューを切り替えても次の曲へ進む。</summary>
    private bool _currentFromQueue;
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
    private readonly CheckedTrackStore _checked;

    /// <summary>曲の音声のダウンロード用 (Bandcamp の配信サーバー。再生 1 回につき 1 リクエスト)。</summary>
    private static readonly HttpClient AudioHttp = CreateAudioHttp();
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly TrackAnalysisStore _analysis;
    private ProgressiveDownload? _download;
    private CancellationTokenSource? _analysisCts;
    private byte[]? _nowPeaks;
    private string _nowBpmText = "";
    private string? _nowBpmToolTip;
    private double? _nowBpm;
    private TrackAnalysis? _nowAnalysis;
    private SpectrumTimeline? _nowSpectrum;

    /// <summary>
    /// 再生中の曲が表示から外れた (チェックして隠した) ときに、次の曲を決めるための「さっきまでの位置」。
    /// </summary>
    private int _currentIndexHint = -1;

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
        _analysis = new TrackAnalysisStore(_cache);

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Queue);
        view.IsLiveSorting = true;
        foreach (var p in new[]
                 {
                     nameof(QueueItemViewModel.TotalSort), nameof(QueueItemViewModel.AlbumSupSort), nameof(QueueItemViewModel.TrackSupSort),
                     nameof(QueueItemViewModel.TrackPriceSort), nameof(QueueItemViewModel.AlbumPriceSort), nameof(QueueItemViewModel.ReleaseDate),
                     nameof(QueueItemViewModel.PurchaseText), nameof(QueueItemViewModel.CheckedSort), nameof(QueueItemViewModel.BpmSort),
                 })
        {
            view.LiveSortingProperties.Add(p);
        }

        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(QueueItemViewModel.IsPurchased));
        view.LiveFilteringProperties.Add(nameof(QueueItemViewModel.IsChecked));
        view.Filter = o => o is QueueItemViewModel q && IsShown(q);
        QueueView = view;

        _checked = new CheckedTrackStore(AppPaths.CheckedFile);

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
            if (MessageBox.Show($"{QueueLabel}の {Queue.Count} 曲をすべて削除しますか？", "Bandcamp Benrify", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                RemoveItems(Queue.ToList());
            }
        }, () => Queue.Count > 0);
        FetchDetailsCommand = new AsyncCommand(
            p => FetchDetailsAsync(Selected(p) is { Count: > 0 } sel ? sel : QueueView.Cast<QueueItemViewModel>().ToList()),
            _ => (PendingPageCount > 0 || PendingPlusCount > 0) && !_fetchingDetails && !Browse.IsFetchingReleases);
        RefreshCollectionCommand = new AsyncCommand(RefreshCollectionAsync, () => HasProfile && !_loadingCollection);
        CancelJobsCommand = new RelayCommand(CancelJobs, () => ActiveJobs > 0);
        OpenInBrowserCommand = new RelayCommand(p => OpenUrl((p as QueueItemViewModel ?? Current)?.Track.TrackUrl));
        OpenAlbumCommand = new RelayCommand(p => OpenUrl(p is QueueItemViewModel q ? TrackDetailsResolver.AlbumUrlOf(q.Track) : null));
        CheckSelectedCommand = new RelayCommand(p => SetChecked(Selected(p), true), p => Selected(p).Any(q => !q.IsChecked));
        UncheckSelectedCommand = new RelayCommand(p => SetChecked(Selected(p), false), p => Selected(p).Any(q => q.IsChecked));
        CheckCurrentAndNextCommand = new AsyncCommand(CheckCurrentAndNextAsync, () => Current is not null);
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        DoubleBpmCommand = new RelayCommand(() => SetManualBpm(NowBpm * 2), () => NowBpm is not null);
        HalveBpmCommand = new RelayCommand(() => SetManualBpm(NowBpm / 2), () => NowBpm is not null);
        ResetBpmCommand = new RelayCommand(() => SetManualBpm(null), () => Current is { } c && _analysis.GetManualBpm(c.Track.TrackId) is not null);
        OpenVisualizerCommand = new RelayCommand(() => VisualizerRequested?.Invoke());
        NewQueueCommand = new RelayCommand(NewQueue);
        RenameQueueCommand = new RelayCommand(p => RenameQueue(p as QueueListItemViewModel ?? _shownQueue));
        DeleteQueueCommand = new RelayCommand(p => DeleteQueue(p as QueueListItemViewModel ?? _shownQueue), _ => Queues.Count > 1);

        LoadQueue();
        LoadCachedCollection();
        UpdatePendingCount();
        _selectedTab = Queue.Count > 0 ? 1 : 0;
        if (string.Join(" / ", new[] { _queueLoadError, _checked.LoadError }.OfType<string>()) is { Length: > 0 } error)
        {
            StatusText = error;
        }
    }

    // ---- チェック済み (使い道はユーザー次第の、曲ごとの印) ----

    /// <summary>右クリック「チェック済みにする」(選択した曲)。</summary>
    public ICommand CheckSelectedCommand { get; }

    /// <summary>右クリック「チェックを外す」(選択した曲)。</summary>
    public ICommand UncheckSelectedCommand { get; }

    /// <summary>再生中の曲をチェック済みにして次の曲へ (Ctrl+D)。</summary>
    public ICommand CheckCurrentAndNextCommand { get; }

    public ICommand ToggleMuteCommand { get; }

    /// <summary>再生中の曲の BPM を手動で 2 倍に直す (自動の推定は 100 と 200 などを取り違えることがあるため)。</summary>
    public ICommand DoubleBpmCommand { get; }

    /// <summary>再生中の曲の BPM を手動で半分に直す。</summary>
    public ICommand HalveBpmCommand { get; }

    /// <summary>手動で直した BPM を消して、自動の推定に戻す。</summary>
    public ICommand ResetBpmCommand { get; }

    public int CheckedCount => _checked.Count;

    public bool IsChecked(long trackId) => _checked.Contains(trackId);

    public bool HideChecked
    {
        get => _settings.HideChecked;
        set
        {
            if (_settings.HideChecked != value)
            {
                RememberCurrentIndex();
                _settings.HideChecked = value;
                _settings.Save();
                OnPropertyChanged();
                QueueView.Refresh();
                OnPropertyChanged(nameof(QueueSummary));
            }
        }
    }

    private bool IsShown(QueueItemViewModel q) => !(HidePurchased && q.IsPurchased) && !(HideChecked && q.IsChecked);

    /// <summary>チェックを付ける / 外して記録し、キューと「探す」の同じ曲の行すべてに反映する。</summary>
    public void SetChecked(IReadOnlyCollection<QueueItemViewModel> items, bool value)
    {
        if (items.Count == 0)
        {
            return;
        }

        // 再生中の曲が隠れても、次はその続きから再生できるように位置を覚えておく
        RememberCurrentIndex();

        int changed;
        try
        {
            changed = _checked.Set(items.Select(i => i.Track), value, DateTimeOffset.Now);
        }
        catch (IOException ex)
        {
            StatusText = "チェック済みの記録を保存できませんでした: " + ex.Message;
            return;
        }

        foreach (var item in Queue.Concat(Browse.Tracks))
        {
            item.SetCheckedState(_checked.Contains(item.Track.TrackId));
        }

        Browse.RefreshCheckedCounts();
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(NowStats));
        OnPropertyChanged(nameof(CheckedCount));
        CommandManager.InvalidateRequerySuggested();

        var first = items.First();
        var what = items.Count == 1 ? $"「{first.Title}」" : $"{changed} 曲";
        StatusText = value
            ? $"{what}をチェック済みにしました (全部で {_checked.Count} 曲)" + (HideChecked ? "。「チェック済みを隠す」がオンなので一覧と再生から外れます" : "")
            : $"{what}のチェックを外しました";
    }

    private async Task CheckCurrentAndNextAsync()
    {
        if (Current is not { } item)
        {
            return;
        }

        var preview = IsPreview;
        SetChecked([item], true);
        if (preview)
        {
            StopPlayback(); // 試聴中の曲はキューに無いので、次へは進まない
        }
        else
        {
            await NextAsync();
        }
    }

    private void RememberCurrentIndex()
    {
        if (CurrentInQueue() is { } current && PlaybackList().IndexOf(current) is var i and >= 0)
        {
            _currentIndexHint = i;
        }
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

    /// <summary>表示しているキューに入っているか (「探す」の曲目の「キュー」列)。</summary>
    public bool IsQueued(long trackId) => _queuedTrackIds.Contains(trackId);

    /// <summary>キューに入っていない曲 (試聴) を再生中か。</summary>
    public bool IsPreview => Current is not null && !_currentFromQueue && !_queuedTrackIds.Contains(Current.Track.TrackId);

    /// <summary>
    /// 表示しているキューの中で、再生中の曲にあたる行。
    /// 別のキューから再生し始めた曲でも、同じ曲がこのキューにあればその行 (無ければ再生中の曲そのもの)。
    /// </summary>
    private QueueItemViewModel? CurrentInQueue()
    {
        if (Current is not { } current || !_queuedTrackIds.Contains(current.Track.TrackId))
        {
            return Current;
        }

        return _playingTwin ?? current;
    }

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
            }
        }
    }

    public ObservableCollection<QueueItemViewModel> Queue { get; } = [];

    /// <summary>並び替え・絞り込み済みのキュー。シャッフルしないときはこの順に再生する。</summary>
    public ICollectionView QueueView { get; }

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
            var shown = Queue.Count(IsShown);
            var total = TimeSpan.FromSeconds(Queue.Sum(q => q.DurationSeconds));
            var text = $"{Queue.Count} 曲 ({(total.TotalHours >= 1 ? $"{(int)total.TotalHours}時間{total.Minutes}分" : $"{total.Minutes}分")})";
            var checkedInQueue = Queue.Count(q => q.IsChecked);
            if (checkedInQueue > 0)
            {
                text += $" / チェック済み {checkedInQueue} 曲";
            }

            return shown != Queue.Count ? $"{text} / 表示 {shown} 曲" : text;
        }
    }

    public bool IsQueueEmpty => Queue.Count == 0;

    public bool HasCurrent => Current is not null;

    /// <summary>キューを切り替えたとき、再生中の曲と同じ曲の行 (再生中の印を付ける)。</summary>
    private QueueItemViewModel? _playingTwin;

    public QueueItemViewModel? Current
    {
        get => _current;
        private set
        {
            if (_current is not null)
            {
                _current.IsPlaying = false;
            }

            if (_playingTwin is not null)
            {
                _playingTwin.IsPlaying = false;
                _playingTwin = null;
            }

            if (Set(ref _current, value))
            {
                OnPropertyChanged(nameof(HasCurrent));
                OnPropertyChanged(nameof(NowTitle));
                OnPropertyChanged(nameof(NowSubtitle));
                OnPropertyChanged(nameof(NowArtUrl));
                OnPropertyChanged(nameof(NowLargeArtUrl));
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

    public string NowSubtitle => Current is null
        ? ""
        : $"{(IsPreview ? "試聴中 · " : "")}{Current.Artist} — {Current.Album}" + (Current.Details.Label is { Length: > 0 } label ? $"  [{label}]" : "");

    /// <summary>再生中の曲のサポーター数と値段。</summary>
    public string NowStats => Current is null
        ? ""
        : $"サポーター {Current.TotalText} (アルバム {Current.AlbumSupText} / 曲 {Current.TrackSupText})   曲 {Current.TrackPriceText}   アルバム {Current.AlbumPriceText}"
          + (Current.IsPurchased ? $"   {Current.PurchaseText}購入済み" : "")
          + (Current.IsChecked ? "   ✓ チェック済み" : "");

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
            }
        }
    }

    public bool HidePurchased
    {
        get => _settings.HidePurchased;
        set
        {
            if (_settings.HidePurchased != value)
            {
                RememberCurrentIndex();
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

    public long CacheSize(CacheKind kind) => _client.CacheSize(kind);

    /// <summary>種類を指定してキャッシュを消し、表示 (サポーター数・購入済みマークなど) を消した状態に合わせる。</summary>
    public void ClearCache(CacheKind kind)
    {
        _client.ClearCache(kind);
        switch (kind)
        {
            case CacheKind.Pages:
                foreach (var item in Queue)
                {
                    RefreshItem(item);
                }

                Browse.RefreshAll();
                UpdatePendingCount();
                StatusText = "作品ページのキャッシュを削除しました";
                break;
            case CacheKind.Discographies:
                StatusText = "作品一覧のキャッシュを削除しました";
                break;
            case CacheKind.Collection:
                LoadCachedCollection();
                StatusText = "コレクションのキャッシュを削除しました (購入済みマークは「コレクション更新」で戻せます)";
                break;
            case CacheKind.Analysis:
                _analysis.Clear();
                foreach (var row in Queue.Concat(Browse.Tracks))
                {
                    row.SetBpm(false, null);
                }

                StatusText = "曲の解析結果のキャッシュを削除しました (次に再生したときに解析し直します)";
                break;
        }
    }

    /// <summary>読み込み済みのコレクションの件数 (キャッシュ削除の確認に使う)。</summary>
    public int CachedCollectionCount => _client.TryGetCachedCollection(BandcampUsername)?.Count ?? 0;

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
            OnPropertyChanged(nameof(VolumeGlyph));

            // 消音中に音量を動かしたら、消音は解除する
            if (IsMuted && value > 0)
            {
                IsMuted = false;
            }
        }
    }

    /// <summary>消音 (音量の値は覚えたまま)。アプリを閉じたら戻る。</summary>
    public bool IsMuted
    {
        get => _player.IsMuted;
        set
        {
            if (_player.IsMuted != value)
            {
                _player.IsMuted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VolumeGlyph));
                OnPropertyChanged(nameof(MuteToolTip));
            }
        }
    }

    /// <summary>音量アイコン (消音中は斜線、それ以外は音量に応じて波の数が変わる)。</summary>
    public string VolumeGlyph => IsMuted || Volume <= 0 ? "" : Volume < 0.34 ? "" : Volume < 0.67 ? "" : "";

    public string MuteToolTip => IsMuted ? "消音を解除 (Ctrl+M)" : "消音 (Ctrl+M)";

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

    /// <summary>再生中の曲の波形 (0.1 秒ごとの音量)。解析中は届いた分だけ、未解析なら null。</summary>
    public byte[]? NowPeaks
    {
        get => _nowPeaks;
        private set => Set(ref _nowPeaks, value);
    }

    public string NowBpmText
    {
        get => _nowBpmText;
        private set
        {
            if (Set(ref _nowBpmText, value))
            {
                OnPropertyChanged(nameof(HasNowBpmText));
            }
        }
    }

    public bool HasNowBpmText => NowBpmText.Length > 0;

    /// <summary>推定 BPM (未解析・推定できなかったときは null)。</summary>
    public double? NowBpm
    {
        get => _nowBpm;
        private set => Set(ref _nowBpm, value);
    }

    /// <summary>再生中の曲の音域ごとの音量の推移 (ビジュアライザ用。解析しながら増えていく)。</summary>
    public SpectrumTimeline? NowSpectrum
    {
        get => _nowSpectrum;
        private set => Set(ref _nowSpectrum, value);
    }

    /// <summary>今の再生位置 (秒)。ビジュアライザが毎フレーム読む (Position は 0.5 秒ごとにしか更新しない)。</summary>
    public double PlaybackSeconds => Current is null ? 0 : _player.Position.TotalSeconds;

    /// <summary>ビジュアライザで大きく表示するジャケット (700px)。</summary>
    public string? NowLargeArtUrl => Current?.Track.ArtId is long id ? BandcampUrls.ArtUrl(id, ArtSize.Large) : null;

    /// <summary>ビジュアライザを開く (ウィンドウは画面側で作る)。</summary>
    public event Action? VisualizerRequested;

    public ICommand OpenVisualizerCommand { get; }

    public string? NowBpmToolTip
    {
        get => _nowBpmToolTip;
        private set => Set(ref _nowBpmToolTip, value);
    }

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

        if (BandcampUrls.ParseFanList(normalized) is not null)
        {
            // 何千件もあることがあるので、まとめてキューには入れない。「探す」で見て選んでもらう
            StatusText = "ファンのコレクション / ウィッシュリストは「探す」で開いて、作品を選んでからキューに入れてください";
            return 0;
        }

        var kind = BandcampUrls.Classify(normalized);
        var target = QueueTarget; // 追加の途中でキューを切り替えても、始めたときのキューに入れる
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
                added += AddTracks([track], target);
            }

            var skipped = found - added;
            StatusText = found == 0
                ? "再生できる曲が見つかりませんでした (試聴が公開されていない作品の可能性があります)"
                : $"{added} 曲を{QueueLabelOf(target)}に追加しました"
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

    /// <summary>
    /// キューに曲を追加する。既に入っている曲はスキップ。追加数を返す。
    /// <paramref name="target"/> を省くと表示しているキューに入れる。表示していないキュー (や消したキュー) なら、保存する中身にだけ足す。
    /// </summary>
    public int AddTracks(IEnumerable<TrackInfo> tracks, SavedQueue? target = null)
    {
        if (target is not null && !ReferenceEquals(target, _shownQueue.Saved))
        {
            return AddToHiddenQueue(tracks, target);
        }

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
            if (ReferenceEquals(item, Current) || ReferenceEquals(item, _playingTwin))
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

    private double _queueMaxSupporters = 1;

    /// <summary>
    /// サポーター数の棒の長さの基準 (これ以上は棒が最大)。
    /// いちばん多い曲に合わせると、1 曲だけ飛び抜けているときに他の棒が全部短くなるので、多い方から 1 割の位置の数にする。
    /// </summary>
    public double QueueMaxSupporters
    {
        get => _queueMaxSupporters;
        private set => Set(ref _queueMaxSupporters, value);
    }

    private void UpdateQueueMaxSupporters()
    {
        var values = Queue.Select(q => q.TotalSort).Where(v => v > 0).Order().ToList();
        QueueMaxSupporters = values.Count == 0 ? 1 : Math.Max(1, values[(int)((values.Count - 1) * 0.9)]);
    }

    private void OnQueueChanged()
    {
        UpdateQueueMaxSupporters();
        SchedulePendingUpdate();
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(IsQueueEmpty));
        OnPropertyChanged(nameof(NowSubtitle));
        _shownQueue.Count = Queue.Count;
        Browse.RefreshQueuedFlags();
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- 複数のキュー ----

    /// <summary>すべてのキュー (作った順)。</summary>
    public ObservableCollection<QueueListItemViewModel> Queues { get; } = [];

    /// <summary>表示している (曲を追加する先の) キュー。切り替えても再生中の曲はそのまま流れ、次の曲からこのキューで続ける。</summary>
    public QueueListItemViewModel SelectedQueue
    {
        get => _shownQueue;
        set
        {
            if (value is null || ReferenceEquals(value, _shownQueue) || !Queues.Contains(value))
            {
                return;
            }

            StoreShownTracks();
            ShowQueue(value);
            SaveQueue();
        }
    }

    /// <summary>
    /// 行き先を選ばない追加 (Chrome 拡張・コマンドライン・「探す」の Ctrl+Enter) で曲を入れるキュー: 表示しているキュー。
    /// 時間のかかる追加は、始めたときのキューに入れるためにこれを覚えておく。
    /// </summary>
    public SavedQueue QueueTarget => _shownQueue.Saved;

    /// <summary>メッセージ用の「キュー」/「「名前」」(キューが 1 つだけなら名前を出さない)。</summary>
    public string QueueLabel => QueueLabelOf(_shownQueue.Saved);

    public string QueueLabelOf(SavedQueue queue) => Queues.Count > 1 ? $"「{queue.Name}」" : "キュー";

    public ICommand NewQueueCommand { get; }

    public ICommand RenameQueueCommand { get; }

    public ICommand DeleteQueueCommand { get; }

    private void ShowQueue(QueueListItemViewModel queue)
    {
        _shownQueue = queue;
        Queue.Clear();
        _queuedTrackIds.Clear();
        _nextSeq = 0;
        _order.Reset();
        _currentIndexHint = -1;
        if (_playingTwin is not null)
        {
            _playingTwin.IsPlaying = false;
            _playingTwin = null;
        }

        AddTracks(queue.Saved.Tracks);

        // 別のキューから流れている曲が、このキューにもあればその行に再生中の印を付け、次の曲もその位置から続ける
        if (Current is { } current && !Queue.Contains(current)
            && Queue.FirstOrDefault(q => q.Track.TrackId == current.Track.TrackId) is { } twin)
        {
            _playingTwin = twin;
            twin.IsPlaying = true;
        }

        RememberCurrentIndex();
        OnQueueChanged();
        UpdatePendingCount();
        OnPropertyChanged(nameof(SelectedQueue));
        OnPropertyChanged(nameof(IsPreview));
        OnPropertyChanged(nameof(NowSubtitle));
    }

    /// <summary>表示しているキューの曲 (追加した順) を、保存する中身に書き戻す。</summary>
    private void StoreShownTracks()
    {
        _shownQueue.Saved.Tracks = Queue.OrderBy(q => q.Seq).Select(q => q.Track with { StreamUrl = null }).ToList();
        _library.SelectedId = _shownQueue.Saved.Id;
    }

    private int AddToHiddenQueue(IEnumerable<TrackInfo> tracks, SavedQueue target)
    {
        if (!_library.Queues.Contains(target))
        {
            return 0; // 追加の途中で消されたキュー
        }

        var ids = target.Tracks.Select(t => t.TrackId).ToHashSet();
        var added = 0;
        foreach (var track in tracks)
        {
            if (ids.Add(track.TrackId))
            {
                target.Tracks.Add(track with { StreamUrl = null });
                added++;
            }
        }

        if (Queues.FirstOrDefault(q => ReferenceEquals(q.Saved, target)) is { } item)
        {
            item.Count = target.Tracks.Count;
        }

        return added;
    }

    /// <summary>曲を指定したキューに入れて保存し、結果をステータスに出す。追加数を返す。</summary>
    public int AddTracksTo(IReadOnlyCollection<TrackInfo> tracks, SavedQueue target, string? note = null)
    {
        var added = AddTracks(tracks, target);
        SaveQueue();
        StatusText = $"{added} 曲を{QueueLabelOf(target)}に追加しました"
                     + (tracks.Count > added ? $" ({tracks.Count - added} 曲は追加済み)" : "")
                     + (note ?? "");
        return added;
    }

    /// <summary>キューの右クリック: 選んだ曲を別のキューにも入れる (<paramref name="move"/> なら、このキューからは消す)。</summary>
    public void CopyToQueue(IReadOnlyCollection<QueueItemViewModel> items, SavedQueue target, bool move)
    {
        if (items.Count == 0 || ReferenceEquals(target, _shownQueue.Saved))
        {
            return;
        }

        var tracks = items.OrderBy(q => q.Seq).Select(q => q.Track).ToList();
        var added = AddTracks(tracks, target);
        if (move)
        {
            RemoveItems(items); // 保存もする
        }
        else
        {
            SaveQueue();
        }

        StatusText = $"{added} 曲を「{target.Name}」に{(move ? "移動しました" : "追加しました")}"
                     + (tracks.Count > added ? $" ({tracks.Count - added} 曲は「{target.Name}」に入っていたので{(move ? "このキューから外しました" : "追加しませんでした")})" : "");
    }

    private void OnQueuesChanged()
    {
        OnPropertyChanged(nameof(QueueLabel));
        OnPropertyChanged(nameof(HasManyQueues));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>キューが 2 つ以上あるか (1 つだけのときはキューの名前や追加先を目立たせない)。</summary>
    public bool HasManyQueues => Queues.Count > 1;

    /// <summary>名前を聞いて新しいキューを作る (表示は切り替えない)。キャンセルしたら null。</summary>
    public SavedQueue? CreateQueue()
    {
        if (NamePrompt.Ask("新しいキュー", "キューの名前", _library.NextDefaultName()) is not { } name)
        {
            return null;
        }

        var saved = new SavedQueue { Name = name };
        _library.Queues.Add(saved);
        Queues.Add(new QueueListItemViewModel(saved));
        OnQueuesChanged();
        SaveQueue();
        return saved;
    }

    /// <summary>「新しいキュー」ボタン: 作ったキューを表示する。</summary>
    private void NewQueue()
    {
        if (CreateQueue() is not { } saved)
        {
            return;
        }

        var item = Queues.First(q => ReferenceEquals(q.Saved, saved));
        SelectedQueue = item;
        SelectedTab = 1;
        StatusText = $"キュー「{saved.Name}」を作りました。「探す」で曲を選んで「キューに追加」から入れられます";
    }

    private void RenameQueue(QueueListItemViewModel queue)
    {
        if (NamePrompt.Ask("キューの名前を変更", "キューの名前", queue.Name) is not { } name || name == queue.Name)
        {
            return;
        }

        queue.Name = name;
        OnQueuesChanged();
        SaveQueue();
    }

    private void DeleteQueue(QueueListItemViewModel queue)
    {
        if (Queues.Count <= 1 || !Queues.Contains(queue))
        {
            return;
        }

        var count = ReferenceEquals(queue, _shownQueue) ? Queue.Count : queue.Saved.Tracks.Count;
        if (MessageBox.Show(
                $"キュー「{queue.Name}」({count} 曲) を削除しますか？\n元に戻せません (チェック済みの印は残ります)。",
                "Bandcamp Benrify", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        if (ReferenceEquals(queue, _shownQueue))
        {
            var index = Queues.IndexOf(queue);
            ShowQueue(Queues[index + 1 < Queues.Count ? index + 1 : index - 1]);
        }

        Queues.Remove(queue);
        _library.Queues.Remove(queue.Saved);
        OnQueuesChanged();
        SaveQueue();
        StatusText = $"キュー「{queue.Name}」を削除しました";
    }

    // ---- 曲ごとのサポーター数・値段 ----

    /// <summary>キャッシュ済みのページからサポーター数・値段・購入済みを計算し直す (リクエストは送らない)。</summary>
    public void RefreshItem(QueueItemViewModel item)
    {
        var details = TrackDetailsResolver.Resolve(item.Track, _client.TryGetCachedTralbum);
        item.Update(details);
        item.Purchase = TrackDetailsResolver.Purchased(item.Track, details, _ownedAlbums, _ownedTracks);
        item.IsQueued = IsQueued(item.Track.TrackId);
        item.CheckRequested ??= (row, value) => SetChecked([row], value);
        item.SetCheckedState(_checked.Contains(item.Track.TrackId));
        item.SetBpm(_analysis.TryGetBpm(item.Track.TrackId, out var bpm), bpm);
        if (Queue.Contains(item))
        {
            UpdateQueueMaxSupporters();
        }

        if (ReferenceEquals(item, Current))
        {
            OnPropertyChanged(nameof(NowStats));
            OnPropertyChanged(nameof(NowSubtitle));
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

        if (!ConfirmManyRequests(urls.Count, plusCount))
        {
            return;
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
                    // 古い版で保存したページ (レーベル不明) はキャッシュが新しくても取り直す。数えたサポーター数は引き継がれる
                    var stale = _client.TryGetCachedTralbum(url) is { Label: null };
                    await _client.GetTralbumAsync(url, RequestPriority.Background, forceRefresh: stale, ct: ct);
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
            var count = await CountExactSupportersAsync(PagesWithMoreSupporters(targets), ct);
            StatusText = FetchResultText(done, count.Counted, count.Capped, errors + count.Errors);
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

    /// <summary>
    /// 量が多いときに確認する (人数を数える分は 1 作品 1 回以上なので最低限の見積もり)。続けてよければ true。
    /// キューと「探す」で共通。
    /// </summary>
    public bool ConfirmManyRequests(int pages, int plusCount)
    {
        var estimate = pages + plusCount;
        if (estimate <= ConfirmRequestThreshold)
        {
            return true;
        }

        var eta = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * estimate);
        return MessageBox.Show(
                   $"{pages} ページの取得と {plusCount} 作品のサポーター集計で、少なくとも {estimate} 回 Bandcamp にアクセスします (約 {Format.Eta(eta)} 以上)。\n途中で中止でき、取得した分は保存されます。続けますか？",
                   "Bandcamp Benrify", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    /// <summary>
    /// 「N+」の作品のサポーターを「もっと見る」と同じ方法で数える (100 人 1 リクエスト、1 作品あたり上限あり)。
    /// 進み具合はジョブ欄に出す。キューと「探す」で共通。
    /// </summary>
    public async Task<(int Counted, int Capped, int Errors)> CountExactSupportersAsync(IReadOnlyList<TralbumPage> pages, CancellationToken ct)
    {
        var counted = 0;
        var capped = 0;
        var errors = 0;
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            var label = $"正確なサポーター数を集計中 {counted}/{pages.Count} 作品: {page.Title}";
            ReportJob(label, (double)counted / pages.Count);
            var progress = new Progress<int>(n => ReportJob($"{label} ({n} 人まで数えました)", (double)counted / pages.Count));
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

        return (counted, capped, errors);
    }

    /// <summary>取得・集計が終わったときのステータス表示。</summary>
    public static string FetchResultText(int pages, int counted, int capped, int errors) =>
        (pages > 0 ? $"{pages} ページを取得" : "")
        + (pages > 0 && counted > 0 ? "、" : "")
        + (counted > 0 ? $"{counted} 作品のサポーター数を集計" : "")
        + (pages > 0 || counted > 0 ? "しました" : "取得するものはありませんでした")
        + (capped > 0 ? $" (サポーターが特に多い {capped} 作品は上限まで数えたので「+」のまま。もう一度押すと続きから数えます)" : "")
        + (errors > 0 ? $" (失敗 {errors})" : "");

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
        var list = PlaybackList();
        QueueItemViewModel? next;
        var anchor = CurrentInQueue();
        if (!Shuffle && anchor is { } current && !list.Contains(current) && _currentIndexHint >= 0 && list.Count > 0)
        {
            // 再生中の曲がチェックなどで表示から外れた: 外れる前の位置にいま並んでいる曲 (= 元の次の曲) へ
            next = list[_currentIndexHint % list.Count];
            _order.MarkPlayed(next, current);
        }
        else
        {
            next = _order.Next(list, anchor);
        }

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

        var prev = _order.Previous(PlaybackList(), CurrentInQueue());
        if (prev is not null)
        {
            await PlayItemAsync(prev, userInitiated: false);
        }
    }

    private async Task PlayItemAsync(QueueItemViewModel item, bool userInitiated)
    {
        if (userInitiated)
        {
            _order.MarkPlayed(item, CurrentInQueue());
        }

        _playCts?.Cancel();
        var cts = _playCts = new CancellationTokenSource();
        _player.Stop();
        ReleaseDownload();
        _currentFromQueue = Queue.Contains(item);
        Current = item;
        RememberCurrentIndex();
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

        await OpenAndPlayAsync(item, url, cts.Token);
    }

    /// <summary>
    /// 音声を 1 回だけダウンロードし、届いた分から再生と解析 (波形・BPM) の両方に回す。
    /// 再生はダウンロードの完了を待たずに始まる。
    /// </summary>
    private async Task OpenAndPlayAsync(QueueItemViewModel item, string url, CancellationToken ct)
    {
        _player.Stop();
        ReleaseDownload();
        ProgressiveDownload? download;
        try
        {
            download = await ProgressiveDownload.StartAsync(AudioHttp, url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (ReferenceEquals(item, Current))
            {
                await OnPlaybackFailedAsync(ex is OperationCanceledException ? "タイムアウト" : ex.Message);
            }

            return;
        }

        if (ct.IsCancellationRequested || !ReferenceEquals(item, Current))
        {
            download?.Dispose();
            return;
        }

        var info = new NowPlayingInfo(item.Title, item.Artist, item.Album, NowArtUrl);
        if (download is null)
        {
            // 長さが分からない応答 (通常は無い): 解析はせず、これまでどおり URL から再生する
            _player.Open(url, info);
        }
        else
        {
            _download = download;
            _player.Open(download.OpenReader(), info);
            StartAnalysis(item, download);
        }

        _player.Play();
        IsPlaying = true;
    }

    /// <summary>解析済みならそれを出し、まだなら同じダウンロードを読みながら裏で解析する。</summary>
    private void StartAnalysis(QueueItemViewModel item, ProgressiveDownload download)
    {
        _analysisCts?.Cancel();
        var cts = _analysisCts = new CancellationTokenSource();
        var trackId = item.Track.TrackId;

        // 解析済みの曲でも、ビジュアライザ用の音域ごとの音量はメモリにしか無いので解析し直す (ダウンロードは再生と共用)
        var cached = _analysis.TryGet(trackId);
        // 古いやり方で解析した結果 (BPM の求め方を変える前) なら、解析し直した結果で置き換える
        var upToDate = cached is { Version: >= TrackAnalysis.CurrentVersion };
        if (cached is not null)
        {
            ShowAnalysis(cached);
        }
        else
        {
            NowPeaks = null;
            NowBpmText = "BPM 解析中…";
            NowBpmToolTip = null;
        }

        var reader = download.OpenReader();
        _ = Task.Run(() =>
        {
            try
            {
                var result = AudioAnalyzer.AnalyzeMp3(
                    reader,
                    cached is not null ? null : peaks => _ui.BeginInvoke(() =>
                    {
                        if (!cts.IsCancellationRequested)
                        {
                            NowPeaks = peaks;
                        }
                    }),
                    cts.Token,
                    spectrum => _ui.BeginInvoke(() =>
                    {
                        if (!cts.IsCancellationRequested)
                        {
                            NowSpectrum = spectrum;
                        }
                    }));
                if (upToDate)
                {
                    return;
                }

                _ui.BeginInvoke(() =>
                {
                    _analysis.Save(trackId, result);
                    _analysis.TryGetBpm(trackId, out var shown); // 手動で直していればその値
                    foreach (var row in Queue.Concat(Browse.Tracks).Where(r => r.Track.TrackId == trackId))
                    {
                        row.SetBpm(true, shown);
                    }

                    if (!cts.IsCancellationRequested)
                    {
                        ShowAnalysis(result);
                    }
                });
            }
            catch (Exception ex)
            {
                // 曲を変えた (ダウンロードを捨てた) / 読めない mp3 など。再生には影響させない
                _ui.BeginInvoke(() =>
                {
                    if (!cts.IsCancellationRequested && cached is null)
                    {
                        NowBpmText = "";
                        NowBpmToolTip = null;
                        Debug.WriteLine($"解析できませんでした: {ex.Message}");
                    }
                });
            }
            finally
            {
                reader.Dispose();
            }
        });
    }

    private void ShowAnalysis(TrackAnalysis analysis)
    {
        _nowAnalysis = analysis;
        NowPeaks = analysis.Peaks;
        ShowBpm();
    }

    /// <summary>再生中の曲の BPM を出す (手動で直していればその値、無ければ自動の推定)。</summary>
    private void ShowBpm()
    {
        if (_nowAnalysis is not { } analysis || Current is not { } item)
        {
            return;
        }

        var manual = _analysis.GetManualBpm(item.Track.TrackId);
        NowBpm = manual ?? analysis.Bpm;
        if (manual is { } m)
        {
            NowBpmText = $"{Math.Round(m):0} BPM";
            NowBpmToolTip = $"手動で直した BPM: {m:0.#}" + (analysis.Bpm is { } auto ? $" (自動の推定: {auto:0.0})" : "")
                + "\n右クリックで 2 倍 / 半分 / 自動の推定に戻す";
        }
        else if (analysis.Bpm is { } bpm)
        {
            NowBpmText = $"{Math.Round(bpm):0} BPM";
            NowBpmToolTip = $"推定 BPM: {bpm:0.0} (確からしさ {analysis.BpmConfidence:0.00})\n曲から自動で推定した目安です。倍や半分になっていることがあります (右クリックで 2 倍 / 半分に直せます)";
        }
        else
        {
            NowBpmText = "BPM —";
            NowBpmToolTip = "テンポがはっきりしないため、BPM を推定できませんでした";
        }

        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>再生中の曲の BPM を手動で直す (null で自動の推定に戻す)。キューと「探す」の BPM 列も直す。</summary>
    private void SetManualBpm(double? bpm)
    {
        if (Current is not { } item)
        {
            return;
        }

        var trackId = item.Track.TrackId;
        _analysis.SetManualBpm(trackId, bpm is { } b ? Math.Round(Math.Clamp(b, 20, 400), 1) : null);
        var known = _analysis.TryGetBpm(trackId, out var shown);
        foreach (var row in Queue.Concat(Browse.Tracks).Where(r => r.Track.TrackId == trackId))
        {
            row.SetBpm(known, shown);
        }

        ShowBpm();
    }

    /// <summary>再生中の曲のダウンロードと解析をやめる (再生を止めてから呼ぶ)。</summary>
    private void ReleaseDownload()
    {
        _analysisCts?.Cancel();
        _analysisCts = null;
        _download?.Dispose();
        _download = null;
        _nowAnalysis = null;
        NowPeaks = null;
        NowBpm = null;
        NowSpectrum = null;
        NowBpmText = "";
        NowBpmToolTip = null;
    }

    private static HttpClient CreateAudioHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            // 応答ヘッダーが届くまでの時間 (本体は再生しながら読む)
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ThrottledHttpClient.UserAgent);
        return http;
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
                await OpenAndPlayAsync(item, url, ct);
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
        ReleaseDownload();
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

    public static async Task RunSafeAsync(Task task)
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

    /// <summary>キューのファイルを読めなかったときのメッセージ (起動時に表示する)。</summary>
    private string? _queueLoadError;

    /// <summary>読めなかったファイルを退避できなかったとき。空のキューで上書きしないよう保存を止める。</summary>
    private bool _queueSaveBlocked;

    /// <summary>
    /// queues.json を読む。まだ無ければ、キューが 1 つしか無かった頃の queue.json を「キュー」として取り込む
    /// (queue.json は書き換えも削除もしない)。
    /// </summary>
    private void LoadQueue()
    {
        var result = QueueLibraryStore.Load(AppPaths.QueuesFile, AppPaths.QueueFile, DateTime.Now);
        _library = result.Library;
        _queueLoadError = result.Error;
        _queueSaveBlocked = result.SaveBlocked;
        foreach (var saved in _library.Queues)
        {
            Queues.Add(new QueueListItemViewModel(saved));
        }

        var selected = _library.Selected;
        ShowQueue(Queues.First(q => ReferenceEquals(q.Saved, selected)));
        OnQueuesChanged();
        if (result.Migrated)
        {
            SaveQueue();
        }
    }

    public void SaveQueue()
    {
        StoreShownTracks();
        if (_queueSaveBlocked)
        {
            return;
        }

        try
        {
            QueueLibraryStore.Save(AppPaths.QueuesFile, _library);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "キューを保存できませんでした: " + ex.Message;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        SaveQueue();
        _settings.Save();
        _jobsCts.Cancel();
        _player.Stop();
        ReleaseDownload();
        _player.Dispose();
        _http.Dispose();
    }
}
