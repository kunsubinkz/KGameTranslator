using GameTranslator.Core;
using GameTranslator.Core.Pipeline;
using GameTranslator.Platform.Windows;
using GameTranslator.Platform.Windows.Capture;
using GameTranslator.Platform.Windows.Gpu;
using GameTranslator.Platform.Windows.Ocr;
using GameTranslator.Platform.Windows.Render;
using Xunit;

namespace GameTranslator.Windows.Tests;

public class MediaTypeSelectorTests
{
    private static NativeFormat F(int i, int w, int h, double fps, string sub) => new(i, w, h, fps, sub, MediaTypeSelector.IsRaw(sub));

    [Fact]
    public void Prefers1080p60Raw()
    {
        var list = new[] { F(0, 1920, 1080, 60, "MJPG"), F(1, 1920, 1080, 60, "NV12"), F(2, 1280, 720, 60, "NV12"), F(3, 1920, 1080, 30, "NV12") };
        Assert.Equal(1, MediaTypeSelector.Choose(list)!.Index);
    }

    [Fact]
    public void FallsBackToMjpegWhenRawLacks60()
    {
        var list = new[] { F(0, 1920, 1080, 60, "MJPG"), F(1, 1920, 1080, 5, "YUY2"), F(2, 1280, 720, 30, "YUY2") };
        Assert.Equal(0, MediaTypeSelector.Choose(list)!.Index);
    }

    [Fact]
    public void NeverInventsFormats()
    {
        Assert.Null(MediaTypeSelector.Choose([]));
        var only = new[] { F(7, 640, 480, 30, "YUY2") };
        Assert.Equal(7, MediaTypeSelector.Choose(only)!.Index);
    }
}

public class GpuSelectionTests
{
    private static GpuInfo G(string name, uint vendor, long vram, params string[] outputs) =>
        new(name, vendor, 1, vram, 0, vram < 512L * 1024 * 1024, outputs,
            outputs.Select((_, i) => new RectI(i * 1920, 0, (i + 1) * 1920, 1080)).ToList(), true);

    private readonly GpuInfo _discrete = G("NVIDIA RTX", 0x10DE, 12L << 30);
    private readonly GpuInfo _integrated = G("Intel UHD", 0x8086, 128L << 20, "DISPLAY1");

    [Fact]
    public void AutoFollowsDisplayOwner()
    {
        var gpus = new[] { _discrete, _integrated };
        Assert.Equal(_integrated, GpuCatalog.Select(gpus, GpuMode.Auto, null, (100, 100)));
    }

    [Fact]
    public void HighPerformanceAndPowerSaving()
    {
        var gpus = new[] { _discrete, _integrated };
        Assert.Equal(_discrete, GpuCatalog.Select(gpus, GpuMode.HighPerformance, null, null));
        Assert.Equal(_integrated, GpuCatalog.Select(gpus, GpuMode.PowerSaving, null, null));
        Assert.Equal(_discrete, GpuCatalog.Select(gpus, GpuMode.Specific, _discrete.StableKey, null));
    }

    [Fact]
    public void EmptyListGivesNull() => Assert.Null(GpuCatalog.Select([], GpuMode.Auto, null, null));
}

public class DeviceMatchTests
{
    [Fact]
    public void MatchesAudioByCardName()
    {
        var video = new CaptureDevice("v", "UGREEN-25854");
        var audio = new[] { new CaptureDevice("a1", "Microphone (Realtek)"), new CaptureDevice("a2", "Digital Audio Interface (UGREEN-25854)") };
        Assert.Equal("a2", DeviceDiscovery.BestAudioMatch(video, audio)!.Id);
        Assert.Null(DeviceDiscovery.BestAudioMatch(video, [audio[0]]));
    }
}

