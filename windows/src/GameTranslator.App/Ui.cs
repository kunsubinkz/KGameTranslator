using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GameTranslator.App;

/// <summary>Mục chọn trong ComboBox (hiển thị bằng ToString).</summary>
public sealed record Choice(string Id, string Text)
{
    public override string ToString() => Text;
}

/// <summary>Hàm dựng giao diện bằng mã cho gọn (các cửa sổ không dùng AXAML).</summary>
public static class Ui
{
    public static TextBlock Label(string text, double size = 13, FontWeight weight = FontWeight.Normal, double opacity = 1) =>
        new() { Text = text, FontSize = size, FontWeight = weight, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
    };

    public static Button Button(string text, Action onClick, bool accent = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Center };
        if (accent) b.Classes.Add("accent");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static ComboBox Combo(IEnumerable<Choice> items, string? selectedId, Action<Choice> onChange, double width = 280)
    {
        var list = items.ToList();
        var cb = new ComboBox { ItemsSource = list, Width = width, MinHeight = 32 };
        cb.SelectedItem = list.FirstOrDefault(c => c.Id == selectedId) ?? list.FirstOrDefault();
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is Choice c) onChange(c); };
        return cb;
    }

    public static NumericUpDown Number(double value, double min, double max, double step, Action<double> onChange, string format = "0.##", double width = 130)
    {
        var n = new NumericUpDown
        {
            Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step,
            FormatString = format, Width = width, MinHeight = 32,
        };
        n.ValueChanged += (_, e) => { if (e.NewValue is { } v) onChange((double)v); };
        return n;
    }

    public static CheckBox Check(string text, bool value, Action<bool> onChange)
    {
        var c = new CheckBox { Content = text, IsChecked = value };
        c.IsCheckedChanged += (_, _) => onChange(c.IsChecked == true);
        return c;
    }

    /// <summary>Dòng "nhãn — điều khiển" cho các trang cài đặt.</summary>
    public static Grid Row(string label, Control control, string? hint = null, double labelWidth = 200)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*"), Margin = new Thickness(0, 6) };
        var l = Label(label);
        l.VerticalAlignment = VerticalAlignment.Top;
        l.Margin = new Thickness(0, 6, 8, 0);
        g.Children.Add(l);
        var right = new StackPanel { Spacing = 2 };
        right.Children.Add(control);
        if (hint is not null) right.Children.Add(Hint(hint));
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    public static StackPanel Stack(params Control[] children)
    {
        var s = new StackPanel { Spacing = 4 };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Horizontal(double spacing = 8, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static Border Card(Control child) => new()
    {
        Padding = new Thickness(14), CornerRadius = new CornerRadius(8), Child = child,
        Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), BorderThickness = new Thickness(1),
    };

    public static ScrollViewer Scroll(Control content) => new()
    {
        Content = new Border { Padding = new Thickness(20), Child = content },
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };
}
