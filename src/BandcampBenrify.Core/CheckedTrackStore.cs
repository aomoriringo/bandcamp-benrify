using System.Text.Json;

namespace BandcampBenrify.Core;

/// <summary>チェック済みの曲の 1 件 (印の意味は使う人次第)。あとで一覧にできるよう曲名なども残す。</summary>
public sealed record CheckedTrack(long TrackId, string Title, string Artist, string? AlbumTitle, string TrackUrl, DateTimeOffset CheckedAt);

/// <summary>
/// チェック済みの曲をローカルの JSON ファイルに保存する (Bandcamp には何も送らない)。
/// 曲 ID (Bandcamp 全体で一意) をキーにするので、キューから消しても、別の画面で同じ曲を開いても印が残る。
/// </summary>
public sealed class CheckedTrackStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<long, CheckedTrack> _items = [];

    public CheckedTrackStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>読み込みに失敗したとき (壊れたファイルは別名で退避して空から始める)。</summary>
    public string? LoadError { get; private set; }

    public int Count => _items.Count;

    public IReadOnlyCollection<CheckedTrack> Items => _items.Values;

    public bool Contains(long trackId) => _items.ContainsKey(trackId);

    /// <summary>チェックを付ける / 外す。状態が変わった曲の数を返し、変わったときだけ保存する。</summary>
    public int Set(IEnumerable<TrackInfo> tracks, bool isChecked, DateTimeOffset now)
    {
        var changed = 0;
        foreach (var t in tracks)
        {
            if (isChecked)
            {
                if (_items.TryAdd(t.TrackId, new CheckedTrack(t.TrackId, t.Title, t.Artist, t.AlbumTitle, t.TrackUrl, now)))
                {
                    changed++;
                }
            }
            else if (_items.Remove(t.TrackId))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            Save();
        }

        return changed;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<CheckedTrack>>(File.ReadAllText(_path)) ?? [];
            foreach (var item in list)
            {
                _items[item.TrackId] = item;
            }
        }
        catch (JsonException ex)
        {
            // 上書きして失わないように、壊れたファイルは残しておく
            var backup = _path + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_path, backup);
            LoadError = $"チェック済みの記録を読み込めなかったので {Path.GetFileName(backup)} に退避しました ({ex.Message})";
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        var list = _items.Values.OrderBy(i => i.CheckedAt).ToList();
        File.WriteAllText(temp, JsonSerializer.Serialize(list, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
