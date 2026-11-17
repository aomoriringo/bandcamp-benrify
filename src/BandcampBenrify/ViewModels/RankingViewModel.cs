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

/// <summary>レーベル / アーティストの作品をサポーター数順に並べるタブ。</summary>
public sealed class RankingViewModel : ObservableObject
{
    private readonly BandcampClient _client;
    private readonly Func<IEnumerable<TrackInfo>, int> _addToQueue;
    private readonly Action<string> _status;
    private CancellationTokenSource? _cts;
    private string _title = "レーベル / アーティストの URL を入力して「ランキング」を押してください";
    private string? _sourceUrl;
    private int _done;
    private int _total;
    private bool _isBusy;
    private string _progressText = "";
    private int _typeFilter;

    public RankingViewModel(BandcampClient client, Func<IEnumerable<TrackInfo>, int> addToQueue, Action<string> status)
    {
        _client = client;
        _addToQueue = addToQueue;
        _status = status;

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        view.SortDescriptions.Add(new SortDescription(nameof(RankingRowViewModel.SortKey), ListSortDirection.Descending));
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(RankingRowViewModel.SortKey));
        view.LiveSortingProperties.Add(nameof(RankingRowViewModel.ReleaseDate));
        view.LiveSortingProperties.Add(nameof(RankingRowViewModel.TrackCount));
        view.Filter = o => o is RankingRowViewModel r && TypeFilter switch
        {
            1 => r.Item.Type == ItemType.Album,
            2 => r.Item.Type == ItemType.Track,
            _ => true,
        };
        RowsView = view;

        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        RefreshCommand = new AsyncCommand(() => _sourceUrl is null ? Task.CompletedTask : LoadAsync(_sourceUrl, forceRefresh: true), () => _sourceUrl is not null && !IsBusy);
        CountExactCommand = new AsyncCommand(p => CountExactAsync(Selected(p)), _ => !IsBusy && Rows.Any(r => r.HasMoreSupporters));
        AddSelectedCommand = new AsyncCommand(p => AddToQueueAsync(Selected(p)), p => p is IList { Count: > 0 });
        AddAllCommand = new AsyncCommand(() => AddToQueueAsync(RowsView.Cast<RankingRowViewModel>().ToList()), () => Rows.Count > 0);
        OpenCommand = new RelayCommand(p =>
        {
            foreach (var row in Selected(p).Take(10))
            {
                Process.Start(new ProcessStartInfo(row.Item.Url) { UseShellExecute = true });
            }
        });
    }

    public ObservableCollection<RankingRowViewModel> Rows { get; } = [];

    public ICollectionView RowsView { get; }

    public ICommand CancelCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand CountExactCommand { get; }

    public ICommand AddSelectedCommand { get; }

    public ICommand AddAllCommand { get; }

    public ICommand OpenCommand { get; }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int Done
    {
        get => _done;
        private set => Set(ref _done, value);
    }

    public int Total
    {
        get => _total;
        private set => Set(ref _total, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>0: すべて / 1: アルバムのみ / 2: 曲のみ</summary>
    public int TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (Set(ref _typeFilter, value))
            {
                RowsView.Refresh();
            }
        }
    }

    public async Task LoadAsync(string url, bool forceRefresh = false)
    {
        Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        IsBusy = true;
        _sourceUrl = url;
        Rows.Clear();
        Done = 0;
        Total = 0;
        try
        {
            ProgressText = "作品一覧を取得中…";
            var disco = await _client.GetDiscographyAsync(url, RequestPriority.Interactive, forceRefresh, ct);
            Title = $"{disco.BandName} — {disco.Items.Count} 作品";
            var toFetch = new List<RankingRowViewModel>();
            foreach (var item in disco.Items)
            {
                var row = new RankingRowViewModel(item);
                if (!forceRefresh && _client.TryGetFreshTralbum(item.Url) is { } fresh)
                {
                    row.Page = fresh;
                    row.State = RowState.Loaded;
                }
                else
                {
                    // 期限切れのキャッシュでもひとまず表示し、順番が来たら更新する
                    row.Page = forceRefresh ? null : _client.TryGetCachedTralbum(item.Url);
                    toFetch.Add(row);
                }

                Rows.Add(row);
            }

            Total = Rows.Count;
            Done = Total - toFetch.Count;
            UpdateRanks();

            var errors = 0;
            for (var i = 0; i < toFetch.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = toFetch[i];
                var remaining = TimeSpan.FromTicks(_client.EstimatedTimePerRequest.Ticks * (toFetch.Count - i));
                ProgressText = $"サポーター数を取得中 {Done}/{Total} (残り約 {FormatEta(remaining)}): {row.Title}";
                row.State = RowState.Loading;
                try
                {
                    row.Page = await _client.GetTralbumAsync(row.Item.Url, RequestPriority.Background, forceRefresh, ct);
                    row.State = RowState.Loaded;
                }
                catch (Exception ex) when (ex is HttpRequestException or BandcampParseException or JsonException)
                {
                    row.Error = ex.Message;
                    row.State = RowState.Error;
                    errors++;
                }

                Done++;
                UpdateRanks();
            }

            var more = Rows.Count(r => r.HasMoreSupporters);
            var noAudio = Rows.Count(r => r.Page is not null && !r.HasAudio);
            ProgressText = $"完了: {Total} 作品" + (errors > 0 ? $" (エラー {errors})" : "") + (noAudio > 0 ? $" / 試聴不可 {noAudio}" : "")
                + (more > 0 ? $"。「N+」の {more} 作品は「正確な人数を数える」で追加取得できます" : "");
        }
        catch (OperationCanceledException)
        {
            ProgressText = $"中止しました ({Done}/{Total})";
            foreach (var row in Rows.Where(r => r.State == RowState.Loading))
            {
                row.State = row.Page is null ? RowState.Pending : RowState.Loaded;
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>「N+」になっている作品のサポーターを追加で数える。選択がなければ全件 (人数の多い順)。</summary>
    private async Task CountExactAsync(IReadOnlyList<RankingRowViewModel> selected)
    {
        var targets = (selected.Count > 0 ? selected : RowsView.Cast<RankingRowViewModel>())
            .Where(r => r.Page is not null && r.HasMoreSupporters)
            .OrderByDescending(r => r.SortKey)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        var i = 0;
        try
        {
            foreach (var row in targets)
            {
                cts.Token.ThrowIfCancellationRequested();
                ProgressText = $"正確な人数を集計中 {i}/{targets.Count}: {row.Title} (1 作品あたり最大 {_client.ExactCountMaxRequestsPerItem} リクエスト)";
                row.State = RowState.Counting;
                try
                {
                    row.Page = await _client.CountSupportersAsync(row.Page!, cts.Token);
                    row.State = RowState.Loaded;
                    UpdateRanks();
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException)
                {
                    row.Error = ex.Message;
                    row.State = RowState.Error;
                }

                i++;
            }

            ProgressText = $"集計完了: {targets.Count} 作品";
        }
        catch (OperationCanceledException)
        {
            ProgressText = $"集計を中止しました ({i}/{targets.Count})";
            foreach (var row in targets.Where(r => r.State == RowState.Counting))
            {
                row.State = RowState.Loaded;
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsBusy = false;
            }
        }
    }

    private async Task AddToQueueAsync(IReadOnlyList<RankingRowViewModel> rows)
    {
        var added = 0;
        foreach (var row in rows)
        {
            var page = row.Page ?? await _client.GetTralbumAsync(row.Item.Url, RequestPriority.Interactive);
            row.Page ??= page;
            added += _addToQueue(page.Tracks.Where(t => t.StreamUrl is not null));
        }

        _status($"{rows.Count} 作品から {added} 曲をキューに追加しました");
    }

    /// <summary>サポーター数での順位を振り直す (表示中の並び順とは独立)。</summary>
    private void UpdateRanks()
    {
        var loaded = Rows.Where(r => r.Page is not null).OrderByDescending(r => r.SortKey).ToList();
        int? rank = null;
        double? previous = null;
        for (var i = 0; i < loaded.Count; i++)
        {
            if (previous != loaded[i].SortKey)
            {
                rank = i + 1;
                previous = loaded[i].SortKey;
            }

            loaded[i].Rank = rank;
        }
    }

    private static string FormatEta(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{Math.Ceiling(t.TotalMinutes)} 分" : $"{Math.Ceiling(t.TotalSeconds)} 秒";

    private static List<RankingRowViewModel> Selected(object? parameter) =>
        parameter is IList list ? list.OfType<RankingRowViewModel>().ToList() : [];

    public void Cancel()
    {
        _cts?.Cancel();
    }
}
