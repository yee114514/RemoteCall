using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using FluentAvalonia.UI.Controls;
using RemoteCall.Core.Config;
using RemoteCall.Core.Data;
using RemoteCall.Core.Server;
using RemoteCall.Plugin;

namespace RemoteCall.ClassIsland;

/// <summary>
/// 应用设置 →「远程消息」页面。
/// 视觉上完全复用 ClassIsland 内置设置页的规范（settings-container + SettingsExpander），
/// 长文本放可换行的 Description 里，Footer 只放短控件，避免把卡片撑出窗口。
/// </summary>
[SettingsPageInfo(PackageInfo.SettingsPageId, "远程消息", "\uE8BD", "\uE8BD")]
public sealed class RemoteCallSettingsPage : SettingsPageBase
{
    private readonly PluginSettingsService _settings;
    private readonly ServerHost _serverHost;
    private readonly MessageStore _store;
    private readonly ServerStatus _status;
    private readonly LogBuffer _logs;

    private readonly ToggleSwitch _lanEnabled;
    private readonly NumericUpDown _lanPort;
    private readonly NumericUpDown _adminPort;
    private readonly NumericUpDown _sessionDays;
    private readonly NumericUpDown _retention;
    private readonly ToggleSwitch _notificationEnabled;
    private readonly NumericUpDown _maskSeconds;
    private readonly NumericUpDown _overlaySeconds;
    private readonly ToggleSwitch _notificationSpeech;
    private readonly TextBox _newPassword;

    private readonly SettingsExpander _lanSetting;
    private readonly SettingsExpander _adminSetting;
    private readonly SettingsExpander _urlsSetting;
    private readonly SettingsExpander _counterSetting;
    private readonly SettingsExpander _passwordSetting;
    private readonly TextBlock _hint;
    private readonly DispatcherTimer _refreshTimer;
    private bool _loading;

    public RemoteCallSettingsPage(
        PluginSettingsService settings,
        ServerHost serverHost,
        MessageStore store,
        ServerStatus status,
        LogBuffer logs)
    {
        _settings = settings;
        _serverHost = serverHost;
        _store = store;
        _status = status;
        _logs = logs;

        _hint = Ui.Text("", opacity: 0.8, fontSize: 12.5);

        _lanEnabled = Ui.Switch(true);
        _lanPort = Ui.Number(CoreDefaults.LanPort, 1, 65535);
        _adminPort = Ui.Number(CoreDefaults.AdminPort, 1, 65535);
        _sessionDays = Ui.Number(CoreDefaults.SessionDays, -1, 365);
        _retention = Ui.Number(CoreDefaults.MessageRetention, -1, 10000);
        _notificationEnabled = Ui.Switch(true);
        _maskSeconds = Ui.Number(CoreDefaults.NotificationMaskSeconds, 1, 60, 0.5);
        _overlaySeconds = Ui.Number(CoreDefaults.NotificationOverlaySeconds, 1, 180, 0.5);
        _notificationSpeech = Ui.Switch(false);
        _notificationSpeech.OnContent = "朗读";
        _notificationSpeech.OffContent = "静音";
        _newPassword = Ui.PasswordBox("新密码（4~64 位）", 200);

        // 状态/说明类卡片：值都放在 Description（可换行），不塞进 Footer
        _lanSetting = Ui.Setting("局域网网页", "其它设备访问的网页服务状态。", null, "\uE774");
        _adminSetting = Ui.Setting("本机管理页", "只监听 127.0.0.1，用于管理历史与密码。", null, "\uE7F4");
        _urlsSetting = Ui.Setting("局域网访问地址", "", Ui.TextButton("复制", () => _ = CopyAddressAsync()), "\uE968");
        _counterSetting = Ui.Setting("消息统计", "", null, "\uE9D2");
        _passwordSetting = Ui.Setting("当前密码", "", null, "\uE8D7");

        Content = BuildContent();
        LoadFromSettings();
        RefreshStatus();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshCountersOnly();
    }

