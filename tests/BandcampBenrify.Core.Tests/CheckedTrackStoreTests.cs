using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

public sealed class CheckedTrackStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "benrify-checked-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "checked.json");

    private static TrackInfo Track(long id) => new()
    {
        TrackId = id,
        Title = $"Song {id}",
        Artist = "Artist",
        AlbumTitle = "Album",
        TrackUrl = $"https://x.bandcamp.com/track/song-{id}",
    };

    [Fact]
    public void CheckAndUncheck_PersistAcrossInstances()
    {
        var store = new CheckedTrackStore(FilePath);
        Assert.Equal(2, store.Set([Track(1), Track(2)], true, DateTimeOffset.Now));
        Assert.Equal(0, store.Set([Track(1)], true, DateTimeOffset.Now)); // 既にチェック済みなら変化なし

        var reloaded = new CheckedTrackStore(FilePath);
        Assert.True(reloaded.Contains(1));
        Assert.True(reloaded.Contains(2));
        Assert.Equal("Song 1", reloaded.Items.Single(i => i.TrackId == 1).Title);

        Assert.Equal(1, reloaded.Set([Track(1)], false, DateTimeOffset.Now));
        Assert.False(new CheckedTrackStore(FilePath).Contains(1));
        Assert.True(new CheckedTrackStore(FilePath).Contains(2));
    }

    [Fact]
    public void NoFile_StartsEmpty_AndDoesNotCreateFileUntilChanged()
    {
        var store = new CheckedTrackStore(FilePath);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.Set([Track(1)], false, DateTimeOffset.Now));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void BrokenFile_IsBackedUpInsteadOfOverwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        var store = new CheckedTrackStore(FilePath);

        Assert.Equal(0, store.Count);
        Assert.NotNull(store.LoadError);
        var backup = Assert.Single(Directory.GetFiles(_dir, "checked.json.broken-*"));
        Assert.Equal("{ not json", File.ReadAllText(backup));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
