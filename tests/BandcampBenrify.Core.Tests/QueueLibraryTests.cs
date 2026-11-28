using System.Text.Json;
using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

/// <summary>名前付きの複数キューの保存と、キューが 1 つだった頃の queue.json の取り込み。</summary>
public sealed class QueueLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bcb-queues-" + Guid.NewGuid().ToString("N"));

    public QueueLibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string LibraryFile => Path.Combine(_dir, "queues.json");

    private string LegacyFile => Path.Combine(_dir, "queue.json");

    private static readonly DateTime Now = new(2026, 11, 28, 12, 0, 0);

    private static TrackInfo Track(long id, string title) => new()
    {
        TrackId = id, Title = title, Artist = "A", AlbumTitle = "Al", TrackUrl = $"https://x.bandcamp.com/track/{id}",
        SourcePageUrl = "https://x.bandcamp.com/album/al", DurationSeconds = 200 + id, ArtId = 99, Label = "L", AlbumId = 7,
        AlbumUrl = "https://x.bandcamp.com/album/al",
    };

    private QueueLibraryLoadResult Load() => QueueLibraryStore.Load(LibraryFile, LegacyFile, Now);

    [Fact]
    public void TheOldSingleQueueBecomesTheFirstNamedQueueAndIsLeftUntouched()
    {
        // 以前の版が書いていたのと同じ形 (曲の配列) の queue.json
        File.WriteAllText(LegacyFile, JsonSerializer.Serialize(new List<TrackInfo> { Track(1, "one"), Track(2, "two") }));
        var legacyBytes = File.ReadAllBytes(LegacyFile);

        var result = Load();
        Assert.True(result.Migrated);
        Assert.Null(result.Error);
        var queue = Assert.Single(result.Library.Queues);
        Assert.Equal("キュー", queue.Name);
        Assert.Equal([Track(1, "one"), Track(2, "two")], queue.Tracks);
        Assert.Same(queue, result.Library.Selected);

        QueueLibraryStore.Save(LibraryFile, result.Library);
        Assert.Equal(legacyBytes, File.ReadAllBytes(LegacyFile)); // 古いファイルは書き換えない

        // 2 回目からは queues.json を読む (queue.json は見ない)
        File.WriteAllText(LegacyFile, JsonSerializer.Serialize(new List<TrackInfo> { Track(3, "three") }));
        var again = Load();
        Assert.False(again.Migrated);
        Assert.Equal([Track(1, "one"), Track(2, "two")], Assert.Single(again.Library.Queues).Tracks);
    }

    [Fact]
    public void QueuesKeepTheirNamesTracksAndSelection()
    {
        var library = QueueLibrary.CreateDefault([Track(1, "one")]);
        var second = new SavedQueue { Name = "朝に聴く", Tracks = [Track(2, "two"), Track(3, "three")] };
        library.Queues.Add(second);
        library.Queues.Add(new SavedQueue { Name = "空" });
        library.SelectedId = second.Id;
        QueueLibraryStore.Save(LibraryFile, library);

        var loaded = Load().Library;
        Assert.Equal(["キュー", "朝に聴く", "空"], loaded.Queues.Select(q => q.Name));
        Assert.Equal(library.Queues.Select(q => q.Id), loaded.Queues.Select(q => q.Id));
        Assert.Equal("朝に聴く", loaded.Selected.Name);
        Assert.Equal([Track(2, "two"), Track(3, "three")], loaded.Selected.Tracks);
        Assert.Empty(loaded.Queues[2].Tracks);
        Assert.False(File.Exists(LegacyFile)); // 新しく使い始めた人には queue.json を作らない

        // 曲は 1 回ずつだけ書く (表示中のキューを別に書いてファイルが膨らまないように)
        var json = File.ReadAllText(LibraryFile);
        Assert.Equal(3, json.Split("\"TrackId\"").Length - 1);
    }

    [Fact]
    public void WithoutAnyFileThereIsOneEmptyQueue()
    {
        var result = Load();
        Assert.False(result.Migrated);
        Assert.Null(result.Error);
        var queue = Assert.Single(result.Library.Queues);
        Assert.Equal("キュー", queue.Name);
        Assert.Empty(queue.Tracks);
    }

    [Fact]
    public void ABrokenQueuesFileIsKeptAside()
    {
        File.WriteAllText(LibraryFile, "{ \"Queues\": [ {");
        File.WriteAllText(LegacyFile, JsonSerializer.Serialize(new List<TrackInfo> { Track(1, "one") }));

        var result = Load();
        Assert.False(result.SaveBlocked);
        Assert.Contains("queues.json.broken-20261128120000", result.Error);
        Assert.Equal("{ \"Queues\": [ {", File.ReadAllText(LibraryFile + ".broken-20261128120000"));
        Assert.Empty(Assert.Single(result.Library.Queues).Tracks); // 古い queue.json の中身で勝手に置き換えない
        Assert.True(File.Exists(LegacyFile));
    }

    [Fact]
    public void ABrokenOldQueueFileIsKeptAside()
    {
        File.WriteAllText(LegacyFile, "[ { \"TrackId\": ");

        var result = Load();
        Assert.False(result.Migrated);
        Assert.Contains("queue.json.broken-20261128120000", result.Error);
        Assert.True(File.Exists(LegacyFile + ".broken-20261128120000"));
        Assert.Empty(Assert.Single(result.Library.Queues).Tracks);
    }

    [Fact]
    public void HandEditedFilesAreRepaired()
    {
        File.WriteAllText(LibraryFile, """
            { "SelectedId": "nope", "Queues": [
                { "Id": "a", "Name": "  ", "Tracks": null },
                { "Id": "a", "Name": " 夜 ", "Tracks": [ { "TrackId": 5, "Title": "five" }, null ] },
                null
            ] }
            """);

        var library = Load().Library;
        Assert.Equal(2, library.Queues.Count);
        Assert.Equal(["キュー", "夜"], library.Queues.Select(q => q.Name));
        Assert.Empty(library.Queues[0].Tracks);
        Assert.Equal(5, Assert.Single(library.Queues[1].Tracks).TrackId);
        Assert.Equal(2, library.Queues.Select(q => q.Id).Distinct().Count());
        Assert.Same(library.Queues[0], library.Selected);
    }

    [Fact]
    public void AnEmptyListOfQueuesStillGivesOneQueue()
    {
        File.WriteAllText(LibraryFile, """{ "Queues": [] }""");
        Assert.Equal("キュー", Assert.Single(Load().Library.Queues).Name);
    }

    [Fact]
    public void NewQueuesGetUnusedDefaultNames()
    {
        var library = QueueLibrary.CreateDefault();
        Assert.Equal("キュー 2", library.NextDefaultName());
        library.Queues.Add(new SavedQueue { Name = "キュー 2" });
        library.Queues.Add(new SavedQueue { Name = "キュー 4" });
        Assert.Equal("キュー 5", library.NextDefaultName());
        library.Queues.RemoveAt(1);
        Assert.Equal("キュー 3", library.NextDefaultName());
    }
}
