using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace GameTranslator.Platform.Windows.Capture;

/// <summary>Nguồn hình thử (không cần capture card): nền chuyển động và hộp thoại tiếng Anh hiện/ẩn luân phiên.</summary>
public sealed class TestPatternSource : IDisposable
{
    public const string DeviceId = "test-pattern";
    public const string DeviceName = "Nguồn thử (không cần capture card)";

    public static readonly string[] Lines =
    [
        "Welcome to Hyrule, traveler. The kingdom needs you now more than ever.",
        "Take this sword and go to the temple. Find the sage and ask for help.",
        "Be careful out there. Monsters roam the fields after dark.",
    ];

    private Thread? _thread;
    private volatile bool _stop;
    public double MeasuredFps { get; private set; }
    public event Action? Stopped;

    public static byte[] RenderDialogFrame(string? text, int width = 1280, int height = 720)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            using var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(40, 90, 60), Color.FromArgb(20, 40, 80), 60f);
            g.FillRectangle(bg, 0, 0, width, height);
            if (text is not null)
            {
                var box = new Rectangle((int)(width * 0.1), (int)(height * 0.72), (int)(width * 0.8), (int)(height * 0.22));
                using var fill = new SolidBrush(Color.FromArgb(235, 15, 20, 45));
                g.FillRectangle(fill, box);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using var font = new Font("Segoe UI", height * 0.036f, FontStyle.Regular, GraphicsUnit.Pixel);
                var inner = new RectangleF(box.X + 18, box.Y + 14, box.Width - 36, box.Height - 28);
                g.DrawString(text, font, Brushes.White, inner);
            }
        }
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[width * height * 4];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            return px;
        }
        finally { bmp.UnlockBits(data); }
    }

    public void Start(IFrameSink sink, int width = 1280, int height = 720)
    {
        Stop();
        _stop = false;
        _thread = new Thread(() => Run(sink, width, height)) { IsBackground = true, Name = "TestPattern" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
    }

    private void Run(IFrameSink sink, int w, int h)
    {
        using var mmcss = Mmcss.Enter("Capture");
        var blank = RenderDialogFrame(null, w, h);
        var dialogs = Lines.Select(l => RenderDialogFrame(l, w, h)).ToArray();
        var frame = new byte[w * h * 4];
        var clock = Stopwatch.StartNew();
        long frames = 0, windowStart = 0, next = 0;
        while (!_stop)
        {
            // Chu kỳ 9 giây: 6 giây có hộp thoại (đổi câu mỗi chu kỳ), 3 giây trống.
            double t = clock.Elapsed.TotalSeconds;
            int cycle = (int)(t / 9);
            bool showDialog = t % 9 < 6;
            var src = showDialog ? dialogs[cycle % dialogs.Length] : blank;
            Buffer.BlockCopy(src, 0, frame, 0, frame.Length);

            // Ô vuông chạy ngang phía trên để thấy hình có mượt không.
            int sq = 60, sx = (int)(t * 300 % (w - sq)), sy = (int)(h * 0.2);
            for (int y = 0; y < sq; y++)
                for (int x = 0; x < sq; x++)
                {
                    int o = ((sy + y) * w + sx + x) * 4;
                    frame[o] = 40; frame[o + 1] = 200; frame[o + 2] = 255; frame[o + 3] = 255;
                }

            sink.OnFrame(FrameFormat.Bgra, frame, w, h, w * 4);
            frames++;
            long ms = clock.ElapsedMilliseconds;
            if (ms - windowStart >= 2000)
            {
                MeasuredFps = frames * 1000.0 / (ms - windowStart);
                frames = 0; windowStart = ms;
            }
            next += 1000 / 60;
            long wait = next - clock.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
        }
        MeasuredFps = 0;
        Stopped?.Invoke();
    }

    public void Dispose() => Stop();
}
