using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BandcampBenrify.Core;

/// <summary>
/// 解析済みデータを JSON でディスクに保存する簡易キャッシュ。同じページを何度も取りに行かないためのもの。
/// </summary>
public sealed class DiskCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _root;

    public DiskCache(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public string RootDirectory => _root;

    public CacheEntry<T>? TryGet<T>(string ns, string key)
    {
        var path = PathFor(ns, key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<CacheEntry<T>>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public T? TryGetFresh<T>(string ns, string key, TimeSpan maxAge, DateTimeOffset now)
        where T : class
    {
        var entry = TryGet<T>(ns, key);
        return entry is not null && now - entry.SavedAt <= maxAge ? entry.Value : null;
    }

    public void Set<T>(string ns, string key, T value, DateTimeOffset now)
    {
        var path = PathFor(ns, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheEntry<T>(key, now, value), JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException)
        {
            // 同時書き込みなどで失敗してもキャッシュなので無視
            TryDelete(tmp);
        }
    }

    /// <summary>指定した種類 (名前空間) のキャッシュの合計サイズ。</summary>
    public long SizeBytes(string ns)
    {
        var dir = Path.Combine(_root, ns);
        return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    /// <summary>指定した種類 (名前空間) のキャッシュを消す。ほかの種類は残す。</summary>
    public void Clear(string ns)
    {
        var dir = Path.Combine(_root, ns);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private string PathFor(string ns, string key)
    {
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_root, ns, hash[..2], hash + ".json");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

public sealed record CacheEntry<T>(string Key, DateTimeOffset SavedAt, T Value);
