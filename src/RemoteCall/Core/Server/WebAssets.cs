using System.Reflection;
using System.Security.Cryptography;

namespace RemoteCall.Core.Server;

/// <summary>
/// 网页静态资源（编译进程序集的 EmbeddedResource）。
/// </summary>
public static class WebAssets
{
    private static readonly Lazy<Dictionary<string, string>> ResourceNames = new(BuildIndex);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, (byte[] Bytes, string ETag, string ContentType)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 读取资源。<paramref name="logicalPath"/> 形如 <c>lan/index.html</c>。
    /// </summary>
    public static bool TryGet(string logicalPath, out byte[] bytes, out string contentType, out string etag)
    {
        var normalized = logicalPath.TrimStart('/').Replace('\\', '/');
        var resourceKey = normalized.Replace('/', '.');
        lock (Sync)
        {
            if (Cache.TryGetValue(normalized, out var cached))
            {
                bytes = cached.Bytes;
                contentType = cached.ContentType;
                etag = cached.ETag;
                return true;
            }
        }

        if (!ResourceNames.Value.TryGetValue(resourceKey, out var resourceName))
        {
            bytes = Array.Empty<byte>();
            contentType = "application/octet-stream";
            etag = "";
            return false;
        }

        try
        {
            using var stream = typeof(WebAssets).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                bytes = Array.Empty<byte>();
                contentType = "application/octet-stream";
                etag = "";
                return false;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
            contentType = Http.MimeTypes.ForPath(normalized);
            etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16] + "\"";
            lock (Sync)
            {
                Cache[normalized] = (bytes, etag, contentType);
            }

            return true;
        }
        catch
        {
            bytes = Array.Empty<byte>();
            contentType = "application/octet-stream";
            etag = "";
            return false;
        }
    }

    private static Dictionary<string, string> BuildIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(WebAssets).Assembly;
        const string marker = ".Core.Web.";
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var relative = name[(index + marker.Length)..];
            map[relative] = name;
        }

        return map;
    }
}
