using BandcampBenrify.Core;
using BandcampBenrify.Infrastructure;

namespace BandcampBenrify.ViewModels;

/// <summary>キューの 1 行。サポーター数・値段・購入済みはキャッシュ済みページから計算する。</summary>
public sealed class QueueItemViewModel(TrackInfo track, int seq) : ObservableObject
{
    private static readonly string[] DetailProperties =
    [
        nameof(TotalText), nameof(TotalSort), nameof(AlbumSupText), nameof(AlbumSupSort),
        nameof(TrackSupText), nameof(TrackSupSort), nameof(TrackPriceText), nameof(TrackPriceSort),
        nameof(AlbumPriceText), nameof(AlbumPriceSort), nameof(ReleaseDate), nameof(ReleaseDateText),
        nameof(NeedsDetails), nameof(TotalIsPartial),
    ];

    private bool _isPlaying;
    private TrackDetails _details = new();
    private PurchaseState _purchase;

    public TrackInfo Track { get; } = track;

    /// <summary>キューに追加された順番 (並び替えを戻すときに使う)。</summary>
    public int Seq { get; } = seq;

    public string Title => Track.Title;

    public string Artist => Track.Artist;

    public string Album => string.IsNullOrEmpty(Track.AlbumTitle) ? "(シングル)" : Track.AlbumTitle;

    public string Duration => Format.Duration(Track.DurationSeconds);

    public double DurationSeconds => Track.DurationSeconds;

    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    public TrackDetails Details => _details;

    public void Update(TrackDetails details)
    {
        _details = details;
        foreach (var p in DetailProperties)
        {
            OnPropertyChanged(p);
        }
    }

    // ---- サポーター ----

    public string TotalText => _details.TotalDisplay;

    public double TotalSort => _details.TotalSortKey;

    /// <summary>曲ページ未取得で合計がアルバム分だけの状態。</summary>
    public bool TotalIsPartial => _details.HasAnyData && !_details.IsComplete;

    public string AlbumSupText => !_details.HasAlbum ? "—" : _details.AlbumSupporters?.Display ?? "…";

    public double AlbumSupSort => _details.AlbumSupporters?.SortKey ?? -1;

    public string TrackSupText => _details.TrackSupporters?.Display ?? "…";

    public double TrackSupSort => _details.TrackSupporters?.SortKey ?? -1;

    public bool NeedsDetails => !_details.IsComplete;

    // ---- 値段 ----

    public string TrackPriceText => _details.TrackPage is null ? "…" : _details.TrackPrice?.Display ?? "単品なし";

    public double TrackPriceSort => _details.TrackPrice is { } p ? (double)p.Amount : -1;

    public string AlbumPriceText => !_details.HasAlbum ? "—" : _details.AlbumPage is null ? "…" : _details.AlbumPrice?.Display ?? "—";

    public double AlbumPriceSort => _details.AlbumPrice is { } p ? (double)p.Amount : -1;

    public DateTimeOffset? ReleaseDate => _details.AlbumPage?.ReleaseDate ?? _details.TrackPage?.ReleaseDate;

    public string ReleaseDateText => ReleaseDate?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";

    // ---- 購入済み ----

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

    public static string Eta(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{Math.Ceiling(t.TotalMinutes)} 分" : $"{Math.Ceiling(t.TotalSeconds)} 秒";
}
