using System.Text.Json;
using System.Text.Json.Serialization;

namespace BandcampBenrify.Core;

/// <summary>名前を付けたキュー 1 つ。</summary>
public sealed class SavedQueue
{
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "";

    /// <summary>追加した順の曲。</summary>
    public List<TrackInfo> Tracks { get; set; } = [];

    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// すべてのキュー (queues.json の中身)。
/// 1 つしかキューが無かった頃の queue.json (曲の配列) は、初めて読むときに「キュー」という名前の 1 つ目のキューとして取り込む。
/// queue.json は書き換えも削除もしないので、取り込んだ時点の内容がそのまま残る。
/// </summary>
public sealed class QueueLibrary
{
    public const int CurrentVersion = 1;

    /// <summary>1 つしか無かった頃のキューを取り込んだときと、最初のキューの名前。</summary>
    public const string DefaultName = "キュー";

    public int Version { get; set; } = CurrentVersion;

    /// <summary>表示している (行き先を選ばない追加で曲を入れる) キュー。</summary>
    public string? SelectedId { get; set; }

    public List<SavedQueue> Queues { get; set; } = [];

    [JsonIgnore]
    public SavedQueue Selected => Queues.FirstOrDefault(q => q.Id == SelectedId) ?? Queues[0];

    public static QueueLibrary CreateDefault(List<TrackInfo>? tracks = null)
    {
        var queue = new SavedQueue { Name = DefaultName, Tracks = tracks ?? [] };
        return new QueueLibrary { Queues = [queue], SelectedId = queue.Id };
    }

    /// <summary>まだ使われていない「キュー 2」「キュー 3」… の名前。</summary>
    public string NextDefaultName()
    {
        for (var n = Queues.Count + 1; ; n++)
        {
            var name = $"{DefaultName} {n}";
            if (Queues.All(q => q.Name != name))
            {
                return name;
            }
        }
    }

    /// <summary>手で直されたり古い版で書かれたりしたファイルでも使えるよう、欠けた値を埋める。</summary>
    internal void Normalize()
    {
        Queues = Queues?.Where(q => q is not null).ToList() ?? [];
        if (Queues.Count == 0)
        {
            Queues.Add(new SavedQueue { Name = DefaultName });
        }

        var ids = new HashSet<string>();
        foreach (var q in Queues)
        {
            q.Tracks = q.Tracks?.Where(t => t is not null).ToList() ?? [];
            q.Name = string.IsNullOrWhiteSpace(q.Name) ? DefaultName : q.Name.Trim();
            if (string.IsNullOrEmpty(q.Id) || !ids.Add(q.Id))
            {
                q.Id = SavedQueue.NewId();
                ids.Add(q.Id);
            }
        }

        if (Queues.All(q => q.Id != SelectedId))
        {
            SelectedId = Queues[0].Id;
        }
    }
}

/// <summary>キューを読めたか。読めなかったときも、元のファイルは消さずに残す。</summary>
/// <param name="Error">起動時に知らせるメッセージ。</param>
/// <param name="SaveBlocked">元のファイルを退避できなかった。空のキューで上書きしないよう、保存しないこと。</param>
/// <param name="Migrated">queue.json (1 つしか無かった頃のキュー) を取り込んだ。すぐ queues.json に保存するとよい。</param>
public sealed record QueueLibraryLoadResult(QueueLibrary Library, string? Error, bool SaveBlocked, bool Migrated);

public static class QueueLibraryStore
{
    public static QueueLibraryLoadResult Load(string libraryFile, string legacyFile, DateTime now)
    {
        if (File.Exists(libraryFile))
        {
            try
            {
                var library = JsonSerializer.Deserialize<QueueLibrary>(File.ReadAllText(libraryFile)) ?? QueueLibrary.CreateDefault();
                library.Normalize();
                return new QueueLibraryLoadResult(library, null, false, false);
            }
            catch (JsonException ex)
            {
                return MoveAside(libraryFile, now, ex.Message);
            }
            catch (IOException ex)
            {
                return Blocked(ex.Message);
            }
        }

        if (File.Exists(legacyFile))
        {
            try
            {
                var tracks = JsonSerializer.Deserialize<List<TrackInfo>>(File.ReadAllText(legacyFile)) ?? [];
                var library = QueueLibrary.CreateDefault(tracks.Where(t => t is not null).ToList());
                return new QueueLibraryLoadResult(library, null, false, true);
            }
            catch (JsonException ex)
            {
                return MoveAside(legacyFile, now, ex.Message);
            }
            catch (IOException ex)
            {
                return Blocked(ex.Message);
            }
        }

        return new QueueLibraryLoadResult(QueueLibrary.CreateDefault(), null, false, false);
    }

    public static void Save(string libraryFile, QueueLibrary library)
    {
        library.Version = QueueLibrary.CurrentVersion;
        SafeFile.WriteAllTextAtomic(libraryFile, JsonSerializer.Serialize(library));
    }

    private static QueueLibraryLoadResult MoveAside(string path, DateTime now, string reason)
    {
        // 壊れたファイルは別名で残しておく (このあとの保存で上書きして失わないように)
        try
        {
            var backup = SafeFile.MoveAside(path, now);
            return new QueueLibraryLoadResult(QueueLibrary.CreateDefault(), $"キューを読み込めなかったので {backup} に退避しました ({reason})", false, false);
        }
        catch (IOException)
        {
            return Blocked(reason);
        }
    }

    private static QueueLibraryLoadResult Blocked(string reason) =>
        new(QueueLibrary.CreateDefault(), $"キューを読み込めませんでした ({reason})。元のファイルを上書きしないよう、キューの変更は保存しません。アプリを再起動してください", true, false);
}
