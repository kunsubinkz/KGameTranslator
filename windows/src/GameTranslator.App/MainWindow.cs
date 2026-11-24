using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using GameTranslator.Core;
using GameTranslator.Platform.Windows.Capture;

namespace GameTranslator.App;

public sealed class MainWindow : Window
{
    private static readonly IBrush ToolbarBg = new SolidColorBrush(Color.Parse("#2A2A2E"));
    private static readonly IBrush ToolbarLine = new SolidColorBrush(Color.Parse("#17171A"));
    private static readonly IBrush PillBg = new SolidColorBrush(Color.Parse("#3B3B42"));
    private static readonly IBrush StatusBg = new SolidColorBrush(Color.Parse("#202024"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#A9A9B2"));

    private readonly Session _s;
    private readonly VideoHost _video = new();
    private readonly ComboBox _cbVideo = new() { Width = 230 };
    private readonly ComboBox _cbProfile = new() { Width = 210 };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, Width = 110, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _tbTranslate = new();
    private readonly Border _toolbar = new() { Background = ToolbarBg, BorderBrush = ToolbarLine, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8) };
    private readonly Border _statusBar = new() { Background = StatusBg, BorderBrush = ToolbarLine, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 0), Height = 30 };
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, Fill = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stState = StatusText("");
    private readonly TextBlock _stModel = StatusText("");
    private readonly TextBlock _stQuota = StatusText("");
    private readonly TextBlock _stNllb = StatusText("");
    private readonly TextBlock _stRight = StatusText("");
    private readonly TextBlock _stNotice = StatusText("");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _tick;
    private bool _fullscreen;
    private bool _loadingDevices;
    private bool _dragging;
    private Window? _settings, _history, _profiles;

