namespace BandcampBenrify.Core;

/// <summary>保存したデータを壊さない / 失わないためのファイル操作。</summary>
public static class SafeFile
{
    /// <summary>
    /// 一時ファイルに書いてから置き換える。書き込み中に落ちても、元のファイルは前の内容のまま残る。
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// 読み込めなかったファイルを「元の名前.broken-日時」に移して残す (このあとの保存で上書きしないように)。
    /// 移した先のファイル名を返す。
    /// </summary>
    public static string MoveAside(string path, DateTime now)
    {
        var backup = path + $".broken-{now:yyyyMMddHHmmss}";
        File.Move(path, backup);
        return Path.GetFileName(backup);
    }
}
