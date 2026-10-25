namespace RemoteCall.Core.Config;

/// <summary>
/// 插件级设置（持久化到插件配置目录的 settings.json）。
/// </summary>
public sealed class PluginSettings
{
    // ---- 服务 ----

    /// <summary>是否开启局域网网页服务。</summary>
    public bool LanEnabled { get; set; } = true;

    /// <summary>局域网网页端口。</summary>
    public int LanPort { get; set; } = CoreDefaults.LanPort;

    /// <summary>本机管理页端口（仅绑定 127.0.0.1）。</summary>
    public int AdminPort { get; set; } = CoreDefaults.AdminPort;

    // ---- 登录 ----

    /// <summary>
    /// 「记住我」时长的天数。<c>-1</c> 表示不限制（会话不会因为时间过期）。
    /// </summary>
    public int SessionDays { get; set; } = CoreDefaults.SessionDays;

    /// <summary>密码盐（Base64）。为空表示尚未设置密码。</summary>
    public string PasswordSalt { get; set; } = "";

    /// <summary>PBKDF2 派生密钥（Base64）。为空表示尚未设置密码。</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>PBKDF2 迭代次数。</summary>
    public int PasswordIterations { get; set; } = CoreDefaults.PasswordIterations;

    /// <summary>
    /// 首次启动自动生成的密码明文。用户自行设置密码后会清空，仅用于让用户能在设置页看到初始密码。
    /// </summary>
    public string GeneratedPassword { get; set; } = "";

    // ---- 提醒 ----

    /// <summary>收到新消息时是否弹出 ClassIsland 提醒。</summary>
    public bool NotificationEnabled { get; set; } = true;

    /// <summary>提醒遮罩显示秒数。</summary>
    public double NotificationMaskSeconds { get; set; } = CoreDefaults.NotificationMaskSeconds;

    /// <summary>提醒正文显示秒数。</summary>
    public double NotificationOverlaySeconds { get; set; } = CoreDefaults.NotificationOverlaySeconds;

    /// <summary>提醒时是否朗读消息内容。</summary>
    public bool NotificationSpeech { get; set; } = false;

    // ---- 数据 ----

    /// <summary>
    /// 最多保留多少条历史消息。<c>-1</c> 表示不限制（全部保留）。
    /// </summary>
    public int MessageRetention { get; set; } = CoreDefaults.MessageRetention;
}
