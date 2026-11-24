using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GameTranslator.Core;
using GameTranslator.Platform.Windows;
using GameTranslator.Platform.Windows.Capture;
using GameTranslator.Platform.Windows.Gpu;
using GameTranslator.Platform.Windows.Render;

namespace GameTranslator.App;

public sealed class SettingsWindow : Window
{
    private readonly Session _s;
    private readonly Action _profilesChanged;

    public SettingsWindow(Session s, Action profilesChanged)
    {
        _s = s;
        _profilesChanged = profilesChanged;
        Title = "Cài đặt";
        Width = 760; Height = 700; MinWidth = 620; MinHeight = 480;
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Chung", FontSize = 15, Content = Ui.Scroll(GeneralTab()) });
        tabs.Items.Add(new TabItem { Header = "Dịch", FontSize = 15, Content = Ui.Scroll(TranslateTab()) });
        tabs.Items.Add(new TabItem { Header = "OCR", FontSize = 15, Content = Ui.Scroll(OcrTab()) });
        tabs.Items.Add(new TabItem { Header = "Phụ đề", FontSize = 15, Content = Ui.Scroll(SubtitleTab()) });
        tabs.Items.Add(new TabItem { Header = "Phím tắt", FontSize = 15, Content = Ui.Scroll(HotkeysTab()) });
        Content = tabs;
    }

    private void SaveApp() { _s.Settings.Save(); _s.ApplyRenderSettings(); _s.RefreshOverlay(); }
    private void SaveProfile() => _s.SaveCurrentProfile();

    // ---------- Chung ----------

    private Control GeneralTab()
    {
        var st = _s.Settings;
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Ui.Label($"Hồ sơ đang dùng: {_s.Current.DisplayName} (các tab Dịch, OCR, Phụ đề áp dụng cho hồ sơ này)", 12, opacity: 0.7));

        // Nguồn hình
        var videos = DeviceDiscovery.VideoDevices().Select(d => new Choice(d.Id, d.Name)).ToList();
        videos.Add(new Choice(TestPatternSource.DeviceId, TestPatternSource.DeviceName));
        var formatBox = new ComboBox { Width = 280, MinHeight = 32 };
        void LoadFormats()
        {
            var items = new List<Choice> { new("auto", "Tự động (1080p, ≥ 60 fps, thô trước)") };
            if (st.VideoDeviceId is { } id && id != TestPatternSource.DeviceId)
                foreach (var f in MfCaptureSource.ListFormats(new CaptureDevice(id, st.VideoDeviceName ?? "")))
                    items.Add(new Choice(f.Index.ToString(), $"{f.Label} ({f.SubtypeName})"));
            formatBox.ItemsSource = items;
            formatBox.SelectedItem = items.FirstOrDefault(i => i.Id == (st.FormatIndex?.ToString() ?? "auto")) ?? items[0];
        }
        LoadFormats();
        formatBox.SelectionChanged += (_, _) =>
        {
            if (formatBox.SelectedItem is not Choice c) return;
            var idx = c.Id == "auto" ? (int?)null : int.Parse(c.Id);
            if (idx == st.FormatIndex) return;
            st.FormatIndex = idx; SaveApp(); _s.StartVideo();
        };
        panel.Children.Add(Ui.Row("Capture card", Ui.Combo(videos, st.VideoDeviceId, c =>
        {
            st.VideoDeviceId = c.Id; st.VideoDeviceName = c.Text; st.FormatIndex = null; SaveApp(); LoadFormats(); _s.StartVideo();
        })));
        panel.Children.Add(Ui.Row("Định dạng hình", formatBox,
            "Định dạng thô (không nén) cho độ trễ thấp nhất; MJPEG/H.264 phải giải mã nên trễ hơn."));

        // Tiếng
        var ins = DeviceDiscovery.AudioInputs().Select(d => new Choice(d.Id, d.Name)).Prepend(new Choice("", "Không phát tiếng")).ToList();
        var outs = DeviceDiscovery.AudioOutputs().Select(d => new Choice(d.Id, d.Name)).Prepend(new Choice("", "Thiết bị phát mặc định")).ToList();
        panel.Children.Add(Ui.Row("Nguồn tiếng", Ui.Combo(ins, st.AudioInputId ?? "", c => { st.AudioInputId = c.Id; SaveApp(); _s.StartAudio(); })));
        panel.Children.Add(Ui.Row("Thiết bị phát tiếng", Ui.Combo(outs, st.AudioOutputId ?? "", c => { st.AudioOutputId = c.Id; SaveApp(); _s.StartAudio(); })));
        panel.Children.Add(Ui.Row("Âm lượng", new Slider
        {
            Minimum = 0, Maximum = 1, Value = st.Volume, Width = 280,
        }.With(sl => sl.ValueChanged += (_, _) => { st.Volume = sl.Value; _s.Audio.Volume = (float)sl.Value; })));
        panel.Children.Add(Ui.Row("Bù trễ tiếng (ms)", Ui.Number(st.AudioDelayMs, 0, 200, 5, v => { st.AudioDelayMs = (int)v; SaveApp(); _s.StartAudio(); }, "0"),
            "Thêm độ trễ cho tiếng nếu tiếng đến sớm hơn hình."));

