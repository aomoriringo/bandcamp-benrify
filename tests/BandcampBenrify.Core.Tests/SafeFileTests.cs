using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

public sealed class SafeFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "benrify-safefile-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "queue.json");

    [Fact]
    public void WriteAllTextAtomic_CreatesDirectory_ReplacesContent_LeavesNoTemp()
    {
        SafeFile.WriteAllTextAtomic(FilePath, "[1]");
        SafeFile.WriteAllTextAtomic(FilePath, "[1,2]");

        Assert.Equal("[1,2]", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void WriteAllTextAtomic_LeftoverTempFromCrash_IsOverwritten()
    {
        SafeFile.WriteAllTextAtomic(FilePath, "[1]");
        File.WriteAllText(FilePath + ".tmp", "[壊れ"); // 前回の書き込み途中で落ちた残り

        SafeFile.WriteAllTextAtomic(FilePath, "[1,2]");

        Assert.Equal("[1,2]", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void MoveAside_KeepsBrokenContentUnderNewName()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{not json");

        var backup = SafeFile.MoveAside(FilePath, new DateTime(2026, 11, 18, 9, 30, 0));

        Assert.Equal("queue.json.broken-20261118093000", backup);
        Assert.False(File.Exists(FilePath));
        Assert.Equal("{not json", File.ReadAllText(Path.Combine(_dir, backup)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
