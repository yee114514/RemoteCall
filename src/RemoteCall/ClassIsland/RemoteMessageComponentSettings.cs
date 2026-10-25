using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RemoteCall.ClassIsland;

/// <summary>
/// 「远程消息」组件的设置（随组件布局一起持久化，属性名用 PascalCase 与 ClassIsland 的序列化一致）。
/// </summary>
public sealed class RemoteMessageComponentSettings : INotifyPropertyChanged
{
    private double _slideIntervalSeconds = 5;
    private double _slideDurationSeconds = 0.35;
    private double _viewportHeight = 44;
    private bool _includeCompleted;
    private bool _newestFirst = true;
    private int _maxItems = 20;
    private string _emptyText = "";

    /// <summary>每隔多少秒向上切换到下一条消息。</summary>
    public double SlideIntervalSeconds
    {
        get => _slideIntervalSeconds;
        set => Set(ref _slideIntervalSeconds, value);
    }

    /// <summary>上滑动画的时长（秒）。</summary>
    public double SlideDurationSeconds
    {
        get => _slideDurationSeconds;
        set => Set(ref _slideDurationSeconds, value);
    }

    /// <summary>显示区域高度（像素）。</summary>
    public double ViewportHeight
    {
        get => _viewportHeight;
        set => Set(ref _viewportHeight, value);
    }

    /// <summary>是否把「已完成展示」的消息也一起轮播。</summary>
    public bool IncludeCompleted
    {
        get => _includeCompleted;
        set => Set(ref _includeCompleted, value);
    }

    /// <summary>true = 最新的消息优先显示；false = 最早的未完成消息优先。</summary>
    public bool NewestFirst
    {
        get => _newestFirst;
        set => Set(ref _newestFirst, value);
    }

    /// <summary>最多参与轮播的消息条数。</summary>
    public int MaxItems
    {
        get => _maxItems;
        set => Set(ref _maxItems, value);
    }

    /// <summary>没有消息时显示的占位文本，留空表示不显示任何内容。</summary>
    public string EmptyText
    {
        get => _emptyText;
        set => Set(ref _emptyText, value ?? "");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
