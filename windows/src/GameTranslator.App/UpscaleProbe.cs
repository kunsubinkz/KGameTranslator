using GameTranslator.Platform.Windows;
using GameTranslator.Platform.Windows.Capture;
using GameTranslator.Platform.Windows.Gpu;
using GameTranslator.Platform.Windows.Render;

namespace GameTranslator.App;

/// <summary>`--upscale-test &lt;thư mục&gt; [ảnh x y w h]`: phóng 2x cùng một ảnh qua từng chế độ hiển thị, xuất PNG để so độ mịn.
/// Không có ảnh thì dùng ảnh tổng hợp: đường xéo, vòng tròn và chữ vẽ không khử răng cưa (giống đồ họa game).</summary>
public static class UpscaleProbe
{
    public static int Run(string[] args)
    {
        SelfTestConsole.Attach();
        int i = Array.IndexOf(args, "--upscale-test");
        var dir = args.ElementAtOrDefault(i + 1) ?? "upscale-out";
        Directory.CreateDirectory(dir);
        var (px, w, h) = args.Length >= i + 7
            ? Crop(args[i + 2], int.Parse(args[i + 3]), int.Parse(args[i + 4]), int.Parse(args[i + 5]), int.Parse(args[i + 6]))
            : Synthetic(480, 270);

        var gpu = GpuCatalog.Select(GpuCatalog.Enumerate(), GpuMode.Auto, null, null);
        using var win = Win32Window.Create(0, w * 2, h * 2);
        using var r = new VideoRenderer(win.Handle, gpu) { SynchronousRender = true };
        foreach (var (name, mode, stops, aa) in new (string, ScalingMode, float, bool)[]
                 {
                     ("1-mac-dinh", ScalingMode.Default, 0, false),
                     ("2-diem-anh", ScalingMode.Pixel, 0, false),
                     ("3-fsr-net-0.2", ScalingMode.Smooth4K, 0.2f, false),
                     ("4-fsr-fxaa", ScalingMode.Smooth4K, 0.2f, true),
                 })
        {
            r.Mode = mode;
            r.RcasStops = stops;
            r.AntiAlias = aa;
            r.CaptureRender = true;
            r.OnFrame(FrameFormat.Bgra, px, w, h, w * 4);
            if (r.LastRendered is not { } shot) { SelfTestConsole.Line(name + ": không đọc lại được ảnh"); continue; }
            var file = Path.Combine(dir, name + ".png");
            Save(shot.Bgra, shot.Width, shot.Height, file);
            SelfTestConsole.Line($"{name}: {shot.Width}x{shot.Height} → {file}");
        }
        return 0;
    }

    /// <summary>`--upscale-perf`: đo thời gian vẽ mỗi khung khi phóng 1080p lên 4K (đồng bộ dọc tắt, độ trễ khung = 1
    /// nên Present chờ GPU xong khung trước: thời gian mỗi khung xấp xỉ thời gian GPU).</summary>
    public static int Perf()
    {
        SelfTestConsole.Attach();
        foreach (var gpu in GpuCatalog.Enumerate())
        {
            using var win = Win32Window.Create(0, 3840, 2160);
            using var r = new VideoRenderer(win.Handle, gpu) { SynchronousRender = true, VSync = false };
            r.StatusChanged += m => SelfTestConsole.Line("    trạng thái: " + m);
            var frame = Synthetic(1920, 1080).Item1;
            foreach (var (name, mode, aa) in new (string, ScalingMode, bool)[]
                     {
                         ("Mặc định", ScalingMode.Default, false), ("FSR", ScalingMode.Smooth4K, false), ("FSR + FXAA", ScalingMode.Smooth4K, true),
                     })
            {
                r.Mode = mode; r.AntiAlias = aa;
                for (int k = 0; k < 20; k++) r.OnFrame(FrameFormat.Bgra, frame, 1920, 1080, 1920 * 4);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                const int n = 200;
                for (int k = 0; k < n; k++) r.OnFrame(FrameFormat.Bgra, frame, 1920, 1080, 1920 * 4);
                SelfTestConsole.Line($"{gpu.Name} · {name}: {sw.Elapsed.TotalMilliseconds / n:0.00} ms/khung, GPU đo được {r.GpuMs:0.00} ms, đọc {r.TimerReads}/{r.TimerMisses} (1080p → 4K)");
            }
        }
        return 0;
    }

    private static (byte[], int, int) Synthetic(int w, int h)
    {
        var px = new byte[w * h * 4];
        void Set(int x, int y, byte v)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int o = (y * w + x) * 4;
            px[o] = px[o + 1] = px[o + 2] = v; px[o + 3] = 255;
        }
        for (int k = 0; k < px.Length; k += 4) { px[k] = 60; px[k + 1] = 30; px[k + 2] = 10; px[k + 3] = 255; }
        // Đường xéo nhiều góc, không khử răng cưa.
        foreach (var slope in new[] { 0.2, 0.45, 1.0, 2.5 })
            for (int x = 0; x < 200; x++) { int y = (int)(x * slope); for (int t = 0; t < 2; t++) Set(10 + x, 10 + y + t, 235); }
        // Vòng tròn.
        for (int a = 0; a < 2000; a++) { double t = a * Math.PI * 2 / 2000; Set(340 + (int)(70 * Math.Cos(t)), 135 + (int)(70 * Math.Sin(t)), 235); }
        // Chữ nhỏ vẽ bằng GDI không khử răng cưa.
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            g.Clear(System.Drawing.Color.Transparent);
            using var font = new System.Drawing.Font("Segoe UI", 15, System.Drawing.FontStyle.Italic, System.Drawing.GraphicsUnit.Pixel);
            g.DrawString("Hateno Village Research Lab", font, System.Drawing.Brushes.White, 20, 225);
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bmp.GetPixel(x, y).A > 128) { int o = (y * w + x) * 4; px[o] = 255; px[o + 1] = 190; px[o + 2] = 80; }
        return (px, w, h);
    }

    private static (byte[], int, int) Crop(string file, int x, int y, int w, int h)
    {
        using var src = new System.Drawing.Bitmap(file);
        var px = new byte[w * h * 4];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                var c = src.GetPixel(x + xx, y + yy);
                int o = (yy * w + xx) * 4;
                px[o] = c.B; px[o + 1] = c.G; px[o + 2] = c.R; px[o + 3] = 255;
            }
        return (px, w, h);
    }

    private static void Save(byte[] bgra, int w, int h, string file)
    {
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
        bmp.UnlockBits(bd);
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }
}
