using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using Microsoft.Extensions.Logging;
using RemoteCall.Core.Config;
using RemoteCall.Core.Data;
using RemoteCall.Plugin;

namespace RemoteCall.ClassIsland;

/// <summary>
/// 提醒提供方：收到新消息时用 ClassIsland 的标准全屏提醒（遮罩 + 正文涟漪动画）提示。
/// </summary>
[NotificationProviderInfo(
    PackageInfo.NotificationProviderGuidString,
    "远程消息提醒",
    PackageInfo.ComponentIconGlyph,
    "局域网内其它设备通过网页发来消息时，在全屏提醒中滚动显示消息内容。")]
public sealed class RemoteMessageNotificationProvider : NotificationProviderBase, IDisposable
{
    private readonly MessageStore _store;
    private readonly PluginSettingsService _settings;
    private readonly ILogger<RemoteMessageNotificationProvider> _logger;
    private bool _subscribed;

    public RemoteMessageNotificationProvider(
        MessageStore store,
        PluginSettingsService settings,
        ILogger<RemoteMessageNotificationProvider> logger)
    {
        _store = store;
        _settings = settings;
        _logger = logger;

        _store.MessageAdded += OnMessageAdded;
        _subscribed = true;
    }

    private void OnMessageAdded(MessageView message)
    {
        var settings = _settings.Current;
        if (!settings.NotificationEnabled)
        {
            return;
        }

        // 提醒主机在 UI 线程消费提醒，这里统一切回 UI 线程
        Dispatcher.UIThread.Post(() => Show(message, settings));
    }

    private void Show(MessageView message, PluginSettings settings)
    {
        try
        {
            var maskDuration = TimeSpan.FromSeconds(Math.Clamp(settings.NotificationMaskSeconds, 1, 60));
            var overlayDuration = TimeSpan.FromSeconds(Math.Clamp(settings.NotificationOverlaySeconds, 1, 180));

            // 遮罩（带涟漪特效）阶段：只提示「有新消息」和发送者
            var mask = NotificationContent.CreateTwoIconsMask(
                $"新消息 · {message.DeviceName}",
                factory: content =>
                {
                    content.Duration = maskDuration;
                    content.IsSpeechEnabled = false;
                });

            // 涟漪特效之后的正文：静态显示，不滚动
            var overlay = NotificationContent.CreateSimpleTextContent(
                message.Text,
                content =>
                {
                    content.Duration = overlayDuration;
                    content.IsSpeechEnabled = settings.NotificationSpeech;
                    content.SpeechContent = $"{message.DeviceName}：{message.Text}";
                });

            ShowNotification(new NotificationRequest
            {
                MaskContent = mask,
                OverlayContent = overlay,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "显示远程消息提醒失败（消息 #{Id}）", message.Id);
        }
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            _store.MessageAdded -= OnMessageAdded;
            _subscribed = false;
        }
    }
}
