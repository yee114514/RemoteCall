using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using RemoteCall.Core.Data;
using RemoteCall.Core.Server;
using RemoteCall.Plugin;

namespace RemoteCall.ClassIsland;

/// <summary>
/// 主界面组件：一次显示一条消息，每隔一段时间向上滑动切换到下一条。
/// </summary>
[ComponentInfo(
    PackageInfo.ComponentGuidString,
    "远程消息",
    PackageInfo.ComponentIconGlyph,
    "在主界面轮流显示局域网设备发来的消息，按设定的间隔向上滑动切换。")]
public sealed class RemoteMessageComponent : ComponentBase<RemoteMessageComponentSettings>
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(30);

    private readonly MessageStore _store;
    private readonly DispatcherTimer _timer = new();
    private readonly Grid _root;
    private readonly Border _viewport;
    private readonly StackPanel _strip;
    private readonly TranslateTransform _translate = new();
    private readonly TextBlock _emptyText;
    private readonly List<MessageView> _messages = new();
    private readonly LogBuffer _logs;

    /// <summary>每一行（= 显示区高度）的控件，用于统一设置高度。</summary>
    private readonly List<Control> _rows = new();

    /// <summary>当前显示的行号（0 .. 消息数-1）。</summary>
    private int _index;
    private double _rowHeight;
    private double _currentY;
    private bool _sliding;
    private double _slideElapsed;
    private double _slideFrom;
    private double _slideTo;
    private double _intervalElapsed;
    private DateTime _lastTickUtc;
    private bool _attached;

    public RemoteMessageComponent(MessageStore store, LogBuffer logs)
    {
        _store = store;
        _logs = logs;

        _emptyText = new TextBlock
        {
            Opacity = 0.6,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        _strip = new StackPanel
        {
            RenderTransform = _translate,
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
        };

        _viewport = new Border
        {
            ClipToBounds = true,
            Child = _strip,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        _viewport.SizeChanged += (_, _) =>
        {
            // 宽度变化（其它组件刷新导致）不需要动；只有显示高度真的变了才重排行高
            if (Math.Abs(_rowHeight - _viewport.Bounds.Height) <= 0.5)
            {
                return;
            }

            ApplyRowHeights();
            RealignToCurrent();
        };

        _root = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _root.Children.Add(_viewport);
        _root.Children.Add(_emptyText);
        _root.SizeChanged += (_, _) => UpdateViewportHeight();
        Content = _root;

        _timer.Interval = TickInterval;
        _timer.Tick += OnTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _store.Changed += OnStoreChanged;
        if (Settings != null)
        {
            Settings.PropertyChanged += OnSettingsChanged;
        }

        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer.Stop();
        _store.Changed -= OnStoreChanged;
        if (Settings != null)
        {
            Settings.PropertyChanged -= OnSettingsChanged;
        }
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_attached)
            {
                Rebuild();
            }
        });
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_attached)
            {
                Rebuild();
            }
        });
    }

    /// <summary>
    /// 重建滚动条：每条消息一行，最后再放一条“第一条”，滚动到它时复位，视觉上无缝。
    /// </summary>
    private void Rebuild()
    {
        if (Settings == null)
        {
            return;
        }

        var maxItems = Math.Clamp(Settings.MaxItems, 1, 200);
        _messages.Clear();
        foreach (var message in _store.Query(MessageFilter.All, Math.Max(maxItems * 3, 50)))
        {
            if (!Settings.IncludeCompleted && message.Completed)
            {
                continue;
            }

            _messages.Add(message);
            if (_messages.Count >= maxItems)
            {
                break;
            }
        }

        if (!Settings.NewestFirst)
        {
            _messages.Reverse();
        }

        _strip.Children.Clear();
        _rows.Clear();
        _index = 0;
        _currentY = 0;
        _translate.Y = 0;
        _sliding = false;
        _slideElapsed = 0;
        _intervalElapsed = 0;

        if (_messages.Count == 0)
        {
            _viewport.IsVisible = false;
            _timer.Stop();
            var emptyText = (Settings.EmptyText ?? "").Trim();
            _emptyText.Text = emptyText;
            _emptyText.IsVisible = emptyText.Length > 0;
            return;
        }

        _emptyText.IsVisible = false;
        _viewport.IsVisible = true;
        UpdateViewportHeight();

        foreach (var message in _messages)
        {
            _strip.Children.Add(BuildRow(message));
        }

        if (_messages.Count > 1)
        {
            _strip.Children.Add(BuildRow(_messages[0]));
        }

        _lastTickUtc = DateTime.UtcNow;
        _logs.Add($"组件：重建轮播，{_messages.Count} 条消息");
        Dispatcher.UIThread.Post(() =>
        {
            ApplyRowHeights();
            ResetSlideState();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 显示高度 = 设置值，但不超过外面实际给到的高度（主窗口只有一行高时不会被裁掉）。
    /// </summary>
    private void UpdateViewportHeight()
    {
        if (Settings == null)
        {
            return;
        }

        var desired = Math.Clamp(Settings.ViewportHeight, 20, 600);
        var available = _root.Bounds.Height;
        var height = available > 4 ? Math.Min(desired, available) : desired;
        height = Math.Clamp(height, 20, 600);

        if (Math.Abs(_viewport.Height - height) <= 0.5)
        {
            return;
        }

        _viewport.Height = height;
        Dispatcher.UIThread.Post(() =>
        {
            ApplyRowHeights();
            RealignToCurrent();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 把当前条的位移重新对齐（不改变当前显示的是第几条，也不打断轮播节奏）。
    /// </summary>
    private void RealignToCurrent()
    {
        _sliding = false;
        _slideElapsed = 0;
        _currentY = -_index * _rowHeight;
        _translate.Y = _currentY;
        _lastTickUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// 每行高度都等于显示区高度：一次只看到一条消息，且内容垂直居中。
    /// </summary>
    private void ApplyRowHeights()
    {
        var height = _viewport.Bounds.Height;
        if (height <= 1 && Settings != null)
        {
            height = Math.Clamp(Settings.ViewportHeight, 20, 600);
        }

        if (height <= 1)
        {
            return;
        }

        foreach (var row in _rows)
        {
            // 注意：Auto 高度的初始值是 NaN，NaN 的比较恒为 false，必须单独判断
            if (double.IsNaN(row.Height) || Math.Abs(row.Height - height) > 0.5)
            {
                row.Height = height;
            }
        }

        _rowHeight = height;
    }

    private Control BuildRow(MessageView message)
    {
        var content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
        };

        var meta = new TextBlock
        {
            Text = $"{message.DeviceName} · {FormatTime(message.CreatedAt)}" + (message.Completed ? " · 已完成" : ""),
            FontSize = 11,
            Opacity = 0.62,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var text = new TextBlock
        {
            Text = message.Text,
            FontSize = 15,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        content.Children.Add(meta);
        content.Children.Add(text);

        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(content);
        _rows.Add(row);
        return row;
    }

    private void ResetSlideState()
    {
        _sliding = false;
        _slideElapsed = 0;
        _intervalElapsed = 0;
        _index = 0;
        _currentY = 0;
        _translate.Y = 0;
        _lastTickUtc = DateTime.UtcNow;

        if (!_attached || _messages.Count <= 1 || Settings == null)
        {
            _timer.Stop();
            return;
        }

        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_attached || _messages.Count <= 1 || Settings == null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var deltaSeconds = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0, 0.5);
        _lastTickUtc = now;

        if (_rowHeight <= 1)
        {
            ApplyRowHeights();
            if (_rowHeight <= 1)
            {
                return;
            }
        }

        if (_sliding)
        {
            var duration = Math.Clamp(Settings.SlideDurationSeconds, 0.05, 2);
            _slideElapsed += deltaSeconds;
            var progress = Math.Clamp(_slideElapsed / duration, 0, 1);
            var eased = EaseOutCubic(progress);
            _currentY = _slideFrom + (_slideTo - _slideFrom) * eased;
            _translate.Y = _currentY;

            if (progress < 1)
            {
                return;
            }

            _sliding = false;
            _slideElapsed = 0;
            _intervalElapsed = 0;
            _index++;

            if (_index >= _messages.Count)
            {
                // 已经滚到末尾那条“复制出来的第一条”，立刻复位（画面与第一条完全相同）
                _index = 0;
                _currentY = 0;
                _translate.Y = 0;
            }

            return;
        }

        var interval = Math.Clamp(Settings.SlideIntervalSeconds, 1, 600);
        _intervalElapsed += deltaSeconds;
        if (_intervalElapsed < interval)
        {
            return;
        }

        _intervalElapsed = 0;
        _slideFrom = _currentY;
        _slideTo = -(_index + 1) * _rowHeight;
        _slideElapsed = 0;
        _sliding = true;
    }

    private static double EaseOutCubic(double progress)
    {
        var inverse = 1 - progress;
        return 1 - inverse * inverse * inverse;
    }

    private static string FormatTime(DateTimeOffset time)
    {
        return time.ToLocalTime().ToString("HH:mm");
    }
}
