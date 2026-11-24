using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GameTranslator.Core;
using GameTranslator.Core.Pipeline;
using GameTranslator.Platform.Windows.Ocr;

namespace GameTranslator.App;

/// <summary>Chọn vùng hộp thoại (vùng chính + các vùng phụ) trên ảnh tĩnh lấy từ khung mới nhất.</summary>
public sealed class RegionSelectorWindow : Window
{
    private readonly Session _s;
    private readonly byte[] _pixels;
    private readonly int _w, _h;
    private readonly List<Region> _regions;
    private int _selected;
    private readonly Canvas _canvas;
    private readonly TextBox _result = new() { IsReadOnly = true, MinHeight = 60, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
    private readonly TextBlock _info = Ui.Label("", 12, opacity: 0.7);

    private enum Drag { None, Draw, Move, Resize }
    private Drag _drag;
    private Point _start;
    private Region _origin = new(0, 0, 0, 0);
    private int _handle;

    private const double HandleSize = 14;

    public RegionSelectorWindow(Session s, (byte[] Pixels, int Width, int Height) snap)
    {
        _s = s;
        _pixels = snap.Pixels; _w = snap.Width; _h = snap.Height;
        _regions = s.Current.AllRegions.Select(r => r with { }).ToList();
        Title = $"Chọn vùng hộp thoại — {s.Current.DisplayName}";
        Width = 1100; Height = 780; MinWidth = 700; MinHeight = 500;

        var bmp = new WriteableBitmap(new PixelSize(_w, _h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            for (int y = 0; y < _h; y++)
                System.Runtime.InteropServices.Marshal.Copy(_pixels, y * _w * 4, fb.Address + y * fb.RowBytes, _w * 4);
        }

        _canvas = new Canvas { Width = _w, Height = _h, Background = Brushes.Black };
        _canvas.PointerPressed += OnDown;
        _canvas.PointerMoved += OnMove;
        _canvas.PointerReleased += (_, _) => _drag = Drag.None;
        var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };
        _canvas.Children.Add(new Image { Source = bmp, Width = _w, Height = _h });

        var bar = Ui.Horizontal(8,
            Ui.Button("Thêm vùng", () => { _regions.Add(new Region(0.1, 0.55, 0.3, 0.1)); _selected = _regions.Count - 1; Redraw(); }),
            Ui.Button("Xóa vùng", () => { if (_regions.Count > 1) { _regions.RemoveAt(_selected); _selected = Math.Max(0, _selected - 1); Redraw(); } }),
            Ui.Button("Thử OCR các vùng", TestOcr),
            Ui.Button("Lưu", Save, accent: true),
            Ui.Button("Hủy", Close));
        bar.Margin = new Thickness(10);

        var bottom = new StackPanel { Margin = new Thickness(10, 0, 10, 10), Spacing = 6 };
        bottom.Children.Add(_info);
        bottom.Children.Add(_result);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(bottom);
        dock.Children.Add(viewbox);
        Content = dock;
        Redraw();
    }

    private Rect ToRect(Region r) => new(r.X * _w, r.Y * _h, r.Width * _w, r.Height * _h);

    private void Redraw()
    {
        while (_canvas.Children.Count > 1) _canvas.Children.RemoveAt(1);
        for (int i = 0; i < _regions.Count; i++)
        {
            var rect = ToRect(_regions[i].Clamped());
            var color = i == 0 ? Colors.Gold : Colors.DeepSkyBlue;
            var shape = new Rectangle
            {
                Width = rect.Width, Height = rect.Height, Stroke = new SolidColorBrush(color), StrokeThickness = i == _selected ? 4 : 2,
                Fill = new SolidColorBrush(Color.FromArgb(i == _selected ? (byte)40 : (byte)15, color.R, color.G, color.B)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(shape, rect.X); Canvas.SetTop(shape, rect.Y);
            _canvas.Children.Add(shape);
            var tag = new TextBlock { Text = i == 0 ? "Chính" : $"Phụ {i}", Foreground = new SolidColorBrush(color), FontSize = 26, FontWeight = FontWeight.Bold, IsHitTestVisible = false };
            Canvas.SetLeft(tag, rect.X + 6); Canvas.SetTop(tag, Math.Max(rect.Y - 34, 0));
            _canvas.Children.Add(tag);
            if (i == _selected)
            {
                foreach (var (hx, hy) in HandlePoints(rect))
                {
                    var hd = new Rectangle { Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = Brushes.Black, IsHitTestVisible = false };
                    Canvas.SetLeft(hd, hx - HandleSize / 2); Canvas.SetTop(hd, hy - HandleSize / 2);
                    _canvas.Children.Add(hd);
                }
            }
        }
        var m = _regions[_selected].Clamped();
        _info.Text = $"Đang chọn: {(_selected == 0 ? "vùng chính" : $"vùng phụ {_selected}")} · x {m.X:P0}, y {m.Y:P0}, rộng {m.Width:P0}, cao {m.Height:P0}. "
                     + "Kéo vào vùng để di chuyển, kéo chấm trắng để đổi cỡ, kéo ở chỗ trống để vẽ lại vùng đang chọn.";
    }

    private static IEnumerable<(double, double)> HandlePoints(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return (r.X, r.Y); yield return (cx, r.Y); yield return (r.Right, r.Y);
        yield return (r.X, cy); yield return (r.Right, cy);
        yield return (r.X, r.Bottom); yield return (cx, r.Bottom); yield return (r.Right, r.Bottom);
    }

    private void OnDown(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        _start = p;
        double slack = HandleSize * 1.2;
        // Chấm đổi cỡ của vùng đang chọn.
        var cur = ToRect(_regions[_selected].Clamped());
        int hi = 0;
        foreach (var (hx, hy) in HandlePoints(cur))
        {
            if (Math.Abs(p.X - hx) <= slack && Math.Abs(p.Y - hy) <= slack)
            {
                _drag = Drag.Resize; _handle = hi; _origin = _regions[_selected].Clamped(); return;
            }
            hi++;
        }
        // Bên trong một vùng: chọn rồi di chuyển (ưu tiên vùng đang chọn).
        var order = Enumerable.Range(0, _regions.Count).OrderBy(i => i == _selected ? 0 : 1);
        foreach (var i in order)
        {
            if (ToRect(_regions[i].Clamped()).Contains(p))
            {
                _selected = i; _drag = Drag.Move; _origin = _regions[i].Clamped(); Redraw(); return;
            }
        }
        _drag = Drag.Draw;
        _regions[_selected] = new Region(p.X / _w, p.Y / _h, 0.01, 0.01);
    }

    private void OnMove(object? sender, PointerEventArgs e)
    {
        if (_drag == Drag.None) return;
        var p = e.GetPosition(_canvas);
        double nx = Math.Clamp(p.X / _w, 0, 1), ny = Math.Clamp(p.Y / _h, 0, 1);
        switch (_drag)
        {
            case Drag.Draw:
            {
                double sx = Math.Clamp(_start.X / _w, 0, 1), sy = Math.Clamp(_start.Y / _h, 0, 1);
                _regions[_selected] = new Region(Math.Min(sx, nx), Math.Min(sy, ny), Math.Abs(nx - sx), Math.Abs(ny - sy)).Clamped();
                break;
            }
            case Drag.Move:
            {
                double dx = (p.X - _start.X) / _w, dy = (p.Y - _start.Y) / _h;
                _regions[_selected] = _origin with
                {
                    X = Math.Clamp(_origin.X + dx, 0, 1 - _origin.Width),
                    Y = Math.Clamp(_origin.Y + dy, 0, 1 - _origin.Height),
                };
                break;
            }
            case Drag.Resize:
            {
                double l = _origin.X, t = _origin.Y, r = _origin.X + _origin.Width, b = _origin.Y + _origin.Height;
                // Thứ tự chấm: 0 TL, 1 T, 2 TR, 3 L, 4 R, 5 BL, 6 B, 7 BR
                if (_handle is 0 or 3 or 5) l = nx;
                if (_handle is 2 or 4 or 7) r = nx;
                if (_handle is 0 or 1 or 2) t = ny;
                if (_handle is 5 or 6 or 7) b = ny;
                _regions[_selected] = new Region(Math.Min(l, r), Math.Min(t, b), Math.Abs(r - l), Math.Abs(b - t)).Clamped();
                break;
            }
        }
        Redraw();
    }

    private async void TestOcr()
    {
        _result.Text = "Đang đọc...";
        try
        {
            var ocr = new WindowsOcr();
            var parts = new List<string>();
            int i = 0;
            foreach (var r in _regions)
            {
                var c = r.Clamped();
                int x = (int)(c.X * _w), y = (int)(c.Y * _h);
                int w = Math.Clamp((int)(c.Width * _w), 1, _w - x), h = Math.Clamp((int)(c.Height * _h), 1, _h - y);
                var img = BgraImage.Create(w, h);
                for (int row = 0; row < h; row++) Buffer.BlockCopy(_pixels, ((y + row) * _w + x) * 4, img.Pixels, row * w * 4, w * 4);
                var res = await ocr.RecognizeAsync(Preprocessor.Prepare(img, _s.Current.Ocr), _s.Current.Ocr, []);
                var text = TextNormalizer.Normalize(res.Text);
                parts.Add($"[{(i == 0 ? "Chính" : "Phụ " + i)}] {(text ?? "(không đọc được chữ)")}  ({res.ElapsedMs:0} ms)");
                i++;
            }
            _result.Text = string.Join("\n", parts);
        }
        catch (Exception ex) { _result.Text = "Lỗi OCR: " + ex.Message; }
    }

    private void Save()
    {
        _s.Current.Region = _regions[0].Clamped();
        _s.Current.ExtraRegions = [.. _regions.Skip(1).Select(r => r.Clamped())];
        _s.SaveCurrentProfile();
        Close();
    }
}
