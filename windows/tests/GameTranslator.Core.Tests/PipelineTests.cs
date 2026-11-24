using GameTranslator.Core;
using GameTranslator.Core.Pipeline;
using Xunit;

namespace GameTranslator.Core.Tests;

public class PipelineTests
{
    private sealed class FakeOcr : IOcrEngine
    {
        public string Text = "Welcome to Hyrule, traveler.";
        public string Name => "fake";
        public Task<OcrResult> RecognizeAsync(BgraImage image, OcrSettings s, IReadOnlyList<string> w, CancellationToken ct = default) =>
            Task.FromResult(new OcrResult(Text, 1, 5));
    }

    private sealed class FakeTranslator : ITranslator
    {
        public string Id => "fake";
        public int Calls;
        public Task<string> TranslateAsync(TranslationRequest r, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult("Chào mừng đến Hyrule.");
        }
    }

    private static BgraImage Frame(bool text)
    {
        var img = BgraImage.Create(160, 40);
        for (int i = 0; i < 160 * 40; i++)
        {
            byte v = text ? (byte)((i / 3 % 2) * 220 + 20) : (byte)10;
            img.Pixels[i * 4] = img.Pixels[i * 4 + 1] = img.Pixels[i * 4 + 2] = v;
            img.Pixels[i * 4 + 3] = 255;
        }
        return img;
    }

    private static async Task Settle(PipelineCoordinator p)
    {
        for (int i = 0; i < 200 && !p.WantsFrame; i++) await Task.Delay(5);
        await Task.Delay(20);
    }

    [Fact]
    public async Task TextAppearsTranslatesThenClears()
    {
        var events = new List<PipelineEvent>();
        double now = 0;
        using var cache = new TranslationCache(":memory:");
        var ocr = new FakeOcr();
        var p = new PipelineCoordinator(ocr, cache, e => { lock (events) events.Add(e); }, () => now);
        var tr = new FakeTranslator();
        p.Configure(Profile.Default, new FallbackTranslator(TranslateMode.OnlineOnly, tr, "m"));
        p.SetEnabled(true);

        foreach (var t in new[] { 0.0, 200, 400, 600 })
        {
            now = t;
            p.Submit([Frame(true)]);
            await Settle(p);
        }
        Assert.Contains(events, e => e is PipelineEvent.Recognized);
        Assert.Contains(events, e => e is PipelineEvent.Subtitle { Cached: false, Target: "Chào mừng đến Hyrule." });
        Assert.Equal(1, tr.Calls);

        // Hộp thoại đóng: vùng trống đủ lâu thì ẩn phụ đề.
        foreach (var t in new[] { 700.0, 1000, 1600 })
        {
            now = t;
            p.Submit([Frame(false)]);
            await Settle(p);
        }
        Assert.Contains(events, e => e is PipelineEvent.Cleared);
    }

    [Fact]
    public async Task SecondTimeUsesCache()
    {
        var events = new List<PipelineEvent>();
        double now = 0;
        using var cache = new TranslationCache(":memory:");
        cache.Store("default", "Welcome to Hyrule, traveler.", "Đã lưu", "claude:m");
        var p = new PipelineCoordinator(new FakeOcr(), cache, e => { lock (events) events.Add(e); }, () => now);
        var tr = new FakeTranslator();
        p.Configure(Profile.Default, new FallbackTranslator(TranslateMode.OnlineOnly, tr, "m"));
        p.SetEnabled(true);
        foreach (var t in new[] { 0.0, 200, 400, 600 })
        {
            now = t;
            p.Submit([Frame(true)]);
            await Settle(p);
        }
        Assert.Contains(events, e => e is PipelineEvent.Subtitle { Cached: true, Target: "Đã lưu" });
        Assert.Equal(0, tr.Calls);
    }

    [Fact]
    public void DisabledPipelineDropsFrames()
    {
        using var cache = new TranslationCache(":memory:");
        var p = new PipelineCoordinator(new FakeOcr(), cache, _ => { });
        Assert.False(p.WantsFrame);
    }
}

public class PreprocessorTests
{
    private static BgraImage Solid(int w, int h, Func<int, int, byte> value)
    {
        var img = BgraImage.Create(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                img.Pixels[o] = value(x, y); img.Pixels[o + 1] = 0; img.Pixels[o + 2] = 0; img.Pixels[o + 3] = 255;
            }
        return img;
    }

    [Fact]
    public void BrightRegionIsLeftAsIs()
    {
        var img = Solid(40, 10, (x, _) => (byte)(x % 2 == 0 ? 20 : 230));
        Assert.Same(img, Preprocessor.AutoLevels(img));
    }

    [Fact]
    public void DimRegionIsStretched()
    {
        // Chữ xanh mờ (kênh B = 70) trên nền tối (B = 10): sau cân sáng phải sáng hẳn lên.
        var img = Solid(40, 10, (x, _) => (byte)(x % 2 == 0 ? 10 : 70));
        var res = Preprocessor.AutoLevels(img);
        Assert.NotSame(img, res);
        Assert.True(res.Pixels[4] >= 200, $"điểm chữ = {res.Pixels[4]}");
        Assert.True(res.Pixels[0] <= 10);
    }
}

public class ParagraphTests
{
    [Fact]
    public void ParagraphsKeepBoundaries()
    {
        var raw = "Hateno Village Research Lab\n\nRobbie Lookout Landing\n\nRobbie installed the sensor,\nwhich detects shrines.";
        Assert.Equal("Hateno Village Research Lab\nRobbie Lookout Landing\nRobbie installed the sensor, which detects shrines.",
            TextNormalizer.NormalizeParagraphs(raw));
    }

    [Fact]
    public void SingleParagraphMatchesNormalize()
    {
        Assert.Equal(TextNormalizer.Normalize("Hello\nthere, traveler."), TextNormalizer.NormalizeParagraphs("Hello\nthere, traveler."));
        Assert.Null(TextNormalizer.NormalizeParagraphs("  \n\n 12 "));
    }

    [Fact]
    public void NoteOnlyForMultiParagraphText()
    {
        Assert.Equal("", PromptBuilder.ParagraphNote("One line only."));
        Assert.Contains("nhiều đoạn", PromptBuilder.ParagraphNote("One\nTwo"));
    }
}
