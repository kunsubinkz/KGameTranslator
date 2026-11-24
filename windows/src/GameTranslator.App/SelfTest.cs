using System.Runtime.InteropServices;
using System.Text;
using GameTranslator.Core;
using GameTranslator.Core.Pipeline;
using GameTranslator.Platform.Windows;
using GameTranslator.Platform.Windows.Capture;
using GameTranslator.Platform.Windows.Gpu;
using GameTranslator.Platform.Windows.Ocr;
using GameTranslator.Platform.Windows.Render;

namespace GameTranslator.App;

/// <summary>`GameTranslator.exe --selftest`: kiểm tra GPU, vẽ thử 3 chế độ phóng và đọc lại hướng ảnh, OCR, thiết bị.</summary>
public static class SelfTest
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    private static readonly StringBuilder Log = new();
    private static int _failures;

    private static void Line(string s)
    {
        Log.AppendLine(s);
        try { Console.WriteLine(s); } catch (Exception) { }
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Line($"[{(ok ? "OK" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    public static int Run()
    {
        AttachConsole(-1);
        Line($"Game Translator selftest · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {Environment.OSVersion} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        try
        {
            var gpus = GpuCatalog.Enumerate();
            Check("Liệt kê GPU", gpus.Count > 0, $"{gpus.Count} GPU {GpuCatalog.LastError}");
            foreach (var g in gpus) Line("    " + g.DisplayText + $" · tearing={g.SupportsTearing}");
            foreach (var g in gpus) RenderTest(g);

            Check("Chọn GPU tự động", GpuCatalog.Select(gpus, GpuMode.Auto, null, null) is not null);
            OcrTest().GetAwaiter().GetResult();

            var video = DeviceDiscovery.VideoDevices();
            Line($"    Thiết bị hình: {video.Count}");
            foreach (var v in video)
            {
                Line("      · " + v.Name);
                foreach (var f in MfCaptureSource.ListFormats(v).Take(40)) Line("          " + f.Label);
                var best = MediaTypeSelector.Choose(MfCaptureSource.ListFormats(v));
                if (best is not null) Line("          → chọn: " + best.Label);
            }
            Line($"    Tiếng vào: {DeviceDiscovery.AudioInputs().Count}, tiếng ra: {DeviceDiscovery.AudioOutputs().Count}");
            Check("Credential Manager (ghi/đọc/xóa)", CredentialRoundTrip());
        }
        catch (Exception ex)
        {
            _failures++;
            Line("[FAIL] Ngoại lệ: " + ex);
        }
        Line(_failures == 0 ? "KẾT QUẢ: tất cả đạt." : $"KẾT QUẢ: {_failures} lỗi.");
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameTranslator", "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "selftest.txt"), Log.ToString());
        }
        catch (Exception) { }
        return _failures == 0 ? 0 : 1;
    }

    private static bool CredentialRoundTrip()
    {
        const string account = "selftest";
        CredentialStore.Write(account, "giá-trị-thử");
        var ok = CredentialStore.Read(account) == "giá-trị-thử";
        CredentialStore.Delete(account);
        return ok && CredentialStore.Read(account) is null;
    }

    /// <summary>Bốn góc: trên-trái đỏ, trên-phải xanh dương, dưới-trái xanh lá, dưới-phải trắng (BGRA).</summary>
    private static byte[] Quadrants(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool right = x >= w / 2, bottom = y >= h / 2;
                (byte b, byte g, byte r) = (right, bottom) switch
                {
                    (false, false) => ((byte)0, (byte)0, (byte)255),
                    (true, false) => ((byte)255, (byte)0, (byte)0),
                    (false, true) => ((byte)0, (byte)255, (byte)0),
                    _ => ((byte)255, (byte)255, (byte)255),
                };
                int o = (y * w + x) * 4;
                px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
            }
        return px;
    }

    private static void RenderTest(GpuInfo gpu)
    {
        using var win = Win32Window.Create(0, 640, 360);
        VideoRenderer? renderer = null;
        try
        {
            renderer = new VideoRenderer(win.Handle, gpu);
            renderer.SynchronousRender = true;
            Check($"Tạo device trên {gpu.Name}", true);
            const int fw = 320, fh = 180;
            var frame = Quadrants(fw, fh);
            foreach (var mode in Enum.GetValues<ScalingMode>())
            {
                renderer.Mode = mode;
                renderer.CaptureRender = true;
                renderer.OnFrame(FrameFormat.Bgra, frame, fw, fh, fw * 4);
                var shot = renderer.LastRendered;
                if (shot is null) { Check($"Vẽ chế độ {mode}", false, "không đọc lại được ảnh"); continue; }
                var (bgra, w, h) = shot.Value;
                (byte B, byte G, byte R) At(double fx, double fy)
                {
                    int o = ((int)(h * fy) * w + (int)(w * fx)) * 4;
                    return (bgra[o], bgra[o + 1], bgra[o + 2]);
                }
                var tl = At(0.25, 0.25); var tr = At(0.75, 0.25); var bl = At(0.25, 0.75); var br = At(0.75, 0.75);
                bool ok = tl.R > 200 && tl.G < 60 && tl.B < 60
                       && tr.B > 200 && tr.R < 60 && tr.G < 60
                       && bl.G > 200 && bl.R < 60 && bl.B < 60
                       && br.R > 200 && br.G > 200 && br.B > 200;
                Check($"Vẽ chế độ {mode} + hướng ảnh ({gpu.Vendor})", ok, $"TL={tl} TR={tr} BL={bl} BR={br}");
            }
            renderer.SetOverlay(new OverlayState { SubtitleText = "Xin chào, lữ khách!", Badge = "selftest" });
            renderer.CaptureRender = true;
            renderer.OnFrame(FrameFormat.Bgra, frame, fw, fh, fw * 4);
            Check("Vẽ phụ đề Direct2D/DirectWrite", renderer.LastRendered is not null);
        }
        catch (Exception ex)
        {
            Check($"Vẽ trên {gpu.Name}", false, ex.Message);
        }
        finally { renderer?.Dispose(); }
    }

    private static async Task OcrTest()
    {
        Check("Gói OCR tiếng Anh", WindowsOcr.IsEnglishAvailable);
        if (!WindowsOcr.IsEnglishAvailable) return;
        const string text = "Welcome to Hyrule, traveler.";
        var frame = TestPatternSource.RenderDialogFrame(text);
        var profile = Profile.Default;
        var crops = new List<BgraImage>();
        var r = profile.Region!;
        int x = (int)(r.X * 1280), y = (int)(r.Y * 720), w = (int)(r.Width * 1280), h = (int)(r.Height * 720);
        var img = BgraImage.Create(w, h);
        for (int row = 0; row < h; row++) Buffer.BlockCopy(frame, ((y + row) * 1280 + x) * 4, img.Pixels, row * w * 4, w * 4);
        var ocr = new WindowsOcr();
        var res = await ocr.RecognizeAsync(Preprocessor.Prepare(img, profile.Ocr), profile.Ocr, []);
        var normalized = TextNormalizer.Normalize(res.Text) ?? "";
        double cer = TextNormalizer.CharacterErrorRate(text, normalized);
        Check("OCR hộp thoại mẫu", cer < 0.1, $"\"{normalized}\" · CER {cer:P0} · {res.ElapsedMs:0} ms");
    }
}

