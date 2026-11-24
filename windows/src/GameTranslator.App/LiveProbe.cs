using System.Diagnostics;
using GameTranslator.Core;
using GameTranslator.Core.Pipeline;
using GameTranslator.Platform.Windows;
using GameTranslator.Platform.Windows.Capture;
using GameTranslator.Platform.Windows.Gpu;
using GameTranslator.Platform.Windows.Ocr;
using GameTranslator.Platform.Windows.Render;

namespace GameTranslator.App;

/// <summary>`--probe-live`: mở capture card thật qua từng đường (thô, MJPEG giải mã GPU, MJPEG giải mã CPU),
/// đo FPS, độ trễ trong app, CPU và OCR vùng của hồ sơ đang dùng. Không gọi API dịch.</summary>
public static class LiveProbe
{
    private sealed class Sink(VideoRenderer r, bool allowGpu) : IGpuFrameSink
    {
        public int CpuFrames, GpuFrames;
        public double LatencySum; public int LatencyN;
        public Vortice.MediaFoundation.IMFDXGIDeviceManager? DeviceManager => allowGpu ? r.DeviceManager : null;

        public void OnFrame(FrameFormat format, ReadOnlySpan<byte> data, int width, int height, int stride)
        {
            CpuFrames++;
            r.OnFrame(format, data, width, height, stride);
            Sample();
        }

        public void OnGpuFrame(Vortice.Direct3D11.ID3D11Texture2D texture, uint subresource, int width, int height)
        {
            GpuFrames++;
            r.OnGpuFrame(texture, subresource, width, height);
            Sample();
        }

        private void Sample()
        {
            var l = r.Stats.InAppLatencyMs;
            if (l > 0) { LatencySum += l; LatencyN++; }
        }
    }

    public static int Run()
    {
        SelfTestConsole.Attach();
        var dev = DeviceDiscovery.VideoDevices().FirstOrDefault();
        if (dev is null) { SelfTestConsole.Line("Không có capture card."); return 1; }
        var formats = MfCaptureSource.ListFormats(dev);
        var raw = MediaTypeSelector.Choose(formats.Where(f => f.IsRaw).ToList());
        var mjpg = MediaTypeSelector.Choose(formats.Where(f => !f.IsRaw).ToList());
        var settings = AppSettings.Load();
        var profile = new ProfileStore(AppPaths.Profiles).Load(settings.ProfileId) ?? Profile.Default;
        SelfTestConsole.Line($"Thiết bị: {dev.Name} · hồ sơ: {profile.DisplayName} · vùng chính {profile.MainRegion}");
        var gpu = GpuCatalog.Select(GpuCatalog.Enumerate(), GpuMode.Auto, null, null);

        var cases = new List<(string Name, NativeFormat? Format, bool Gpu)>
        {
            ("thô (GPU đổi màu)", raw, false),
            ("MJPEG, GPU giải mã", mjpg, true),
            ("MJPEG, CPU giải mã", mjpg, false),
        };
        foreach (var (name, format, allowGpu) in cases)
        {
            if (format is null) continue;
            using var win = Win32Window.Create(0, 1280, 720);
            using var renderer = new VideoRenderer(win.Handle, gpu) { VSync = false };
            var sink = new Sink(renderer, allowGpu);
            using var cap = new MfCaptureSource();
            string path = "", status = "";
            cap.Started += i => path = i.Path;
            cap.StatusChanged += m => { if (!string.IsNullOrEmpty(m)) status = m; };
            var proc = Process.GetCurrentProcess();
            cap.Start(dev, sink, format.Index);
            Thread.Sleep(1500);
            var cpu0 = proc.TotalProcessorTime; var t0 = Stopwatch.GetTimestamp();
            int f0 = sink.CpuFrames + sink.GpuFrames;
            sink.LatencySum = 0; sink.LatencyN = 0;
            Thread.Sleep(5000);
            proc.Refresh();
            double secs = Stopwatch.GetElapsedTime(t0).TotalSeconds;
            double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / secs / Environment.ProcessorCount * 100;
            int frames = sink.CpuFrames + sink.GpuFrames - f0;
            SelfTestConsole.Line($"[{name}] đường={path} · {frames / secs:0.0} fps · khung GPU {sink.GpuFrames}/CPU {sink.CpuFrames} · trễ trong app TB {(sink.LatencyN > 0 ? sink.LatencySum / sink.LatencyN : 0):0.0} ms · CPU {cpu:0.0}% {status}");

            if (renderer.Snapshot() is { } snap)
            {
                var dir = Path.Combine(Path.GetTempPath(), "gt-probe");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, new string(name.Where(char.IsLetterOrDigit).ToArray()) + ".png");
                using var bmp = new System.Drawing.Bitmap(snap.Width, snap.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, snap.Width, snap.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                System.Runtime.InteropServices.Marshal.Copy(snap.Pixels, 0, bd.Scan0, snap.Pixels.Length);
                bmp.UnlockBits(bd);
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                SelfTestConsole.Line("    ảnh: " + file);
            }
            var crops = renderer.CropRegions(profile.AllRegions);
            if (crops is not null)
            {
                var ocr = new WindowsOcr();
                foreach (var c in crops)
                {
                    var res = ocr.RecognizeAsync(Preprocessor.Prepare(c, profile.Ocr), profile.Ocr, []).GetAwaiter().GetResult();
                    var text = TextNormalizer.Normalize(res.Text);
                    SelfTestConsole.Line($"    OCR vùng {c.Width}x{c.Height} ({res.ElapsedMs:0} ms): {text ?? "(không có chữ)"}");
                }
            }
            cap.Stop();
        }
        PipelineRun(dev, mjpg ?? raw, gpu);
        return 0;
    }