        // GPU
        panel.Children.Add(Ui.Label("Card màn hình và hiển thị", 15, Avalonia.Media.FontWeight.SemiBold));
        var gpuItems = new List<Choice>
        {
            new("Auto", "Tự động (khuyên dùng) — theo màn hình đang chứa cửa sổ"),
            new("HighPerformance", "Hiệu năng cao"),
            new("PowerSaving", "Tiết kiệm điện"),
        };
        gpuItems.AddRange(_s.Gpus.Select(g => new Choice("gpu:" + g.StableKey, g.DisplayText)));
        var gpuNote = Ui.Hint("");
        void UpdateGpuNote()
        {
            var pick = _s.Renderer?.Gpu;
            if (pick is null) { gpuNote.Text = ""; return; }
            var anyOutputs = _s.Gpus.Any(g => g.Outputs.Count > 0);
            gpuNote.Text = $"Đang dùng: {pick.DisplayText}." + (pick.Outputs.Count == 0 && anyOutputs
                ? " GPU này không nối màn hình đang dùng: hình được xử lý trên GPU này rồi chép sang GPU tích hợp để hiện lên màn hình, có thể thêm khoảng 1 khung trễ. Muốn trễ thấp nhất hãy chọn Tự động, hoặc cắm màn hình vào cổng của GPU này."
                : "");
        }
        var selectedGpu = st.Gpu == GpuMode.Specific ? "gpu:" + st.GpuKey : st.Gpu.ToString();
        var gpuCombo = Ui.Combo(gpuItems, selectedGpu, c =>
        {
            if (c.Id.StartsWith("gpu:")) { st.Gpu = GpuMode.Specific; st.GpuKey = c.Id[4..]; }
            else st.Gpu = Enum.Parse<GpuMode>(c.Id);
            SaveApp();
            try { _s.FollowWindowGpu(null); } catch (Exception ex) { gpuNote.Text = ex.Message; return; }
            UpdateGpuNote();
        }, 520);
        panel.Children.Add(Ui.Row("Card màn hình (GPU)", Ui.Stack(gpuCombo, gpuNote)));
        UpdateGpuNote();
        var exe = Environment.ProcessPath ?? "";
        var prefNote = Ui.Hint("");
        void ShowPref() => prefNote.Text = "Ưu tiên GPU của Windows cho app này: " + (GpuPreferenceRegistry.Get(exe) switch { 2 => "Hiệu năng cao", 1 => "Tiết kiệm điện", _ => "do Windows quyết định" }) + ". Cần mở lại app để áp dụng cho phần giao diện.";
        ShowPref();
        panel.Children.Add(Ui.Row("Ưu tiên GPU của Windows", Ui.Stack(Ui.Horizontal(8,
            Ui.Button("Hiệu năng cao", () => { GpuPreferenceRegistry.Set(exe, 2); ShowPref(); }),
            Ui.Button("Tiết kiệm điện", () => { GpuPreferenceRegistry.Set(exe, 1); ShowPref(); }),
            Ui.Button("Mặc định", () => { GpuPreferenceRegistry.Set(exe, 0); ShowPref(); })), prefNote)));

