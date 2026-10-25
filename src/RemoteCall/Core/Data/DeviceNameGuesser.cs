namespace RemoteCall.Core.Data;

/// <summary>
/// 从 User-Agent 猜一个"像设备名"的默认名字。
/// <para>
/// 浏览器出于隐私不提供主机名/设备名，所以这里只能按 UA 识别设备类型与机型：
/// iPhone / iPad / Android 机型（如 SM-G991B、Pixel 7）/ Windows 电脑 / Mac / Linux 等。
/// 用户在网页上改名后以用户设置为准。
/// </para>
/// </summary>
public static class DeviceNameGuesser
{
    public const string Unknown = "未知设备";
    public const string AndroidDevice = "Android 设备";
    public const string WindowsPc = "Windows 电脑";
    public const string MacDevice = "Mac";
    public const string LinuxDevice = "Linux 设备";

    public static string FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return Unknown;
        }

        var ua = userAgent;

        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            return "iPhone";
        }

        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase))
        {
            return "iPad";
        }

        if (ua.Contains("iPod", StringComparison.OrdinalIgnoreCase))
        {
            return "iPod touch";
        }

        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            var model = ExtractAndroidModel(ua);
            return model ?? AndroidDevice;
        }

        if (ua.Contains("CrOS", StringComparison.OrdinalIgnoreCase))
        {
            return "Chromebook";
        }

        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return WindowsPc;
        }

        if (ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase))
        {
            return MacDevice;
        }

        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return LinuxDevice;
        }

        return Unknown;
    }

    /// <summary>
    /// 从 <c>...; Android 13; SM-G991B Build/...</c> 这类 UA 里取机型。
    /// </summary>
    private static string? ExtractAndroidModel(string ua)
    {
        var index = ua.IndexOf("Android", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        // 跳到 "Android" 之后的第一个分号，再取到 "Build/" 或右括号
        var afterAndroid = ua[(index + "Android".Length)..];
        var semicolon = afterAndroid.IndexOf(';');
        if (semicolon < 0)
        {
            return null;
        }

        var candidate = afterAndroid[(semicolon + 1)..];

        var build = candidate.IndexOf("Build/", StringComparison.OrdinalIgnoreCase);
        if (build >= 0)
        {
            candidate = candidate[..build];
        }
        else
        {
            var close = candidate.IndexOf(')');
            if (close >= 0)
            {
                candidate = candidate[..close];
            }
        }

        candidate = candidate.Trim().TrimEnd(';').Trim();

        // 过滤掉 "Android 13"、"wv"、"K" 这类不是机型的内容
        if (candidate.Length < 2 ||
            candidate.Length > 40 ||
            candidate.StartsWith("Android", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("wv", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("K", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return candidate;
    }
}
