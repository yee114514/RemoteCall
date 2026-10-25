namespace RemoteCall.Core.Data;

/// <summary>
/// 一条消息（持久化到 store.json）。
/// </summary>
public sealed class MessageRecord
{
    public long Id { get; set; }
    public string Text { get; set; } = "";

    /// <summary>发送设备的稳定标识（浏览器 localStorage 中的 UUID）。</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>发送时的设备 IP。</summary>
    public string Ip { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>是否已在 ClassIsland 上完成展示。</summary>
    public bool Completed { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// 一个设备。（设备名单独存放，改名后历史消息通过 deviceId 联查，名字自动同步。）
/// </summary>
public sealed class DeviceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string LastIp { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// store.json 的结构。
/// </summary>
public sealed class StoreSnapshot
{
    public int Version { get; set; } = 2;
    public long NextMessageId { get; set; } = 1;
    public List<DeviceRecord> Devices { get; set; } = new();
    public List<MessageRecord> Messages { get; set; } = new();
}

/// <summary>
/// 对外的消息视图（已把设备名解析好）。
/// </summary>
public sealed record MessageView(
    long Id,
    string Text,
    string DeviceId,
    string DeviceName,
    string Ip,
    DateTimeOffset CreatedAt,
    bool Completed,
    DateTimeOffset? CompletedAt);

/// <summary>
/// 对外的设备视图。
/// </summary>
public sealed record DeviceView(
    string Id,
    string Name,
    string LastIp,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>消息筛选。</summary>
public enum MessageFilter
{
    All = 0,
    Pending = 1,
    Completed = 2,
}
