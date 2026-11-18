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
        nameof(NeedsDetails), nameof(TotalIsPartial), nameof(Label),
    ];

    private bool _isPlaying;
    private bool _isQueued;
    private TrackDetails _details = new();
    private PurchaseState _purchase;

    public TrackInfo Track { get; } = track;

    private int _seq = seq;

    /// <summary>キューに追加された順番 (1 始まり。削除したら詰め直す)。「探す」の曲目ではトラック番号。</summary>
    public int Seq
    {
        get => _seq;
        set => Set(ref _seq, value);
    }

    public string Title => Track.Title;

    public string Artist => Track.Artist;

    public string Album => string.IsNullOrEmpty(Track.AlbumTitle) ? "(シングル)" : Track.AlbumTitle;

    /// <summary>レーベル名 (自主リリースは空、不明なら「…」)。</summary>
    public string Label => _details.Label ?? "…";

    public string Duration => Format.Duration(Track.DurationSeconds);

    public double DurationSeconds => Track.DurationSeconds;

    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    /// <summary>キューに入っているか (「探す」の曲目で使う)。</summary>
    public bool IsQueued
    {
        get => _isQueued;
        set
        {
            if (Set(ref _isQueued, value))
            {
                OnPropertyChanged(nameof(QueuedText));
            }
        }
    }

    public string QueuedText => IsQueued ? "✓" : "";

    /// <summary>試聴できる曲か (「探す」の曲目用。ページから読んだ直後の TrackInfo にだけストリーム URL が入っている)。</summary>
    public bool IsStreamable => Track.StreamUrl is not null;

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

    /// <summary>取得していないページがあるか、サポーター数が「N+」のままか。</summary>
    public bool NeedsDetails => !_details.IsComplete || _details.TotalHasMore;

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

    // ---- チェック済み (聴いた上で買わない) ----

    private bool _isChecked;

    /// <summary>チェックボックスから変えられたときに呼ばれる (記録に保存して、同じ曲の他の行にも反映する)。</summary>
    public Action<QueueItemViewModel, bool>? CheckRequested { get; set; }

    /// <summary>チェック済みか。画面から変えると <see cref="CheckRequested"/> 経由で記録される。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value == _isChecked)
            {
                return;
            }

            if (CheckRequested is { } request)
            {
                request(this, value);
            }
            else
            {
                SetCheckedState(value);
            }
        }
    }

    /// <summary>記録の状態を表示に反映する (保存はしない)。</summary>
    public void SetCheckedState(bool value)
    {
        if (Set(ref _isChecked, value, nameof(IsChecked)))
        {
            OnPropertyChanged(nameof(CheckedSort));
        }
    }

    public int CheckedSort => _isChecked ? 1 : 0;
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