    private Control BuildContent()
    {
        // 与 ClassIsland 内置设置页同样的容器 class：内边距、最大宽度、入场动画都交给宿主主题
        var panel = new StackPanel();
        panel.Classes.Add("settings-container");
        panel.Classes.Add("animated-intro");

        // ---------------- 服务状态 ----------------
        panel.Children.Add(Ui.Section("\uE9D9", "服务状态"));
        panel.Children.Add(_lanSetting);
        panel.Children.Add(_adminSetting);
        panel.Children.Add(_urlsSetting);
        panel.Children.Add(_counterSetting);

        var actionRow = Ui.ButtonRow(
            Ui.TextButton("打开本机管理页", OpenAdminPage),
            Ui.TextButton("重启监听", RestartServers),
            Ui.TextButton("插入测试消息", InsertTestMessage));
        actionRow.Margin = new Thickness(0, 4, 0, 12);
        panel.Children.Add(actionRow);

        // ---------------- 服务设置 ----------------
        panel.Children.Add(Ui.Section("\uE713", "服务设置"));
        panel.Children.Add(Ui.Setting("开启局域网网页", "关闭后其它设备无法访问网页，本机管理页仍然可用。", _lanEnabled, "\uE774"));
        panel.Children.Add(Ui.Setting("局域网端口", $"手机等设备访问 http://<本机IP>:端口/ 。默认 {CoreDefaults.LanPort}。", _lanPort, "\uE968"));
        panel.Children.Add(Ui.Setting("本机管理端口", $"只监听 127.0.0.1。默认 {CoreDefaults.AdminPort}。", _adminPort, "\uE7F4"));
        panel.Children.Add(Ui.Setting("记住登录天数", $"勾选「记住登录状态」的设备在这个天数内不需要重新登录。填 {CoreDefaults.Unlimited} 表示不限制。", _sessionDays, "\uE8D7"));
        panel.Children.Add(Ui.Setting("历史保留条数", $"超过上限时自动丢弃最旧的消息。填 {CoreDefaults.Unlimited} 表示不限制（全部保留）。", _retention, "\uE81C"));

        // ---------------- 访问密码 ----------------
        panel.Children.Add(Ui.Section("\uE72E", "访问密码"));
        panel.Children.Add(_passwordSetting);

        var passwordFooter = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        passwordFooter.Children.Add(_newPassword);
        passwordFooter.Children.Add(Ui.TextButton("设置", SetPassword));
        passwordFooter.Children.Add(Ui.TextButton("随机生成", GeneratePassword));
        panel.Children.Add(Ui.Setting("修改访问密码", "修改后所有已登录的设备都需要重新登录。", passwordFooter, "\uE72E"));

        // ---------------- 新消息提醒 ----------------
        panel.Children.Add(Ui.Section("\uEA8F", "新消息提醒"));
        panel.Children.Add(Ui.Setting("新消息提醒", "使用 ClassIsland 的提醒（遮罩 + 正文）提示设备发来的新消息。", _notificationEnabled, "\uEA8F"));
        panel.Children.Add(Ui.Setting("遮罩时长（秒）", "提醒进入时的遮罩显示时长。", _maskSeconds, "\uE916"));
        panel.Children.Add(Ui.Setting("正文时长（秒）", "遮罩之后正文静止显示的时长。", _overlaySeconds, "\uE916"));
        panel.Children.Add(Ui.Setting("语音朗读", "开启后提醒会朗读消息内容（需要 ClassIsland 已启用语音）。", _notificationSpeech, "\uE767"));

        // ---------------- 保存 ----------------
        var saveRow = Ui.ButtonRow(
            Ui.TextButton("保存并应用", SaveAll),
            Ui.TextButton("重新载入", () =>
            {
                LoadFromSettings();
                RefreshStatus();
                _hint.Text = "已重新载入当前设置。";
            }));
        saveRow.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(saveRow);
        panel.Children.Add(_hint);

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _status.Updated += OnStatusUpdated;
        _refreshTimer.Start();
        RefreshStatus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _status.Updated -= OnStatusUpdated;
        _refreshTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnStatusUpdated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshStatus);
    }

    // ---------------- 读取 / 写入设置 ----------------

    private void LoadFromSettings()
    {
        var settings = _settings.Current;
        _loading = true;
        try
        {
            _lanEnabled.IsChecked = settings.LanEnabled;
            _lanPort.Value = settings.LanPort;
            _adminPort.Value = settings.AdminPort;
            _sessionDays.Value = settings.SessionDays;
            _retention.Value = settings.MessageRetention;
            _notificationEnabled.IsChecked = settings.NotificationEnabled;
            _maskSeconds.Value = (decimal)settings.NotificationMaskSeconds;
            _overlaySeconds.Value = (decimal)settings.NotificationOverlaySeconds;
            _notificationSpeech.IsChecked = settings.NotificationSpeech;
        }
        finally
        {
            _loading = false;
        }

        UpdatePasswordState();
    }

    private void SaveAll()
    {
        if (_loading)
        {
            return;
        }

        var ok = _settings.Update(settings =>
        {
            settings.LanEnabled = _lanEnabled.IsChecked == true;
            settings.LanPort = (int)(_lanPort.Value ?? settings.LanPort);
            settings.AdminPort = (int)(_adminPort.Value ?? settings.AdminPort);
            settings.SessionDays = (int)(_sessionDays.Value ?? settings.SessionDays);
            settings.MessageRetention = (int)(_retention.Value ?? settings.MessageRetention);
            settings.NotificationEnabled = _notificationEnabled.IsChecked == true;
            settings.NotificationMaskSeconds = (double)(_maskSeconds.Value ?? (decimal)settings.NotificationMaskSeconds);
            settings.NotificationOverlaySeconds = (double)(_overlaySeconds.Value ?? (decimal)settings.NotificationOverlaySeconds);
            settings.NotificationSpeech = _notificationSpeech.IsChecked == true;
        }, out var error);

        if (!ok)
        {
            _hint.Text = "保存失败：" + (error ?? "设置不合法。");
            return;
        }

        _hint.Text = "已保存。端口或开关的改动会在约 0.3 秒内生效。";
        LoadFromSettings();
        RefreshStatus();
    }

    private void SetPassword()
    {
        var password = _newPassword.Text?.Trim() ?? "";
        if (!_settings.SetPassword(password, out var error))
        {
            _hint.Text = "设置密码失败：" + (error ?? "密码不合法。");
            return;
        }

        _serverHost.Context.Sessions.Clear();
        _newPassword.Text = "";
        _hint.Text = "访问密码已更新，所有设备需要重新登录。";
        UpdatePasswordState();
    }

    private void GeneratePassword()
    {
        var generated = _settings.GenerateAndSetPassword(out var error);
        if (error != null)
        {
            _hint.Text = "生成密码失败：" + error;
            return;
        }

        _serverHost.Context.Sessions.Clear();
        _newPassword.Text = generated;
        _hint.Text = "已生成随机密码：" + generated + "（记下后可以点「设置」应用它）";
        UpdatePasswordState();
    }

    private void UpdatePasswordState()
    {
        var settings = _settings.Current;
        _passwordSetting.Description = string.IsNullOrEmpty(settings.GeneratedPassword)
            ? "当前使用自定义密码。"
            : $"当前使用默认密码：{settings.GeneratedPassword}（建议改成自己的密码）";
    }

    // ---------------- 状态 ----------------

    private void RefreshStatus()
    {
        var snapshot = _status.Snapshot();

        _lanSetting.Description = !snapshot.LanEnabled
            ? "已关闭。其它设备无法访问网页，本机管理页仍然可用。"
            : snapshot.LanRunning
                ? $"运行中 · 端口 {snapshot.LanPort}"
                : $"未运行 · {snapshot.LanError ?? "未知原因"}";

        _adminSetting.Description = snapshot.AdminRunning
            ? $"运行中 · 端口 {snapshot.AdminPort} · 只监听 127.0.0.1"
            : $"未运行 · {snapshot.AdminError ?? "未知原因"}";

        _urlsSetting.Description = snapshot.LanUrls.Count > 0
            ? "手机、平板连接到同一个局域网后访问：" + Environment.NewLine + string.Join(Environment.NewLine, snapshot.LanUrls)
            : "未获取到局域网地址（请检查电脑是否已连上局域网）。";

        RefreshCountersOnly();
    }

    private void RefreshCountersOnly()
    {
        var snapshot = _status.Snapshot();
        var summary = $"共 {_store.Count} 条消息，其中未完成 {_store.PendingCount} 条；已处理请求 {snapshot.RequestCount} 次。";
        var logs = _logs.Snapshot();
        if (logs.Count > 0)
        {
            summary += Environment.NewLine + $"最近日志：{logs[^1].Message}";
        }

        _counterSetting.Description = summary;
    }

    private void RestartServers()
    {
        _serverHost.Restart();
        _hint.Text = "已重启监听。";
        RefreshStatus();
    }

    private void OpenAdminPage()
    {
        var url = $"http://127.0.0.1:{_settings.Current.AdminPort}/";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _hint.Text = "已在默认浏览器打开：" + url;
        }
        catch (Exception ex)
        {
            _hint.Text = "无法打开浏览器：" + ex.Message;
        }
    }

    private async Task CopyAddressAsync()
    {
        var snapshot = _status.Snapshot();
        var url = snapshot.LanUrls.FirstOrDefault() ?? $"http://127.0.0.1:{_settings.Current.LanPort}/";
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                _hint.Text = "剪贴板不可用，请手动记下：" + url;
                return;
            }

            await clipboard.SetTextAsync(url);
            _hint.Text = "已复制：" + url;
        }
        catch (Exception ex)
        {
            _hint.Text = "复制失败：" + ex.Message + "（可以手动使用 " + url + "）";
        }
    }

    private void InsertTestMessage()
    {
        var message = _store.Add("这是一条测试消息，用于确认组件和提醒是否正常。", "settings-selftest", "本机测试", "127.0.0.1");
        _hint.Text = $"已插入测试消息 #{message.Id}。";
        RefreshCountersOnly();
    }
}
