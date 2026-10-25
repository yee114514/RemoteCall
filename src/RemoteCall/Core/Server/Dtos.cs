namespace RemoteCall.Core.Server;

// ---- 请求 DTO ----

public sealed class LoginRequest
{
    public string? Password { get; set; }
    public bool Remember { get; set; }
}

public sealed class SendMessageRequest
{
    public string? Text { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }

    /// <summary>设备名来源：<c>auto</c>（UA 识别，可被纠正）或 <c>user</c>（用户手动设置）。</summary>
    public string? NameSource { get; set; }
}

public sealed class CompleteRequest
{
    public bool Completed { get; set; }
}

public sealed class RenameDeviceRequest
{
    public string? Name { get; set; }
    public string? RequesterId { get; set; }

    /// <summary>设备名来源：<c>auto</c>（UA 识别）或 <c>user</c>（用户手动设置，默认）。</summary>
    public string? NameSource { get; set; }
}

public sealed class ClearRequest
{
    public string? Scope { get; set; }
}

public sealed class PasswordRequest
{
    public string? Password { get; set; }
}

public sealed class AdminSettingsRequest
{
    public bool? LanEnabled { get; set; }
    public int? LanPort { get; set; }
    public int? AdminPort { get; set; }
    public int? SessionDays { get; set; }
    public bool? NotificationEnabled { get; set; }
    public double? NotificationMaskSeconds { get; set; }
    public double? NotificationOverlaySeconds { get; set; }
    public bool? NotificationSpeech { get; set; }
    public int? MessageRetention { get; set; }
}
