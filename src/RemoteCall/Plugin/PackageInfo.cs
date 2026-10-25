namespace RemoteCall.Plugin;

/// <summary>
/// 插件内部使用的固定标识与默认值。
/// </summary>
public static class PackageInfo
{
    /// <summary>插件清单 id（同时是插件配置目录名）。</summary>
    public const string PluginId = "remote.call";

    /// <summary>插件版本（与 manifest.yml 保持一致）。</summary>
    public const string Version = "1.0.0.0";

    /// <summary>主界面组件 GUID（带连字符的小写形式）。</summary>
    public const string ComponentGuidString = "7c4b2e51-9f3a-4d6e-b1c8-2a5d9e7f3b64";

    /// <summary>提醒提供方 GUID。</summary>
    public const string NotificationProviderGuidString = "e15a8c3d-6b47-4e92-8f1d-3c7b5a2e9d48";

    /// <summary>设置页面 id。</summary>
    public const string SettingsPageId = "remote.call.settings";

    /// <summary>组件图标（Fluent 字体单字符）。</summary>
    public const string ComponentIconGlyph = "\uE8BD";

    /// <summary>组件 GUID 的强类型形式。</summary>
    public static Guid ComponentGuid => Guid.Parse(ComponentGuidString);
}
