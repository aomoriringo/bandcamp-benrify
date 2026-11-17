using BandcampBenrify.Core;
using BandcampBenrify.Infrastructure;

namespace BandcampBenrify.ViewModels;

public sealed class QueueItemViewModel(TrackInfo track) : ObservableObject
{
    private bool _isPlaying;

    public TrackInfo Track { get; } = track;

    public string Title => Track.Title;

    public string Artist => Track.Artist;

    public string Album => string.IsNullOrEmpty(Track.AlbumTitle) ? "(シングル)" : Track.AlbumTitle;

    public string Duration => Format.Duration(Track.DurationSeconds);

    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }
}

public enum RowState
{
    Pending,
    Loading,
    Loaded,
    Counting,
    Error,
}

public sealed class RankingRowViewModel(DiscographyItem item) : ObservableObject
{
    private TralbumPage? _page;
    private RowState _state = RowState.Pending;
    private string? _error;
    private int? _rank;

    public DiscographyItem Item { get; } = item;

    public string Title => Item.Title;

    public string Artist => Item.Artist;

    public string TypeLabel => Item.Type == ItemType.Album ? "アルバム" : "曲";

    public string? ArtUrl => string.IsNullOrEmpty(Item.ArtUrl) ? null : Item.ArtUrl;

    public TralbumPage? Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                OnPropertyChanged(nameof(Supporters));
                OnPropertyChanged(nameof(SortKey));
                OnPropertyChanged(nameof(ReleaseDate));
                OnPropertyChanged(nameof(ReleaseDateText));
                OnPropertyChanged(nameof(TrackCount));
                OnPropertyChanged(nameof(HasMoreSupporters));
                OnPropertyChanged(nameof(HasAudio));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    /// <summary>サポーター数での順位 (同数は同順位)。未取得は null。</summary>
    public int? Rank
    {
        get => _rank;
        set => Set(ref _rank, value);
    }

    /// <summary>試聴できる曲が 1 曲以上あるか。</summary>
    public bool HasAudio => Page?.Tracks.Any(t => t.StreamUrl is not null) ?? false;

    public RowState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
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

    public string Supporters => Page?.Supporters.Display ?? "…";

    /// <summary>未取得は -1。「N+」は N.5 として扱う。</summary>
    public double SortKey => Page?.Supporters.SortKey ?? -1;

    public bool HasMoreSupporters => Page?.Supporters.HasMore ?? false;

    public DateTimeOffset? ReleaseDate => Page?.ReleaseDate;

    public string ReleaseDateText => Page?.ReleaseDate?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";

    public int? TrackCount => Page?.Tracks.Count;

    public string StateText => State switch
    {
        RowState.Pending => "待機",
        RowState.Loading => "取得中",
        RowState.Counting => "集計中",
        RowState.Error => "エラー: " + Error,
        _ when Page is not null && !HasAudio => "試聴不可",
        _ => "",
    };
}

public static class Format
{
    public static string Duration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds))
        {
            return "";
        }

        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
