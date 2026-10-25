using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ClassIsland.Core.Abstractions.Controls;
using RemoteCall.Core.Data;

namespace RemoteCall.ClassIsland;

/// <summary>
/// 「远程消息」组件的设置控件（在每个组件实例上独立设置）。
/// </summary>
public sealed class RemoteMessageComponentSettingsControl : ComponentBase<RemoteMessageComponentSettings>
{
    private readonly NumericUpDown _interval;
    private readonly NumericUpDown _duration;
    private readonly NumericUpDown _viewportHeight;
    private readonly NumericUpDown _maxItems;
    private readonly CheckBox _includeCompleted;
    private readonly CheckBox _newestFirst;
    private readonly TextBox _emptyText;
    private readonly MessageStore _store;
    private readonly TextBlock _preview;
    private bool _loading;

    public RemoteMessageComponentSettingsControl(MessageStore store)
    {
        _store = store;

        _interval = Ui.Number(5, 1, 600, 1);
        _duration = Ui.Number(0.35, 0.05, 2, 0.05);
        _viewportHeight = Ui.Number(44, 20, 600, 4);
        _maxItems = Ui.Number(20, 1, 200, 1);
        _includeCompleted = new CheckBox { Content = "同时轮播已完成展示的消息", VerticalAlignment = VerticalAlignment.Center };
        _newestFirst = new CheckBox { Content = "最新的消息优先显示", VerticalAlignment = VerticalAlignment.Center };
        _emptyText = new TextBox
        {
            Watermark = "留空表示没有消息时不显示内容",
            Width = 280,
            MaxLength = 100,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _preview = Ui.Text("", opacity: 0.8, fontSize: 12.5);

        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(4, 4, 12, 8) };
        panel.Children.Add(Ui.Setting("切换间隔（秒）", "每隔多少秒向上滑动切换到下一条消息。", _interval));
        panel.Children.Add(Ui.Setting("动画时长（秒）", "上滑动画持续时间，0.35 秒左右比较自然。", _duration));
        panel.Children.Add(Ui.Setting("显示高度", "组件显示区域高度（像素），一般够显示一条消息即可。", _viewportHeight));
        panel.Children.Add(Ui.Setting("最多显示条数", "参与轮播的消息条数上限。", _maxItems));
        panel.Children.Add(Ui.Setting("显示已完成", "默认只轮播「未完成展示」的消息。", _includeCompleted));
        panel.Children.Add(Ui.Setting("排序", "勾选时最新消息优先，取消勾选时最早的未完成消息优先。", _newestFirst));
        panel.Children.Add(Ui.Setting("空状态文本", "没有任何消息时显示的占位文字。", _emptyText));
        panel.Children.Add(_preview);

        Content = panel;

        _interval.ValueChanged += (_, _) => Apply();
        _duration.ValueChanged += (_, _) => Apply();
        _viewportHeight.ValueChanged += (_, _) => Apply();
        _maxItems.ValueChanged += (_, _) => Apply();
        _includeCompleted.IsCheckedChanged += (_, _) => Apply();
        _newestFirst.IsCheckedChanged += (_, _) => Apply();
        _emptyText.TextChanged += (_, _) => Apply();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        if (Settings == null)
        {
            return;
        }

        _loading = true;
        try
        {
            _interval.Value = (decimal)Math.Clamp(Settings.SlideIntervalSeconds, 1, 600);
            _duration.Value = (decimal)Math.Clamp(Settings.SlideDurationSeconds, 0.05, 2);
            _viewportHeight.Value = (decimal)Math.Clamp(Settings.ViewportHeight, 20, 600);
            _maxItems.Value = Math.Clamp(Settings.MaxItems, 1, 200);
            _includeCompleted.IsChecked = Settings.IncludeCompleted;
            _newestFirst.IsChecked = Settings.NewestFirst;
            _emptyText.Text = Settings.EmptyText ?? "";
        }
        finally
        {
            _loading = false;
        }

        UpdatePreview();
    }

    private void Apply()
    {
        if (_loading || Settings == null)
        {
            return;
        }

        Settings.SlideIntervalSeconds = (double)(_interval.Value ?? 5m);
        Settings.SlideDurationSeconds = (double)(_duration.Value ?? 0.35m);
        Settings.ViewportHeight = (double)(_viewportHeight.Value ?? 44m);
        Settings.MaxItems = (int)Math.Clamp(_maxItems.Value ?? 20m, 1m, 200m);
        Settings.IncludeCompleted = _includeCompleted.IsChecked == true;
        Settings.NewestFirst = _newestFirst.IsChecked == true;
        Settings.EmptyText = _emptyText.Text ?? "";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var pending = _store.PendingCount;
        _preview.Text = pending > 0
            ? $"当前有 {pending} 条未完成的消息会在主界面轮流显示。"
            : "当前没有未完成的消息。发一条消息试试看吧。";
    }
}
