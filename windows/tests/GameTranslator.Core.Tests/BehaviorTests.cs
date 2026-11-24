using GameTranslator.Core;
using Xunit;

namespace GameTranslator.Core.Tests;

/// <summary>Port tay từ TranslatorTests.swift (phần phụ thuộc thời gian / bất đồng bộ chưa có vector).</summary>
public class FallbackTranslatorTests
{
    private sealed class Fake(Func<string> result) : ITranslator
    {
        public string Id => "fake";
        public Task<string> TranslateAsync(TranslationRequest request, CancellationToken ct = default) => Task.FromResult(result());
    }

    private static Fake Ok(string text) => new(() => text);
    private static Fake Fail(TranslationException ex) => new(() => throw ex);

    private static readonly TranslationRequest Req = new("Hello");

    private static FallbackTranslator Make(TranslateMode mode, ITranslator online, ITranslator offline,
        RateLimiter? limiter = null, Func<DateTimeOffset>? now = null) =>
        new(mode, online, "m", TranslateProvider.Claude, offline, limiter, now);

    [Fact]
    public async Task UsesOnlineWhenAvailable()
    {
        var o = await Make(TranslateMode.OnlineWithOfflineFallback, Ok("Chào"), Ok("Xin chào")).TranslateAsync(Req);
        Assert.Equal(new TranslationOutcome("Chào", TranslationEngine.Claude("m")), o);
    }

    [Fact]
    public async Task FallsBackToOfflineOnNetworkError()
    {
        var o = await Make(TranslateMode.OnlineWithOfflineFallback,
            Fail(new(TranslationErrorKind.Network, "x")), Ok("Xin chào")).TranslateAsync(Req);
        Assert.Equal(TranslationEngine.Apple, o.Engine);
        Assert.Equal("Xin chào", o.Text);
    }

    [Fact]
    public async Task FallsBackToOriginalWhenEverythingFails()
    {
        var o = await Make(TranslateMode.OnlineWithOfflineFallback,
            Fail(new(TranslationErrorKind.Timeout)), Fail(new(TranslationErrorKind.Unavailable, "x"))).TranslateAsync(Req);
        Assert.Equal(new TranslationOutcome("Hello", TranslationEngine.Original), o);
    }

    [Fact]
    public async Task OnlineOnlyNeverUsesOffline()
    {
        var o = await Make(TranslateMode.OnlineOnly, Fail(new(TranslationErrorKind.EmptyResponse)), Ok("Xin chào")).TranslateAsync(Req);
        Assert.Equal(TranslationEngine.Original, o.Engine);
    }

    [Fact]
    public async Task OfflineOnlySkipsOnline()
    {
        var o = await Make(TranslateMode.OfflineOnly, Ok("Chào"), Ok("Xin chào")).TranslateAsync(Req);
        Assert.Equal(TranslationEngine.Apple, o.Engine);
    }

    [Fact]
    public async Task QuotaErrorBacksOffOnline()
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var t = Make(TranslateMode.OnlineWithOfflineFallback, Fail(new(TranslationErrorKind.QuotaExceeded)), Ok("Xin chào"), now: () => date);
        await t.TranslateAsync(Req);
        date = date.AddSeconds(0.5);
        Assert.Equal(TranslationEngine.Apple, (await t.TranslateAsync(Req)).Engine);
    }

    [Fact]
    public async Task ExhaustedLimiterSkipsOnline()
    {
        var t = Make(TranslateMode.OnlineWithOfflineFallback, Ok("Chào"), Ok("Xin chào"), new RateLimiter(1, 1));
        Assert.Equal(TranslationEngine.Claude("m"), (await t.TranslateAsync(Req)).Engine);
        Assert.Equal(TranslationEngine.Apple, (await t.TranslateAsync(Req)).Engine);
    }

    [Fact]
    public async Task GeminiProviderUsesGeminiEngine()
    {
        var t = new FallbackTranslator(TranslateMode.OnlineOnly, Ok("Chào"), "m", TranslateProvider.Gemini);
        Assert.Equal(TranslationEngine.Gemini("m"), (await t.TranslateAsync(Req)).Engine);
    }

    [Fact]
    public async Task UpgradeUsesOnlineEvenInOfflineFirstFlow()
    {
        var t = Make(TranslateMode.OnlineWithOfflineFallback, Ok("Chào"), Ok("Xin chào"));
        Assert.Equal(new TranslationOutcome("Chào", TranslationEngine.Claude("m")), await t.UpgradeAsync(Req));
        Assert.Null(await Make(TranslateMode.OfflineOnly, Ok("Chào"), Ok("Xin chào")).UpgradeAsync(Req));
    }
}

public class CacheAndStoreTests
{
    [Fact]
    public void CacheStatsAndEngineOnUpdate()
    {
        using var cache = new TranslationCache(":memory:");
        cache.Store("p", "A long sentence", "Câu dài", "apple");
        cache.Lookup("p", "A long sentence");
        cache.Lookup("p", "A long sentence");
        Assert.Equal((1, 2), cache.Stats("p"));
        var key = TranslationCache.Key("p", "A long sentence");
        cache.UpdateTarget(key, "Một câu dài", "claude:m");
        Assert.Equal("claude:m", cache.History("p").First().Engine);
        Assert.Equal("Một câu dài", cache.Lookup("p", "a LONG sentence")!.Target);
    }

    [Fact]
    public void MetaRoundTrip()
    {
        using var cache = new TranslationCache(":memory:");
        Assert.Null(cache.Meta("k"));
        cache.SetMeta("k", "v1");
        cache.SetMeta("k", "v2");
        Assert.Equal("v2", cache.Meta("k"));
    }

    [Fact]
    public void ProfileRoundTripAndStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gt-" + Guid.NewGuid());
        try
        {
            var store = new ProfileStore(dir);
            store.SeedIfNeeded();
            Assert.Equal(["default", "zelda-totk"], store.List().Select(p => p.Id).Order().ToArray());
            var p = Profile.ZeldaTotk;
            p.Subtitle.FontSize = 40;
            store.Save(p);
            Assert.Equal(40, store.Load("zelda-totk")!.Subtitle.FontSize);
            Assert.Equal("duong-ve-nha-2", ProfileStore.MakeId("Đường Về Nhà 2!"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void OlderProfileJsonStillDecodes()
    {
        var json = """{"id":"old","name":"Old","region":{"x":0.1,"y":0.2,"width":0.3,"height":0.4},"subtitle":{"position":"below","fontSize":30}}""";
        var profile = Profile.FromJson(json)!;
        Assert.Equal(SubtitlePosition.Below, profile.Subtitle.Position);
        Assert.Equal(0.6, profile.Subtitle.BackgroundOpacity);
        Assert.True(profile.Translate.UpgradeOffline);
    }
}