    private static TextBlock StatusText(string t) => new() { Text = t, FontSize = 12, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock IconGlyph(int codepoint, double size = 15)
    {
        var t = new TextBlock { Text = ((char)codepoint).ToString(), FontSize = size };
        t.Classes.Add("icon");
        return t;
    }

    public MainWindow(Session session)
    {
        _s = session;
        Title = "Game Translator";
        Width = 1280; Height = 780; MinWidth = 860; MinHeight = 520;
        Background = Brushes.Black;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://GameTranslator/Assets/icon.png"))); } catch (Exception) { }

        BuildToolbar();
        BuildStatusBar();
        var root = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        root.Children.Add(_toolbar);
        root.Children.Add(_statusBar);
        root.Children.Add(_video);
        Content = root;

        _video.HandleReady += OnVideoReady;
        _video.SurfaceResized += () => _s.Renderer?.Resize();
        _video.SurfaceMouse += OnSurfaceMouse;
        _s.Notice += m => { AppLog.Write("thong_bao: " + m); Dispatcher.UIThread.Post(() => _stNotice.Text = m); };
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _timer.Tick += (_, _) => OnTick();
        Closing += (_, _) => { _timer.Stop(); _s.Dispose(); };
        Opened += (_, _) => _timer.Start();
    }

    // ---------- Thanh công cụ (theo bản thiết kế) ----------

    private static Border Pill(Control child) => new()
    {
        Background = PillBg, CornerRadius = new CornerRadius(8), Height = 36, Child = child,
    };

    private static Button ToolButton(Control content, Action onClick, string? tip = null, bool outline = false)
    {
        var b = new Button { Content = content };
        b.Classes.Add("tool");
        if (outline) b.Classes.Add("outline");
        if (tip is not null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private void BuildToolbar()
    {
        // Nguồn hình
        _cbVideo.Classes.Add("tool");
        ToolTip.SetTip(_cbVideo, "Nguồn hình (capture card)");
        _cbVideo.SelectionChanged += (_, _) =>
        {
            if (_loadingDevices || _cbVideo.SelectedItem is not Choice c) return;
            _s.Settings.VideoDeviceId = c.Id;
            _s.Settings.VideoDeviceName = c.Text;
            _s.Settings.FormatIndex = null;
            if (_s.Settings.AudioEnabled && c.Id != TestPatternSource.DeviceId)
            {
                var audio = DeviceDiscovery.AudioInputs();
                if (DeviceDiscovery.BestAudioMatch(new CaptureDevice(c.Id, c.Text), audio) is { } a) _s.Settings.AudioInputId = a.Id;
            }
            _s.Settings.Save();
            _s.StartVideo();
        };
        var videoPill = Pill(Ui.Horizontal(0, new Border { Padding = new Thickness(12, 0, 2, 0), Child = IconGlyph(0xE722) }, _cbVideo));

        // Hồ sơ game
        _cbProfile.Classes.Add("tool");
        ToolTip.SetTip(_cbProfile, "Hồ sơ game (Ctrl+Shift+P để quản lý)");
        _cbProfile.SelectionChanged += (_, _) =>
        {
            if (_loadingDevices || _cbProfile.SelectedItem is not Choice c) return;
            _s.SelectProfile(c.Id);
        };
        var profilePill = Pill(Ui.Horizontal(0, new Border { Padding = new Thickness(12, 0, 2, 0), Child = IconGlyph(0xE80A) }, _cbProfile));

        var refresh = ToolButton(IconGlyph(0xE72C), RefreshDevices, "Quét lại thiết bị");

        // Âm lượng
        _volume.Value = _s.Settings.Volume;
        _volume.ValueChanged += (_, _) =>
        {
            _s.Settings.Volume = _volume.Value;
            _s.Audio.Volume = (float)_volume.Value;
        };
        _volume.PointerReleased += (_, _) => _s.Settings.Save();
        var volumeBox = Ui.Horizontal(8, IconGlyph(0xE767), _volume);
        volumeBox.VerticalAlignment = VerticalAlignment.Center;
        volumeBox.Margin = new Thickness(0, 0, 6, 0);

        // Chọn vùng, Dịch
        var region = ToolButton(Ui.Horizontal(8, IconGlyph(0xE7C4), new TextBlock { Text = "Chọn vùng", VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = "Ctrl+R", FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center }), OpenRegionSelector, null, outline: true);

        _tbTranslate.Classes.Add("tool");
        _tbTranslate.Padding = new Thickness(16, 0);
        _tbTranslate.FontWeight = FontWeight.SemiBold;
        // Dịch bật sẵn khi mở app (như bản macOS); nhớ lựa chọn lần trước.
        _tbTranslate.IsChecked = _s.Settings.TranslateEnabled;
        _s.SetTranslate(_s.Settings.TranslateEnabled);
        SetTranslateLabel();
        _tbTranslate.IsCheckedChanged += (_, _) =>
        {
            bool on = _tbTranslate.IsChecked == true;
            _s.SetTranslate(on);
            _s.Settings.TranslateEnabled = on;
            _s.Settings.Save();
            SetTranslateLabel();
        };
        ToolTip.SetTip(_tbTranslate, "Bật/tắt dịch (Ctrl+T)");

        var subtitle = ToolButton(Ui.Horizontal(6, IconGlyph(0xE8D2), new TextBlock { Text = "Phụ đề", VerticalAlignment = VerticalAlignment.Center }), OpenSubtitleQuick, "Chỉnh phụ đề: cỡ chữ, vị trí, căn lề, độ đậm nền (Ctrl+L)");
        var offline = ToolButton(Ui.Horizontal(6, IconGlyph(0xE753), new TextBlock { Text = "Offline", VerticalAlignment = VerticalAlignment.Center }), OpenLocalModel, "Model dịch offline NLLB: cài, tải model, chạy thử (Ctrl+Shift+M)");
        var fullscreen = ToolButton(IconGlyph(0xE740), ToggleFullscreen, "Toàn màn hình (F11)");
        var settings = ToolButton(IconGlyph(0xE713), OpenSettings, "Cài đặt (Ctrl+,)");

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(videoPill);
        left.Children.Add(refresh);
        left.Children.Add(profilePill);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(volumeBox);
        right.Children.Add(region);
        right.Children.Add(_tbTranslate);
        right.Children.Add(subtitle);
        right.Children.Add(offline);
        right.Children.Add(fullscreen);
        right.Children.Add(settings);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        _toolbar.Child = grid;
    }

    /// <summary>Nút "Dịch" kèm công tắc nhỏ như bản thiết kế: bật thì nền xanh, núm sang phải.</summary>
    private void SetTranslateLabel()
    {
        bool on = _tbTranslate.IsChecked == true;
        var knob = new Ellipse
        {
            Width = 14, Height = 14, Fill = Brushes.White,
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(2, 0),
        };
        var track = new Border
        {
            Width = 34, Height = 18, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(on ? Color.FromArgb(90, 255, 255, 255) : Color.Parse("#62626C")),
            Child = new Panel { Children = { knob } },
        };
        _tbTranslate.Content = Ui.Horizontal(10,
            new TextBlock { Text = on ? "Dịch bật" : "Dịch tắt", VerticalAlignment = VerticalAlignment.Center }, track);
    }

    private void BuildStatusBar()
    {
        var history = new TextBlock { Text = "Lịch sử  Ctrl+H", FontSize = 12, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        history.PointerPressed += (_, _) => OpenHistory();
        var profiles = new TextBlock { Text = "Hồ sơ  Ctrl+Shift+P", FontSize = 12, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        profiles.PointerPressed += (_, _) => OpenProfiles();

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        left.Children.Add(Ui.Horizontal(7, _dot, _stState));
        left.Children.Add(_stModel);
        left.Children.Add(_stQuota);
        _stNllb.Cursor = new Cursor(StandardCursorType.Hand);
        _stNllb.PointerPressed += (_, _) => OpenLocalModel();
        left.Children.Add(_stNllb);
        left.Children.Add(_stNotice);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_stRight);
        right.Children.Add(profiles);
        right.Children.Add(history);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        _statusBar.Child = grid;
    }

    private void RefreshDevices()
    {
        _loadingDevices = true;
        var items = new List<Choice>();
        foreach (var d in DeviceDiscovery.VideoDevices()) items.Add(new Choice(d.Id, d.Name));
        items.Add(new Choice(TestPatternSource.DeviceId, TestPatternSource.DeviceName));
        _cbVideo.ItemsSource = items;
        _cbVideo.SelectedItem = items.FirstOrDefault(i => i.Id == _s.Settings.VideoDeviceId);
        ReloadProfileCombo();
        _loadingDevices = false;
    }

    public void ReloadProfileCombo()
    {
        var was = _loadingDevices;
        _loadingDevices = true;
        var items = _s.ProfileList.Select(p => new Choice(p.Id, p.DisplayName)).ToList();
        _cbProfile.ItemsSource = items;
        _cbProfile.SelectedItem = items.FirstOrDefault(i => i.Id == _s.Current.Id);
        _loadingDevices = was;
    }

    // ---------- Video ----------

    private void OnVideoReady(nint hwnd)
    {
        if (_s.Renderer is not null) return;
        try
        {
            _s.AttachRenderer(hwnd);
        }
        catch (Exception ex)
        {
            _stNotice.Text = "Không khởi tạo được card màn hình: " + ex.Message;
            return;
        }
        RefreshDevices();
        // Tự mở nguồn đã dùng lần trước; nếu chưa có thì chọn capture card đầu tiên.
        if (string.IsNullOrEmpty(_s.Settings.VideoDeviceId) || _cbVideo.SelectedItem is null)
        {
            var first = (_cbVideo.ItemsSource as List<Choice>)?.FirstOrDefault();
            if (first is not null) _cbVideo.SelectedItem = first; // kích hoạt StartVideo qua SelectionChanged
        }
        else _s.StartVideo();
    }

    private void OnSurfaceMouse(string kind, int x, int y)
    {
        if (kind == "dblclick") { ToggleFullscreen(); return; }
        var r = _s.Renderer;
        if (r is null || _s.Current.Subtitle.Position != SubtitlePosition.Free) return;
        var (vx, vy, vw, vh) = r.VideoRect;
        if (vw <= 0 || vh <= 0) return;
        if (kind == "down") _dragging = true;
        if (kind is "down" or "move" && _dragging)
        {
            _s.Current.Subtitle.FreeX = Math.Clamp((x - vx) / vw, 0, 1);
            _s.Current.Subtitle.FreeY = Math.Clamp((y - vy) / vh, 0, 1);
            _s.RefreshOverlay();
        }
        else if (kind == "up" && _dragging)
        {
            _dragging = false;
            _s.Profiles.Save(_s.Current);
        }
    }

    // ---------- Cửa sổ phụ ----------

    private void OpenRegionSelector()
    {
        var snap = _s.Renderer?.Snapshot();
        if (snap is null) { _stNotice.Text = "Chưa có hình để chọn vùng. Hãy chọn nguồn hình trước."; return; }
        var w = new RegionSelectorWindow(_s, snap.Value);
        _s.SelectingRegion = true;
        _s.RefreshOverlay();
        w.Closed += (_, _) => { _s.SelectingRegion = false; _s.RefreshOverlay(); };
        w.Show(this);
    }

    private void OpenSettings()
    {
        if (_settings is { IsVisible: true }) { _settings.Activate(); return; }
        _settings = new SettingsWindow(_s, ReloadProfileCombo);
        _settings.Show(this);
    }

    private Window? _subtitleQuick, _localModel;

    private void OpenLocalModel()
    {
        if (_localModel is { IsVisible: true }) { _localModel.Activate(); return; }
        _localModel = new LocalModelWindow(_s);
        _localModel.Show(this);
    }

    private void OpenSubtitleQuick()
    {
        if (_subtitleQuick is { IsVisible: true }) { _subtitleQuick.Activate(); return; }
        _subtitleQuick = new SubtitleQuickWindow(_s);
        _subtitleQuick.Show(this);
    }

    private void OpenHistory()
    {
        if (_history is { IsVisible: true }) { _history.Activate(); return; }
        _history = new HistoryWindow(_s);
        _history.Show(this);
    }

    private void OpenProfiles()
    {
        if (_profiles is { IsVisible: true }) { _profiles.Activate(); return; }
        _profiles = new ProfilesWindow(_s, ReloadProfileCombo, OpenRegionSelector);
        _profiles.Show(this);
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        _toolbar.IsVisible = _statusBar.IsVisible = !_fullscreen;
        WindowState = _fullscreen ? WindowState.FullScreen : WindowState.Normal;
        Dispatcher.UIThread.Post(() => _s.Renderer?.Resize(), DispatcherPriority.Background);
    }

    // ---------- Phím tắt ----------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool handled = true;
        if (ctrl && !shift && e.Key == Key.T) _tbTranslate.IsChecked = !(_tbTranslate.IsChecked == true);
        else if (ctrl && !shift && e.Key == Key.R) OpenRegionSelector();
        else if (ctrl && shift && e.Key == Key.R) _s.Retranslate();
        else if (ctrl && e.Key == Key.O) _s.SetShowSource(!_s.ShowSource);
        else if (e.Key == Key.F11 || (alt && e.Key == Key.Enter)) ToggleFullscreen();
        else if (e.Key == Key.Escape && _fullscreen) ToggleFullscreen();
        else if (ctrl && !shift && e.Key == Key.H) OpenHistory();
        else if (ctrl && !shift && e.Key == Key.L) OpenSubtitleQuick();
        else if (ctrl && shift && e.Key == Key.P) OpenProfiles();
        else if (ctrl && shift && e.Key == Key.M) OpenLocalModel();
        else if (ctrl && e.Key == Key.OemComma) OpenSettings();
        else handled = false;
        e.Handled = handled;
    }

    // ---------- Trạng thái ----------

    private void OnTick()
    {
        var st = _s.Snapshot();
        // Chấm màu + câu trạng thái giống thanh trạng thái của bản thiết kế.
        if (st.Message is not null) { _dot.Fill = Brushes.Orange; _stState.Text = st.Message; }
        else if (!_s.TranslateEnabled) { _dot.Fill = Brushes.Gray; _stState.Text = "Dịch đang tắt"; }
        else if (st.LastEngine.StartsWith("claude")) { _dot.Fill = Brushes.LimeGreen; _stState.Text = "Đã dịch bằng Claude"; }
        else if (st.LastEngine.StartsWith("gemini")) { _dot.Fill = Brushes.LimeGreen; _stState.Text = "Đã dịch bằng Gemini"; }
        else if (st.LastEngine == "original") { _dot.Fill = Brushes.Orange; _stState.Text = "Hiện chữ gốc (chưa dịch được)"; }
        else { _dot.Fill = Brushes.LimeGreen; _stState.Text = "Đang chờ hộp thoại"; }
        _stModel.Text = st.Model;
        _stQuota.Text = $"Còn {st.Quota} request hôm nay";
        _stNllb.Text = st.Nllb;
        _stRight.Text = $"OCR {st.Ocr}    Dịch {st.Translate}    CPU {st.Cpu:0}%    {ShortGpu(st.Gpu)}";

        if (_tick % 10 == 0) AppLog.Write($"fps={st.Fps:0.0} dinh_dang={st.Format} gpu={st.Gpu} tre_trong_app={st.LatencyMs:0.0}ms cpu={st.Cpu:0}% ocr={st.Ocr} dich={st.Translate} msg={st.Message}");
        // Rút/cắm lại capture card: luồng capture đã dừng thì thử mở lại mỗi ~3 giây.
        if (_tick % 6 == 5 && !string.IsNullOrEmpty(_s.Settings.VideoDeviceId) && _s.Settings.VideoDeviceId != TestPatternSource.DeviceId && !_s.Capture.IsRunning)
            _s.StartVideo();
        _s.RefreshOverlay(); // cập nhật thẻ góc (FPS, độ trễ) khi không có khung mới
        if (++_tick % 4 == 0)
        {
            // Chế độ Tự động: GPU theo màn hình chứa cửa sổ.
            double scale = RenderScaling;
            var center = (Position.X + (int)(Bounds.Width * scale / 2), Position.Y + (int)(Bounds.Height * scale / 2));
            try { _s.FollowWindowGpu(center); } catch (Exception ex) { _stNotice.Text = ex.Message; }
        }
    }

    private static string ShortGpu(string gpu) =>
        gpu.Replace("Intel(R) ", "").Replace("NVIDIA GeForce ", "").Replace("AMD Radeon ", "").Replace("(TM)", "").Replace(" Graphics", "").Trim();
}