/// <summary>`--probe-capture`: mở từng định dạng 1080p/720p của capture card vài giây và đo FPS thực.</summary>
public static class CaptureProbe
{
    private sealed class Counter : IFrameSink
    {
        public int Frames; public int W, H; public long FirstTicks, LastTicks; public byte Sample;
        public void OnFrame(FrameFormat format, ReadOnlySpan<byte> bgra, int width, int height, int stride)
        {
            if (Frames == 0) FirstTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            Frames++; W = width; H = height; LastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            long sum = 0; for (int i = 0; i < bgra.Length; i += 4099) sum += bgra[i]; Sample = (byte)(sum / Math.Max(1, bgra.Length / 4099));
        }
    }

    public static int Run()
    {
        SelfTestConsole.Attach();
        foreach (var dev in DeviceDiscovery.VideoDevices())
        {
            SelfTestConsole.Line("Thiết bị: " + dev.Name);
            var formats = MfCaptureSource.ListFormats(dev);
            foreach (var f in formats.Where(f => f.Height is 1080 or 720 && f.Fps >= 29))
            {
                var counter = new Counter();
                using var src = new MfCaptureSource();
                string status = "";
                src.StatusChanged += m => { if (!string.IsNullOrEmpty(m)) status = m; };
                src.Start(dev, counter, f.Index);
                Thread.Sleep(3500);
                src.Stop();
                double secs = counter.Frames > 1 ? System.Diagnostics.Stopwatch.GetElapsedTime(counter.FirstTicks, counter.LastTicks).TotalSeconds : 0;
                SelfTestConsole.Line($"  #{f.Index,-3} {f.Label,-34} ({f.SubtypeName}) → {counter.Frames} khung, {(secs > 0 ? counter.Frames / secs : 0):0.0} fps, {counter.W}x{counter.H}, độ sáng TB {counter.Sample} {status}");
            }
        }
        return 0;
    }
}

