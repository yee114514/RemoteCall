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

        // 安卓浏览器切到「桌面版网站」后 UA 会变成 X11; Linux，里面没有 Android 字样；
        // 这类 UA 通常仍带 Mobile 或 wv)（WebView），据此判定为安卓而不是 Linux 桌面。
        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) &&
            (ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
             ua.Contains("wv)", StringComparison.OrdinalIgnoreCase) ||
             ua.Contains("; U;", StringComparison.OrdinalIgnoreCase)))
        {
            return AndroidDevice;
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

        // "zh-cn; MI 8" 这种，取最后一个像机型的片段
        var segments = candidate.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chosen = segments.LastOrDefault(LooksLikeModel) ?? candidate;

        return LooksLikeModel(chosen) ? chosen : null;
    }

    private static bool LooksLikeModel(string text)
    {
        if (text.Length < 2 || text.Length > 40)
        {
            return false;
        }

        if (text.StartsWith("Android", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("wv", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("K", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("U", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 语言区域（zh-cn / en-US）、纯数字都不算机型
        if (text.All(char.IsDigit))
        {
            return false;
        }

        if (text.Length <= 6 && text.Contains('-') &&
            char.IsLetter(text[0]) && char.IsLetter(text[1]))
        {
            return false;
        }

        return true;
    }
}