        panel.Children.Add(Ui.Row("Hiển thị hình", Ui.Combo(
            [new("Smooth4K", "Mịn 4K (AMD FSR 1 + khử răng cưa)"), new("Default", "Mặc định (song tuyến, nhẹ nhất)"), new("Pixel", "Điểm ảnh (nét, hệ số nguyên)")],
            st.Scaling.ToString(), c => { st.Scaling = Enum.Parse<ScalingMode>(c.Id); SaveApp(); }, 360),
            "Mịn 4K: phóng ảnh theo hướng cạnh (AMD FSR 1) nên cạnh xéo không bị bậc thang khi phóng 1080p lên 1440p/4K."));
        panel.Children.Add(Ui.Row("Khử răng cưa", Ui.Check("Làm mượt cạnh răng cưa có sẵn trong hình game (FXAA)", st.AntiAlias, v => { st.AntiAlias = v; SaveApp(); }),
            "Chỉ dùng ở chế độ Mịn 4K. Chữ rất mảnh trong game có thể mềm đi một chút; phụ đề không bị ảnh hưởng."));
        var sharpText = Ui.Label("", 13);
        void ShowSharp() => sharpText.Text = st.SharpenStops <= 0.05 ? "rất nét" : st.SharpenStops <= 0.35 ? "nét (khuyên dùng)" : st.SharpenStops <= 1 ? "vừa" : "mềm";
        ShowSharp();
        var sharp = new Slider { Minimum = 0, Maximum = 2, Value = 2 - st.SharpenStops, Width = 220 };
        sharp.ValueChanged += (_, _) => { st.SharpenStops = Math.Round(2 - sharp.Value, 2); ShowSharp(); _s.ApplyRenderSettings(); _s.RefreshOverlay(); };
        sharp.PointerReleased += (_, _) => st.Save();
        panel.Children.Add(Ui.Row("Độ nét", Ui.Horizontal(10, sharp, sharpText), "Làm nét sau khi phóng (AMD RCAS), chỉ ở chế độ Mịn 4K."));
        panel.Children.Add(Ui.Row("Đồng bộ dọc", Ui.Check("Bật (không xé hình)", st.VSync, v => { st.VSync = v; SaveApp(); }),
            "Tắt thì trễ thấp nhất nhưng có thể thấy dải xé ngang khi hình chuyển động; hợp với màn hình G-Sync/FreeSync."));
        panel.Children.Add(Ui.Row("Giải mã bằng GPU", Ui.Check("Dùng card màn hình giải mã MJPEG/H.264", st.HardwareDecode, v => { st.HardwareDecode = v; SaveApp(); _s.StartVideo(); }),
            "Chỉ áp dụng khi định dạng hình là MJPEG/H.264. Khung giải mã nằm sẵn trên GPU, gần như không tốn CPU. Định dạng thô thì GPU đổi màu bằng shader."));
        panel.Children.Add(Ui.Row("Khung vùng OCR", Ui.Check("Luôn hiện khung vùng OCR trên video", st.ShowRegionFrame, v => { st.ShowRegionFrame = v; SaveApp(); })));
        return panel;
    }

    // ---------- Dịch ----------

    private Control TranslateTab()
    {
        var t = _s.Current.Translate;
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Ui.Row("Chế độ dịch", Ui.Combo(
            [new("OnlineOnly", TranslateMode.OnlineOnly.Title()), new("OnlineWithOfflineFallback", TranslateMode.OnlineWithOfflineFallback.Title()), new("OfflineOnly", TranslateMode.OfflineOnly.Title())],
            t.Mode.ToString(), c => { t.Mode = Enum.Parse<TranslateMode>(c.Id); SaveProfile(); }, 320),
            "Online + dự phòng: Claude/Gemini trước, lỗi hoặc hết hạn mức thì dùng NLLB. Chỉ offline: chỉ NLLB, không tốn tiền API (cần cài ở mục Dịch offline bên dưới)."));

        var provider = Ui.Combo([new("Claude", TranslateProvider.Claude.Title()), new("Gemini", TranslateProvider.Gemini.Title())],
            t.Provider.ToString(), _ => { }, 320);
        panel.Children.Add(Ui.Row("Nhà cung cấp", provider));

        var keyBox = new TextBox { PasswordChar = '•', Width = 380, Watermark = t.Provider.KeyPlaceholder() };
        var keyStatus = Ui.Hint("");
        var model = new AutoCompleteBox { Width = 380, MinimumPrefixLength = 0, Text = t.ActiveModel };
        var testResult = Ui.Hint("");

        void Load()
        {
            var acct = t.Provider == TranslateProvider.Claude ? "claude" : "gemini";
            keyBox.Text = "";
            keyBox.Watermark = t.Provider.KeyPlaceholder();
            keyStatus.Text = CredentialStore.Read(acct) is null ? "Chưa lưu API key." : "Đã lưu API key trong Windows Credential Manager (nhập khóa mới để thay).";
            model.ItemsSource = t.Provider.Presets().Select(p => p.Id).ToList();
            model.Text = t.ActiveModel;
        }
        provider.SelectionChanged += (_, _) =>
        {
            if (provider.SelectedItem is not Choice c) return;
            t.Provider = Enum.Parse<TranslateProvider>(c.Id);
            SaveProfile(); Load();
        };
        keyBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(keyBox.Text)) return;
            var acct = t.Provider == TranslateProvider.Claude ? "claude" : "gemini";
            CredentialStore.Write(acct, keyBox.Text.Trim());
            keyBox.Text = "";
            Load();
            _s.ApplyProfile();
        };
        model.TextChanged += (_, _) =>
        {
            var m = model.Text?.Trim();
            if (string.IsNullOrEmpty(m) || m == t.ActiveModel) return;
            if (t.Provider == TranslateProvider.Claude) t.Model = m; else t.GeminiModel = m;
            SaveProfile();
        };
        panel.Children.Add(Ui.Row("API key", Ui.Stack(keyBox, keyStatus)));
        panel.Children.Add(Ui.Row("Model", Ui.Stack(model, Ui.Hint("Chọn từ gợi ý hoặc nhập tên model khác."))));
        panel.Children.Add(Ui.Row("", Ui.Stack(Ui.Button("Thử API key", async () =>
        {
            testResult.Text = "Đang thử...";
            testResult.Text = await _s.TestKeyAsync(t.Provider, t.ActiveModel);
        }), testResult)));
        Load();

        panel.Children.Add(Ui.Row("Số câu ngữ cảnh", Ui.Number(t.ContextLines, 0, 6, 1, v => { t.ContextLines = (int)v; SaveProfile(); }, "0"),
            "Gửi kèm các câu trước để dịch đúng giọng và đại từ."));
        panel.Children.Add(Ui.Row("Tối đa mỗi phút", Ui.Number(t.RequestsPerMinute, 1, 120, 1, v => { t.RequestsPerMinute = (int)v; SaveProfile(); }, "0")));
        panel.Children.Add(Ui.Row("Tối đa mỗi ngày", Ui.Number(t.RequestsPerDay, 1, 20000, 10, v => { t.RequestsPerDay = (int)v; SaveProfile(); }, "0"),
            "Bộ đếm ngày đặt lại lúc nửa đêm giờ Thái Bình Dương (giống bản macOS)."));
        panel.Children.Add(OfflineSection());
        return panel;
    }

    private Control OfflineSection()
    {
        var l = _s.Settings.Local;
        var p = new StackPanel { Spacing = 4, Margin = new Thickness(0, 14, 0, 0) };
        void Save() { _s.Settings.Save(); _s.ApplyProfile(); }
        p.Children.Add(Ui.Label("Dịch offline (model local)", 15, Avalonia.Media.FontWeight.SemiBold));
        p.Children.Add(Ui.Row("Dùng NLLB", Ui.Check("Dùng NLLB làm bộ dịch offline", l.Enabled, v => { l.Enabled = v; Save(); })));

        var url = new TextBox { Width = 380, Text = l.ExternalUrl, Watermark = "http://192.168.1.20:47811" };
        var token = new TextBox { Width = 380, PasswordChar = '•', Watermark = CredentialStore.Read("nllb-external") is null ? "Token của host" : "Đã lưu token (nhập mới để thay)" };
        var urlRow = Ui.Row("Địa chỉ host", url);
        var tokenRow = Ui.Row("Token", token);
        void ShowExternal() => urlRow.IsVisible = tokenRow.IsVisible = l.Host == LocalHostKind.External;
        p.Children.Add(Ui.Row("Host", Ui.Combo([new("Managed", "Tự chạy trên máy này"), new("External", "Host khác (máy khác trong mạng)")],
            l.Host.ToString(), c => { l.Host = Enum.Parse<LocalHostKind>(c.Id); ShowExternal(); Save(); }, 320)));
        url.LostFocus += (_, _) => { l.ExternalUrl = url.Text?.Trim() ?? ""; Save(); };
        token.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(token.Text)) return;
            CredentialStore.Write("nllb-external", token.Text.Trim());
            token.Text = ""; token.Watermark = "Đã lưu token (nhập mới để thay)";
        };
        p.Children.Add(urlRow);
        p.Children.Add(tokenRow);
        ShowExternal();

        p.Children.Add(Ui.Row("Số beam", Ui.Number(l.Beams, 1, 8, 1, v => { l.Beams = (int)v; _s.Settings.Save(); }, "0"),
            "1 nhanh nhất; 4 dịch tốt hơn nhưng chậm hơn."));
        var status = Ui.Hint("");
        void ShowStatus() => status.Text = l.Host == LocalHostKind.External ? "" : _s.LocalHost.Status.Text + (_s.LocalHost.Status.Device is { } d ? " · " + d : "");
        ShowStatus();
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => ShowStatus();
        timer.Start();
        Closed += (_, _) => timer.Stop();
        var result = Ui.Hint("");
        p.Children.Add(Ui.Row("Trạng thái", Ui.Stack(status, Ui.Horizontal(8,
            Ui.Button("Thử kết nối", async () =>
            {
                result.Text = "Đang thử...";
                try
                {
                    var tr = _s.OfflineTranslator();
                    if (tr is null) { result.Text = "Dịch offline đang tắt."; return; }
                    var h = await tr.HealthAsync();
                    result.Text = h.Ready ? $"Sẵn sàng: {h.Model} · {h.Device}" : $"Host phản hồi nhưng chưa sẵn sàng ({h.State}{(h.Error is null ? "" : ": " + h.Error)}).";
                }
                catch (TranslationException ex) { result.Text = "Không kết nối được: " + ex.Message; }
            }),
            Ui.Button("Quản lý model local... (Ctrl+Shift+M)", () => new LocalModelWindow(_s).Show(this))), result)));
        p.Children.Add(Ui.Hint("NLLB không dùng ngữ cảnh câu trước và từ điển game. Bản dịch offline hiện nhãn \"offline (NLLB)\" và được nâng cấp lên bản online khi còn hạn mức."));
        return p;
    }

    // ---------- OCR ----------

    private Control OcrTab()
    {
        var o = _s.Current.Ocr;
        var d = _s.Current.Detector;
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Ui.Row("Engine OCR", Ui.Label("Windows OCR (có sẵn trong Windows)"), "Cần gói ngôn ngữ tiếng Anh (Settings > Time & language > Language & region)."));
        panel.Children.Add(Ui.Label("Tiền xử lý ảnh vùng", 15, Avalonia.Media.FontWeight.SemiBold));
        panel.Children.Add(Ui.Row("Phóng to", Ui.Number(o.Scale, 1, 4, 0.5, v => { o.Scale = v; SaveProfile(); }), "Phóng to ảnh vùng trước khi đọc giúp OCR đọc chữ nhỏ tốt hơn."));
        panel.Children.Add(Ui.Row("Tương phản", Ui.Number(o.Contrast, 0.5, 3, 0.1, v => { o.Contrast = v; SaveProfile(); })));
        panel.Children.Add(Ui.Row("Đảo màu", Ui.Check("Đảo màu (chữ tối trên nền sáng thành ngược lại)", o.Invert, v => { o.Invert = v; SaveProfile(); })));
        panel.Children.Add(Ui.Label("Phát hiện chữ mới", 15, Avalonia.Media.FontWeight.SemiBold));
        panel.Children.Add(Ui.Row("Lấy mẫu mỗi N khung", Ui.Number(d.SampleEveryNFrames, 1, 120, 1, v => { d.SampleEveryNFrames = (int)v; SaveProfile(); }, "0")));
        panel.Children.Add(Ui.Row("Ngưỡng thay đổi", Ui.Number(d.ChangeThreshold, 0.5, 30, 0.5, v => { d.ChangeThreshold = v; SaveProfile(); })));
        panel.Children.Add(Ui.Row("Chờ ổn định (ms)", Ui.Number(d.StableMs, 100, 3000, 50, v => { d.StableMs = v; SaveProfile(); }, "0")));
        panel.Children.Add(Ui.Row("Ẩn sau khi trống (ms)", Ui.Number(d.HideDelayMs, 200, 5000, 100, v => { d.HideDelayMs = v; SaveProfile(); }, "0")));
        return panel;
    }

    // ---------- Phụ đề ----------

    private Control SubtitleTab() => SubtitleControls.Build(_s);

    private static Control HotkeysTab()
    {
        var rows = new (string, string)[]
        {
            ("Ctrl+T", "Bật/tắt dịch"), ("Ctrl+R", "Chọn vùng hộp thoại"), ("Ctrl+Shift+R", "Dịch lại (bỏ qua cache)"),
            ("Ctrl+O", "Hiện/ẩn chữ gốc"), ("F11 hoặc Alt+Enter", "Toàn màn hình (Esc để thoát)"),
            ("Ctrl+Shift+M", "Model dịch local (NLLB)"), ("Ctrl+H", "Lịch sử bản dịch"), ("Ctrl+L", "Chỉnh nhanh phụ đề"), ("Ctrl+Shift+P", "Hồ sơ game"), ("Ctrl+,", "Cài đặt"),
        };
        var p = new StackPanel { Spacing = 6 };
        foreach (var (k, d) in rows)
            p.Children.Add(Ui.Row(k, Ui.Label(d)));
        return p;
    }
}

internal static class ControlExt
{
    public static T With<T>(this T c, Action<T> a) { a(c); return c; }
}
