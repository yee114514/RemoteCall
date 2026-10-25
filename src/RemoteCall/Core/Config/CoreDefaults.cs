namespace RemoteCall.Core.Config;

/// <summary>
/// Core 层（不依赖 ClassIsland / Avalonia）使用的默认值。
/// </summary>
public static class CoreDefaults
{
    public const int LanPort = 8000;
    public const int AdminPort = 8199;
    public const string CookieName = "remotecall_session";
    public const int SessionDays = 30;
    public const int SessionHoursNotRemembered = 12;
    public const int PasswordIterations = 120_000;

    /// <summary>首次启动时使用的默认访问密码（建议用户改成自己的）。</summary>
    public const string DefaultPassword = "admin";

    /// <summary>「不限制」的取值（用于记住登录天数、历史保留条数）。</summary>
    public const int Unlimited = -1;

    public const int MessageRetention = 1000;
    public const double NotificationMaskSeconds = 4;
    public const double NotificationOverlaySeconds = 12;
    public const int MaxMessageLength = 2000;
    public const int MessagesPerMinutePerIp = 30;
    public const int LoginFailuresPerMinute = 5;
    public const int RequestBodyLimit = 64 * 1024;
}
