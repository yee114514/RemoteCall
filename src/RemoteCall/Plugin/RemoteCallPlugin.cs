using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteCall.ClassIsland;
using RemoteCall.Core.Auth;
using RemoteCall.Core.Config;
using RemoteCall.Core.Data;
using RemoteCall.Core.Server;

namespace RemoteCall.Plugin;

/// <summary>
/// 插件入口：注册网页服务、主界面组件、提醒提供方与设置页面。
/// </summary>
[PluginEntrance]
public class RemoteCallPlugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        var pluginFolder = string.IsNullOrWhiteSpace(PluginConfigFolder) ? "." : PluginConfigFolder;
        Directory.CreateDirectory(pluginFolder);

        var logs = new LogBuffer();
        var settings = new PluginSettingsService(pluginFolder, message => logs.Add(message));
        var store = new MessageStore(pluginFolder, message => logs.Add(message));
        var sessions = new SessionStore(Path.Combine(pluginFolder, "sessions.json"), message => logs.Add(message));
        var status = new ServerStatus();
        store.SetRetention(settings.Current.MessageRetention);

        services.AddSingleton(logs);
        services.AddSingleton(settings);
        services.AddSingleton(store);
        services.AddSingleton(sessions);
        services.AddSingleton(status);

        services.AddSingleton<ServerHost>(sp => new ServerHost(
            settings,
            store,
            sessions,
            status,
            logs,
            message => sp.GetRequiredService<ILogger<ServerHost>>().LogInformation("远程消息：{Message}", message)));
        services.AddHostedService<ServerHostedService>();

        services.AddComponent<RemoteMessageComponent, RemoteMessageComponentSettingsControl>();
        services.AddNotificationProvider<RemoteMessageNotificationProvider>();
        services.AddSettingsPage<RemoteCallSettingsPage>();
    }
}
