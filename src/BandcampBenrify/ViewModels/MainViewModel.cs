using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BandcampBenrify.Core;
using BandcampBenrify.Infrastructure;
using BandcampBenrify.Services;

namespace BandcampBenrify.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxConsecutiveFailures = 5;

    private readonly AppSettings _settings;
    private readonly ThrottledHttpClient _http;
    private readonly DiskCache _cache;
    private readonly BandcampClient _client;
    private readonly AudioPlayer _player = new(Dispatcher.CurrentDispatcher);
    private readonly PlaybackOrder<QueueItemViewModel> _order = new();
    private readonly HashSet<long> _queuedTrackIds = [];
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource _jobsCts = new();
    private CancellationTokenSource? _playCts;
    private string? _lastHttpMessage;
    private int _consecutiveFailures;
    private bool _retriedCurrent;

    private string _inputUrl = "";
    private string _statusText = "準備完了";
    private string _requestStatus = "";
    private int _selectedTab;
    private QueueItemViewModel? _current;
    private bool _isPlaying;
    private double _position;
    private double _duration;
    private int _activeJobs;
    private string _jobText = "";

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
            ExactCountMaxRequestsPerItem = settings.ExactCountMaxRequestsPerItem,
        };

        Ranking = new RankingViewModel(_client, AddTracks, s => StatusText = s);

        _player.Volume = settings.Volume;
        _order.Shuffle = settings.Shuffle;
        _player.Opened += () =>
        {
            _consecutiveFailures = 0;
            Duration = _player.Duration?.TotalSeconds ?? Current?.Track.DurationSeconds ?? 0;
            StatusText = $"再生中: {Current?.Artist} - {Current?.Title}";
        };
        _player.Ended += () => _ = RunSafeAsync(NextAsync());
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

        AsyncCommand.ErrorHandler = ex => StatusText = "エラー: " + ex.Message;

        AddUrlCommand = new AsyncCommand(
            async () =>
            {
                var url = InputUrl;
                if (await AddUrlAsync(url) > 0 && InputUrl == url)
                {
                    InputUrl = ""; // 成功したら次の URL を貼りやすいように空にする
                }
            },
            () => !string.IsNullOrWhiteSpace(InputUrl));
        ShowRankingCommand = new AsyncCommand(() => ShowRankingAsync(InputUrl), () => !string.IsNullOrWhiteSpace(InputUrl));
        PlayPauseCommand = new AsyncCommand(PlayPauseAsync, () => Queue.Count > 0);
        NextCommand = new AsyncCommand(NextAsync, () => Queue.Count > 0);
        PreviousCommand = new AsyncCommand(PreviousAsync, () => Queue.Count > 0);
        PlayItemCommand = new AsyncCommand(p => p is QueueItemViewModel item ? PlayItemAsync(item, userInitiated: true) : Task.CompletedTask);
        RemoveSelectedCommand = new RelayCommand(p => RemoveItems(p is IList list ? list.OfType<QueueItemViewModel>().ToList() : []), p => p is IList { Count: > 0 });
        ClearQueueCommand = new RelayCommand(() =>
        {
            if (MessageBox.Show($"キューの {Queue.Count} 曲をすべて削除しますか？", "Bandcamp Benrify", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                RemoveItems(Queue.ToList());
            }
        }, () => Queue.Count > 0);
        CancelJobsCommand = new RelayCommand(CancelJobs, () => ActiveJobs > 0);
        OpenInBrowserCommand = new RelayCommand(p =>
        {
            var url = (p as QueueItemViewModel ?? Current)?.Track.TrackUrl;
            if (!string.IsNullOrEmpty(url))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        });
        ClearCacheCommand = new RelayCommand(() =>
        {
            _cache.Clear();
            StatusText = "キャッシュを削除しました";
        });

        LoadQueue();
    }

    public ObservableCollection<QueueItemViewModel> Queue { get; } = [];

    public RankingViewModel Ranking { get; }

    public ICommand AddUrlCommand { get; }

    public ICommand ShowRankingCommand { get; }

    public ICommand PlayPauseCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand PreviousCommand { get; }

    public ICommand PlayItemCommand { get; }

    public ICommand RemoveSelectedCommand { get; }

    public ICommand ClearQueueCommand { get; }

    public ICommand CancelJobsCommand { get; }

    public ICommand OpenInBrowserCommand { get; }

    public ICommand ClearCacheCommand { get; }

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

    public int SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, value);
    }

    public string QueueHeader => $"キュー ({Queue.Count})";

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
                OnPropertyChanged(nameof(WindowTitle));
            }

            if (_current is not null)
            {
                _current.IsPlaying = true;
            }
        }
    }

    public string NowTitle => Current?.Title ?? "再生していません";

    public string NowSubtitle => Current is null ? "" : $"{Current.Artist} — {Current.Album}";

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
                OnPropertyChanged();
            }
        }
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

            JobText = kind == UrlKind.Band
                ? $"追加中 {p.Done}/{p.Total} 作品: {p.Current}"
                : $"取得中: {p.Current}";
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
                ? "再生できる曲が見つかりませんでした (ストリーミング非対応の作品の可能性があります)"
                : $"{added} 曲をキューに追加しました"
                    + (skipped > 0 ? $" ({skipped} 曲は追加済みのためスキップ)" : "")
                    + (noAudio > 0 ? $" (試聴できない作品: {noAudio})" : "")
                    + (errors > 0 ? $" (取得に失敗した作品: {errors})" : "");
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
            ActiveJobs--;
            if (ActiveJobs == 0)
            {
                JobText = "";
            }

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
                var vm = new QueueItemViewModel(track);
                Queue.Add(vm);
                added.Add(vm);
            }
        }

        if (added.Count > 0)
        {
            _order.OnAdded(added);
            OnPropertyChanged(nameof(QueueHeader));
            OnPropertyChanged(nameof(IsQueueEmpty));
            CommandManager.InvalidateRequerySuggested();
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

        OnPropertyChanged(nameof(QueueHeader));
        OnPropertyChanged(nameof(IsQueueEmpty));
        SaveQueue();
    }

    public async Task ShowRankingAsync(string url)
    {
        url = url.Trim();
        if (url.Length == 0)
        {
            return;
        }

        SelectedTab = 1;
        await Ranking.LoadAsync(url);
    }

    private void CancelJobs()
    {
        _jobsCts.Cancel();
        _jobsCts = new CancellationTokenSource();
    }

    // ---- 再生 ----

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
        var next = _order.Next(Queue, Current);
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

        var prev = _order.Previous(Queue, Current);
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

    // ---- Chrome 拡張から ----

    public Task EnqueueFromExtensionAsync(string url) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusText = "拡張機能から追加: " + url;
            _ = RunSafeAsync(AddUrlAsync(url));
        }).Task;

    public Task RankingFromExtensionAsync(string url) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            InputUrl = url;
            BringToFront();
            _ = RunSafeAsync(ShowRankingAsync(url));
        }).Task;

    private static void BringToFront()
    {
        if (Application.Current.MainWindow is { } w)
        {
            if (w.WindowState == WindowState.Minimized)
            {
                w.WindowState = WindowState.Normal;
            }

            w.Activate();
        }
    }

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
            var tracks = Queue.Select(q => q.Track with { StreamUrl = null }).ToList();
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
        Ranking.Cancel();
        _player.Stop();
        _player.Dispose();
        _http.Dispose();
    }
}
