using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ClassIsland.Core.Controls;
using FluentAvalonia.UI.Controls;

namespace RemoteCall.ClassIsland;

/// <summary>
/// 用 C# 构建 Avalonia 界面时用到的小工具（不依赖 XAML，避免插件侧 XAML 编译链问题）。
/// 这里尽量复用 ClassIsland 自己的设置页规范：settings-container + SettingsExpander。
/// </summary>
internal static class Ui
{
    /// <summary>分区标题（图标 + 文字），与 ClassIsland 内置设置页一致。</summary>
    public static IconText Section(string glyph, string text)
    {
        return new IconText
        {
            Glyph = glyph,
            Text = text,
            Margin = new Thickness(0, 12, 0, 4),
        };
    }

    /// <summary>一行设置：标题 + 说明 + 右侧操作控件。</summary>
    public static SettingsExpander Setting(string header, string description, Control? footer, string? glyph = null)
    {
        var expander = new SettingsExpander
        {
            Header = header,
            Description = description,
        };

        if (glyph != null)
        {
            expander.IconSource = new FluentIconSource(glyph);
        }

        if (footer != null)
        {
            expander.Footer = footer;
        }

        expander.Margin = new Thickness(0, 0, 0, 2);
        return expander;
    }

    public static TextBlock Text(string text, bool wrap = true, double opacity = 1, double fontSize = 0)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = wrap ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap,
            Opacity = opacity,
            FontSize = fontSize > 0 ? fontSize : double.NaN,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static Button TextButton(string text, Action onClick)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static TextBox PasswordBox(string watermark, double width = 240) => new()
    {
        Watermark = watermark,
        Width = width,
        PasswordChar = '•',
        MaxLength = 64,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static NumericUpDown Number(double value, double min, double max, double increment = 1)
    {
        return new NumericUpDown
        {
            Value = (decimal)value,
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = (decimal)increment,
            FormatString = "0.##",
            MinWidth = 140,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static ToggleSwitch Switch(bool value)
    {
        return new ToggleSwitch
        {
            IsChecked = value,
            OnContent = "开启",
            OffContent = "关闭",
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static StackPanel ButtonRow(params Control[] controls)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
        };
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }

        return panel;
    }
}