    private sealed class PipelineSink(VideoRenderer r, PipelineCoordinator p, IReadOnlyList<Region> regions) : IGpuFrameSink
    {
        private int _n;
        public Vortice.MediaFoundation.IMFDXGIDeviceManager? DeviceManager => r.DeviceManager;
        public void OnFrame(FrameFormat format, ReadOnlySpan<byte> data, int width, int height, int stride) { r.OnFrame(format, data, width, height, stride); Tick(); }
        public void OnGpuFrame(Vortice.Direct3D11.ID3D11Texture2D texture, uint subresource, int width, int height) { r.OnGpuFrame(texture, subresource, width, height); Tick(); }
        private byte[]? _prev;
        private void Tick()
        {
            if (++_n % 15 != 0) return;
            if (r.CropRegions(regions) is not { } crops) return;
            var thumb = crops.SelectMany(c => Preprocessor.Thumbnail(c)).ToArray();
            double sd = ChangeDetector.StandardDeviation(thumb);
            double mad = _prev is null ? double.NaN : ChangeDetector.MeanAbsoluteDifference(_prev, thumb);
            _prev = thumb;
            SelfTestConsole.Line($"    mẫu: độ lệch chuẩn {sd:0.00} (trống nếu < 4), độ khác {mad:0.00} (đổi nếu > 2)");
            if (p.WantsFrame) p.Submit(crops);
        }
    }

    /// <summary>Chạy cả chuỗi phát hiện chữ + OCR + "dịch" (offline giả: trả chữ gốc, không gọi API) trên hình thật trong 6 giây.</summary>
    private static void PipelineRun(CaptureDevice dev, NativeFormat? format, GpuInfo? gpu)
    {
        if (format is null) return;
        var settings = AppSettings.Load();
        var profile = new ProfileStore(AppPaths.Profiles).Load(settings.ProfileId) ?? Profile.Default;
        SelfTestConsole.Line($"Chạy pipeline 6 giây trên hình thật, vùng: {string.Join(" + ", profile.AllRegions)}");
        using var cache = new TranslationCache(":memory:");
        var t0 = Stopwatch.StartNew();
        var p = new PipelineCoordinator(new WindowsOcr(), cache, e => SelfTestConsole.Line($"  {t0.ElapsedMilliseconds,6} ms  {e}"));
        p.Configure(profile, new FallbackTranslator(TranslateMode.OfflineOnly, null, "", offline: null));
        p.SetEnabled(true);
        using var win = Win32Window.Create(0, 1280, 720);
        using var renderer = new VideoRenderer(win.Handle, gpu);
        using var cap = new MfCaptureSource();
        cap.Start(dev, new PipelineSink(renderer, p, profile.AllRegions), format.Index);
        Thread.Sleep(6000);
        cap.Stop();
    }
}