/// <summary>Cần máy Windows thật có GPU.</summary>
public class GpuRenderTests
{
    [Fact]
    public void RendersQuadrantsOnEveryGpuInEveryMode()
    {
        foreach (var gpu in GpuCatalog.Enumerate())
        {
            using var win = Win32Window.Create(0, 320, 180);
            using var r = new VideoRenderer(win.Handle, gpu);
            r.SynchronousRender = true;
            const int w = 64, h = 36;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bool right = x >= w / 2, bottom = y >= h / 2;
                    px[o + 2] = (byte)(!right && !bottom ? 255 : 0);   // đỏ: trên-trái
                    px[o] = (byte)(right && !bottom ? 255 : 0);        // xanh dương: trên-phải
                    px[o + 3] = 255;
                }
            foreach (var mode in Enum.GetValues<ScalingMode>())
            {
                r.Mode = mode;
                r.CaptureRender = true;
                r.OnFrame(FrameFormat.Bgra, px, w, h, w * 4);
                var (bgra, rw, rh) = r.LastRendered!.Value;
                int tl = ((rh / 4) * rw + rw / 4) * 4, tr = ((rh / 4) * rw + 3 * rw / 4) * 4;
                Assert.True(bgra[tl + 2] > 200 && bgra[tl] < 60, $"{gpu.Name} {mode}: trên-trái phải đỏ");
                Assert.True(bgra[tr] > 200 && bgra[tr + 2] < 60, $"{gpu.Name} {mode}: trên-phải phải xanh dương");
            }
        }
    }
}

public class OcrTests
{
    [Fact]
    public async Task ReadsDialogTextFromSampleImage()
    {
        if (!WindowsOcr.IsEnglishAvailable) return; // máy thiếu gói OCR tiếng Anh
        const string text = "Welcome to Hyrule, traveler.";
        var frame = TestPatternSource.RenderDialogFrame(text);
        var region = Profile.Default.MainRegion;
        int x = (int)(region.X * 1280), y = (int)(region.Y * 720), w = (int)(region.Width * 1280), h = (int)(region.Height * 720);
        var img = BgraImage.Create(w, h);
        for (int row = 0; row < h; row++) Buffer.BlockCopy(frame, ((y + row) * 1280 + x) * 4, img.Pixels, row * w * 4, w * 4);
        var res = await new WindowsOcr().RecognizeAsync(Preprocessor.Prepare(img, new OcrSettings()), new OcrSettings(), []);
        Assert.True(TextNormalizer.CharacterErrorRate(text, TextNormalizer.Normalize(res.Text) ?? "") < 0.1);
    }
}

public class CredentialTests
{
    [Fact]
    public void WritesReadsAndDeletes()
    {
        CredentialStore.Write("unit-test", "bí-mật");
        Assert.Equal("bí-mật", CredentialStore.Read("unit-test"));
        CredentialStore.Delete("unit-test");
        Assert.Null(CredentialStore.Read("unit-test"));
    }
}

public class Nv12LayoutTests
{
    [Fact]
    public void PaddedPlaneHeightIsHandled()
    {
        // Card đệm mặt phẳng Y lên 1088 dòng: UV nằm sau 1088 dòng chứ không phải 1080.
        const int w = 64, h = 1080, padded = 1088;
        var data = new byte[w * padded * 3 / 2];
        for (int i = 0; i < w * padded; i++) data[i] = 120;                 // Y xám
        for (int i = w * padded; i < data.Length; i += 2) { data[i] = 90; data[i + 1] = 240; } // U thấp, V cao = đỏ
        using var win = Win32Window.Create(0, 64, 64);
        using var r = new VideoRenderer(win.Handle, null) { SynchronousRender = true };
        r.OnFrame(FrameFormat.Nv12, data, w, h, w);
        var snap = r.Snapshot()!.Value;
        int o = ((h / 2) * w + w / 2) * 4;
        Assert.True(snap.Pixels[o + 2] > 180 && snap.Pixels[o + 1] < 120, $"giữa khung phải đỏ: B={snap.Pixels[o]} G={snap.Pixels[o + 1]} R={snap.Pixels[o + 2]}");
    }
}

public class OcrParagraphTests
{
    private static (string, (double, double, double)) L(string t, double top, double bottom, double left) => (t, (top, bottom, left));

    [Fact]
    public void JoinsRowsAndSplitsParagraphsByGap()
    {
        var text = WindowsOcr.JoinWithParagraphs(
        [
            L("Hateno Village Research Lab", 0, 40, 100),
            L("Robbie", 70, 100, 100),                // cách tiêu đề > 0,6 chiều cao dòng: đoạn mới
            L("Lookout Landing", 72, 100, 700),       // cùng hàng với "Robbie": nối bằng dấu cách
            L("Robbie installed the sensor,", 140, 170, 100),
            L("which detects shrines.", 172, 202, 100), // sát dòng trên: cùng đoạn
        ]);
        Assert.Equal("Hateno Village Research Lab\n\nRobbie Lookout Landing\n\nRobbie installed the sensor,\nwhich detects shrines.", text);
    }
}
