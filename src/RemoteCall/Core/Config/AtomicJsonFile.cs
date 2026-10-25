using System.Text.Json;

namespace RemoteCall.Core.Config;

/// <summary>
/// 以「写临时文件 + 原子替换」的方式读写 JSON 文件，避免进程崩溃时留下半个文件。
/// </summary>
public static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 读取 JSON 文件。文件不存在返回 null；内容损坏时记录日志并返回 null（不抛出）。
    /// </summary>
    public static T? Load<T>(string path, Action<string>? log = null) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(json, ReadOptions);
        }
        catch (Exception ex)
        {
            log?.Invoke($"读取 {Path.GetFileName(path)} 失败，已忽略该文件：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 写入 JSON 文件（原子替换）。
    /// </summary>
    public static void Save<T>(string path, T value, Action<string>? log = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(value, WriteOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            log?.Invoke($"写入 {Path.GetFileName(path)} 失败：{ex.Message}");
        }
    }

    public static T DeepClone<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, WriteOptions);
        return JsonSerializer.Deserialize<T>(json, ReadOptions)!;
    }
}
