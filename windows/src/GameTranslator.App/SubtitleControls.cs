using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GameTranslator.Core;

namespace GameTranslator.App;

/// <summary>Các tùy chỉnh phụ đề (vị trí, căn lề, cỡ chữ, độ mờ nền, số dòng). Dùng chung cho tab Phụ đề và bảng chỉnh nhanh.
/// Mọi thay đổi áp dụng ngay lên video và lưu vào hồ sơ đang dùng.</summary>
public static class SubtitleControls
{
    public static Control Build(Session s, bool compact = false)
    {
        var sub = s.Current.Subtitle;
        void Apply() { s.Profiles.Save(s.Current); s.RefreshOverlay(); }

        Grid Slider(string label, double min, double max, double step, double value, string format, Action<double> set)
        {
            var valueText = Ui.Label(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, value), 13);
            valueText.Width = compact ? 48 : 56;
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 5, TickFrequency = step, Width = compact ? 150 : 260 };
            slider.ValueChanged += (_, _) =>
            {
                var v = Math.Round(slider.Value / step) * step;
                valueText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, v);
                set(v);
            };
            slider.PointerReleased += (_, _) => Apply();
            slider.KeyUp += (_, _) => Apply();
            return Ui.Row(label, Ui.Horizontal(8, slider, valueText), null, compact ? 100 : 200);
        }

        var panel = new StackPanel { Spacing = 2, Width = compact ? 400 : double.NaN };
        panel.Children.Add(Ui.Row("Vị trí", Ui.Combo(
            [new("Above", "Trên hộp thoại"), new("Below", "Dưới hộp thoại"), new("Over", "Đè lên hộp thoại"), new("Free", "Tự do (kéo thả trên video)")],
            sub.Position.ToString(), c => { sub.Position = Enum.Parse<SubtitlePosition>(c.Id); Apply(); }, compact ? 230 : 260), null, compact ? 100 : 200));
        panel.Children.Add(Ui.Row("Căn lề", Ui.Combo(
            [new("Left", "Trái"), new("Center", "Giữa"), new("Right", "Phải"), new("Justified", "Đều")],
            sub.Alignment.ToString(), c => { sub.Alignment = Enum.Parse<SubtitleAlignment>(c.Id); Apply(); }, compact ? 230 : 260), null, compact ? 100 : 200));
        panel.Children.Add(Slider("Cỡ chữ", 12, 80, 1, sub.FontSize, "{0:0}", v => { sub.FontSize = v; s.RefreshOverlay(); }));
        panel.Children.Add(Slider("Độ đậm nền", 0, 1, 0.05, sub.BackgroundOpacity, "{0:P0}", v => { sub.BackgroundOpacity = v; s.RefreshOverlay(); }));
        panel.Children.Add(Slider("Số dòng tối đa", 1, 30, 1, sub.MaxLines, "{0:0}", v => { sub.MaxLines = (int)v; s.RefreshOverlay(); }));
        panel.Children.Add(Ui.Hint("Phụ đề rộng bằng vùng OCR và tự xuống dòng. Nền đậm hơn thì chữ dễ đọc hơn trên nền game sáng. Ở chế độ Tự do, kéo phụ đề bằng chuột trên video."));
        return panel;
    }
}

/// <summary>Bảng chỉnh nhanh phụ đề: cửa sổ nhỏ luôn nằm trên, chỉnh là thấy ngay trên video.</summary>
public sealed class SubtitleQuickWindow : Window
{
    public SubtitleQuickWindow(Session s)
    {
        Title = "Phụ đề — " + s.Current.DisplayName;
        Width = 440; SizeToContent = SizeToContent.Height; CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new Border { Padding = new Thickness(16), Child = SubtitleControls.Build(s, compact: true) };
        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#232327"));
    }
}
