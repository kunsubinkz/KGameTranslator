using System.Net;
using System.Text;
using System.Text.Json;
using GameTranslator.Core;
using GameTranslator.Core.LocalHost;
using Xunit;

namespace GameTranslator.Core.Tests;

public class SentenceSplitterTests
{
    [Fact]
    public void KeepsAbbreviationsAndEllipsisTogether()
    {
        Assert.Equal(["Mr. Smith?!", "Wait...", "What is this?"], SentenceSplitter.Split("Mr. Smith?! Wait... What is this?"));
        Assert.Equal(["Wait... what?"], SentenceSplitter.Split("Wait... what?"));
        Assert.Equal(["Pi is 3.5 or so."], SentenceSplitter.Split("Pi is 3.5 or so."));
        Assert.Equal(["Hello"], SentenceSplitter.Split("  Hello  "));
        Assert.Empty(SentenceSplitter.Split("   "));
    }

    [Fact]
    public void SplitsQuotedSentences()
    {
        Assert.Equal(["\"Run!\"", "He ran."], SentenceSplitter.Split("\"Run!\" He ran."));
    }
}

public class LocalHostTranslatorTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return f(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LocalHostTranslator Make(Handler h, string? token = "tok") =>
        new(() => "http://127.0.0.1:47811/", () => token, () => 1, new HttpClient(h));

    [Fact]
    public async Task JoinsSentencesInOrderAndSendsToken()
    {
        var h = new Handler(r => Json(HttpStatusCode.OK, "{\"translations\":[\"Xin chào.\",\"Bạn khỏe không?\"],\"ms\":12}"));
        var t = Make(h);
        var text = await t.TranslateAsync(new TranslationRequest("Hello. How are you?"));
        Assert.Equal("Xin chào. Bạn khỏe không?", text);
        Assert.Equal("Bearer tok", h.Last!.Headers.Authorization!.ToString());
        Assert.Equal("http://127.0.0.1:47811/v1/translate", h.Last.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(h.LastBody!);
        Assert.Equal(2, doc.RootElement.GetProperty("texts").GetArrayLength());
        Assert.Equal("eng_Latn", doc.RootElement.GetProperty("src").GetString());
        Assert.Equal("vie_Latn", doc.RootElement.GetProperty("tgt").GetString());
    }

    [Theory]
    [InlineData(503, TranslationErrorKind.Unavailable, "đang nạp model")]
    [InlineData(401, TranslationErrorKind.Unavailable, "sai token")]
    [InlineData(500, TranslationErrorKind.Network, "HTTP 500")]
    public async Task MapsHttpErrors(int status, TranslationErrorKind kind, string detail)
    {
        var t = Make(new Handler(_ => Json((HttpStatusCode)status, "{}")));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => t.TranslateAsync(new TranslationRequest("Hi.")));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(detail, ex.Message);
    }

    [Fact]
    public async Task ConnectionRefusedIsUnavailable()
    {
        var t = Make(new Handler(_ => throw new HttpRequestException("refused")));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => t.TranslateAsync(new TranslationRequest("Hi.")));
        Assert.Equal(TranslationErrorKind.Unavailable, ex.Kind);
        Assert.Equal("host chưa chạy", ex.Message);
    }

    [Fact]
    public async Task TimeoutMapsToTimeout()
    {
        var t = Make(new Handler(_ => throw new TaskCanceledException()));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => t.TranslateAsync(new TranslationRequest("Hi.")));
        Assert.Equal(TranslationErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task HealthParses()
    {
        var t = Make(new Handler(_ => Json(HttpStatusCode.OK, "{\"version\":1,\"state\":\"ready\",\"model\":\"m\",\"device\":\"xpu:0 Arc\",\"dtype\":\"float16\",\"error\":null}")));
        var h = await t.HealthAsync();
        Assert.True(h.Ready);
        Assert.Equal("xpu:0 Arc", h.Device);
    }

    [Fact]
    public async Task FallbackUsesOfflineWhenOnlineFailsAndLabelsApple()
    {
        var offline = Make(new Handler(_ => Json(HttpStatusCode.OK, "{\"translations\":[\"Chào.\"],\"ms\":5}")));
        var online = new FailingOnline();
        var fb = new FallbackTranslator(TranslateMode.OnlineWithOfflineFallback, online, "m", TranslateProvider.Claude, offline);
        var outcome = await fb.TranslateAsync(new TranslationRequest("Hi."));
        Assert.Equal("Chào.", outcome.Text);
        Assert.Equal(EngineKind.Apple, outcome.Engine.Kind);
    }

    [Fact]
    public async Task FallbackShowsOriginalWhenHostDown()
    {
        var offline = Make(new Handler(_ => throw new HttpRequestException("x")));
        var fb = new FallbackTranslator(TranslateMode.OfflineOnly, null, "m", TranslateProvider.Claude, offline);
        var outcome = await fb.TranslateAsync(new TranslationRequest("Hi."));
        Assert.Equal("Hi.", outcome.Text);
        Assert.Equal(EngineKind.Original, outcome.Engine.Kind);
    }

    private sealed class FailingOnline : ITranslator
    {
        public string Id => "fail";
        public Task<string> TranslateAsync(TranslationRequest r, CancellationToken ct = default) =>
            throw new TranslationException(TranslationErrorKind.Network, "x");
    }
}