internal static class SelfTestConsole
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    public static void Attach() => AttachConsole(-1);
    public static void Line(string s) { try { Console.WriteLine(s); } catch (Exception) { } }
}

/// <summary>`--ocr-image &lt;file&gt; [x y w h]`: OCR một ảnh (png/jpg/webp...) hoặc một vùng pixel của ảnh, in kết quả. Dùng để dò lỗi đọc chữ.</summary>
public static class OcrImageProbe
{
    public static int Run(string[] args)
    {
        SelfTestConsole.Attach();
        var i = Array.IndexOf(args, "--ocr-image");
        var file = args.ElementAtOrDefault(i + 1);
        if (file is null || !File.Exists(file)) { SelfTestConsole.Line("Thiếu file ảnh."); return 2; }
        var (px, w, h) = Decode(file).GetAwaiter().GetResult();
        int x = 0, y = 0, cw = w, ch = h;
        if (args.Length >= i + 6)
        {
            x = int.Parse(args[i + 2]); y = int.Parse(args[i + 3]); cw = int.Parse(args[i + 4]); ch = int.Parse(args[i + 5]);
        }
        var img = BgraImage.Create(cw, ch);
        for (int row = 0; row < ch; row++) Buffer.BlockCopy(px, ((y + row) * w + x) * 4, img.Pixels, row * cw * 4, cw * 4);
        if (Environment.GetEnvironmentVariable("GT_DIM") is { } dimText && double.TryParse(dimText, System.Globalization.CultureInfo.InvariantCulture, out var dim))
            for (int k = 0; k < img.Pixels.Length; k++) if (k % 4 != 3) img.Pixels[k] = (byte)(img.Pixels[k] * dim);
        SelfTestConsole.Line($"Độ lệch chuẩn ảnh thu nhỏ (bộ phát hiện chữ coi là trống nếu < 4): {ChangeDetector.StandardDeviation(Preprocessor.Thumbnail(img)):0.00}");
        var ocr = new WindowsOcr();
        foreach (var (name, s) in new (string, OcrSettings)[]
                 {
                     ("mặc định", new OcrSettings()),
                     ("tự cân sáng + đảo", new OcrSettings { Invert = true }),
                     ("không phóng", new OcrSettings { Scale = 1 }),
                     ("đảo màu", new OcrSettings { Invert = true }),
                 })
        {
            var plain = ocr.RecognizeAsync(Preprocessor.Prepare(img, s, autoLevels: false), s, []).GetAwaiter().GetResult();
            SelfTestConsole.Line($"[{name}, không cân sáng] {TextNormalizer.Normalize(plain.Text) ?? "(không đọc được)"}");
            var res = ocr.RecognizeAsync(Preprocessor.Prepare(img, s), s, []).GetAwaiter().GetResult();
            if (name == "mặc định")
            {
                var paragraphs = TextNormalizer.NormalizeParagraphs(res.Text) ?? "(không đọc được)";
                SelfTestConsole.Line("  đoạn:" + Environment.NewLine + "    " + paragraphs.Replace("\n", Environment.NewLine + "    "));
            }
            SelfTestConsole.Line($"[{name}, tự cân sáng] {res.ElapsedMs:0} ms → {TextNormalizer.Normalize(res.Text) ?? "(không đọc được)"}");
        }
        return 0;
    }

    private static async Task<(byte[], int, int)> Decode(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bmp = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore);
        var bytes = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        return (bytes, bmp.PixelWidth, bmp.PixelHeight);
    }
}
