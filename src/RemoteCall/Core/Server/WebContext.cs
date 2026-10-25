using RemoteCall.Core.Auth;
using RemoteCall.Core.Config;
using RemoteCall.Core.Data;

namespace RemoteCall.Core.Server;

/// <summary>
/// 两个网页服务共享的上下文。
/// </summary>
public sealed class WebContext
{
    private readonly Action<string>? _externalLog;

    public WebContext(
        PluginSettingsService settings,
        MessageStore store,
        SessionStore sessions,
        ServerStatus status,
        LogBuffer logs,
        Action<string>? externalLog = null)
    {
        Settings = settings;
        Store = store;
        Sessions = sessions;
        Status = status;
        Logs = logs;
        _externalLog = externalLog;
    }

    public PluginSettingsService Settings { get; }
    public MessageStore Store { get; }
    public SessionStore Sessions { get; }
    public ServerStatus Status { get; }
    public LogBuffer Logs { get; }

    public string PluginVersion { get; set; } = "1.0.0.0";

    /// <summary>重启两个监听（由 ServerHost 注入）。</summary>
    public Action? RestartServers { get; set; }

    public RateLimiter LoginLimiter { get; } =
        new(CoreDefaults.LoginFailuresPerMinute, TimeSpan.FromMinutes(1));

    public RateLimiter MessageLimiter { get; } =
        new(CoreDefaults.MessagesPerMinutePerIp, TimeSpan.FromMinutes(1));

    public void Log(string message)
    {
        Logs.Add(message);
        _externalLog?.Invoke(message);
    }
}
