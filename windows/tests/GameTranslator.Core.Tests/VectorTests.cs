using System.Text.Json;
using GameTranslator.Core;
using Xunit;

namespace GameTranslator.Core.Tests;

internal static class Vectors
{
    public static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", name + ".json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    /// <summary>So JSON theo cấu trúc: thứ tự khóa không quan trọng, số thực sai số 1e-9, bỏ qua khóa bắt đầu bằng "_".</summary>
    public static void AssertJsonEqual(JsonElement expected, JsonElement actual, string path = "$")
    {
        Assert.True(expected.ValueKind == actual.ValueKind
                    || (IsBool(expected) && IsBool(actual)),
            $"{path}: kiểu {expected.ValueKind} ≠ {actual.ValueKind}");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var e = expected.EnumerateObject().Where(p => !p.Name.StartsWith('_')).ToDictionary(p => p.Name, p => p.Value);
                var a = actual.EnumerateObject().Where(p => !p.Name.StartsWith('_')).ToDictionary(p => p.Name, p => p.Value);
                Assert.True(e.Keys.ToHashSet().SetEquals(a.Keys), $"{path}: khóa {string.Join(",", e.Keys.Order())} ≠ {string.Join(",", a.Keys.Order())}");
                foreach (var (k, v) in e) AssertJsonEqual(v, a[k], $"{path}.{k}");
                break;
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (int i = 0; i < expected.GetArrayLength(); i++)
                    AssertJsonEqual(expected[i], actual[i], $"{path}[{i}]");
                break;
            case JsonValueKind.Number:
                Assert.True(Math.Abs(expected.GetDouble() - actual.GetDouble()) <= 1e-9, $"{path}: {expected} ≠ {actual}");
                break;
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }

    private static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

    public static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, Profile.JsonOptions);

    public static byte[] Bytes(JsonElement array) => array.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray();
}

public class TextNormalizerVectors
{
    private readonly JsonElement _v = Vectors.Load("text_normalizer");

    [Fact]
    public void Normalize()
    {
        foreach (var c in _v.GetProperty("normalize").EnumerateArray())
        {
            var input = c.GetProperty("input").GetString()!;
            var expected = c.GetProperty("expected").GetString();
            Assert.Equal(expected, TextNormalizer.Normalize(input));
        }
    }

    [Fact]
    public void Similarity()
    {
        foreach (var c in _v.GetProperty("similarity").EnumerateArray())
        {
            var a = c.GetProperty("a").GetString()!;
            var b = c.GetProperty("b").GetString()!;
            Assert.Equal(c.GetProperty("levenshtein").GetInt32(), TextNormalizer.Levenshtein(a, b));
            Assert.Equal(c.GetProperty("similarity").GetDouble(), TextNormalizer.Similarity(a, b), 1e-9);
            Assert.Equal(c.GetProperty("isSameSentence").GetBoolean(), TextNormalizer.IsSameSentence(a, b));
        }
    }

    [Fact]
    public void CharacterErrorRate()
    {
        foreach (var c in _v.GetProperty("characterErrorRate").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("cer").GetDouble(),
                TextNormalizer.CharacterErrorRate(c.GetProperty("expected").GetString()!, c.GetProperty("actual").GetString()!), 1e-9);
        }
    }
}

public class ChangeDetectorVectors
{
    private readonly JsonElement _v = Vectors.Load("change_detector");

    private static ChangeDetector.Config ReadConfig(JsonElement c) => new(
        c.GetProperty("changeThreshold").GetDouble(), c.GetProperty("stableMs").GetDouble(),
        c.GetProperty("hideDelayMs").GetDouble(), c.GetProperty("blankStdDev").GetDouble());

    [Fact]
    public void DefaultConfigMatches() => Assert.Equal(ReadConfig(_v.GetProperty("defaultConfig")), new ChangeDetector.Config());

    [Fact]
    public void StandardDeviation()
    {
        foreach (var c in _v.GetProperty("standardDeviation").EnumerateArray())
            Assert.Equal(c.GetProperty("standardDeviation").GetDouble(),
                ChangeDetector.StandardDeviation(Vectors.Bytes(c.GetProperty("samples"))), 1e-9);
    }

    [Fact]
    public void MeanAbsoluteDifference()
    {
        foreach (var c in _v.GetProperty("meanAbsoluteDifference").EnumerateArray())
            Assert.Equal(c.GetProperty("meanAbsoluteDifference").GetDouble(),
                ChangeDetector.MeanAbsoluteDifference(Vectors.Bytes(c.GetProperty("a")), Vectors.Bytes(c.GetProperty("b"))), 1e-9);
    }

    [Fact]
    public void Scenarios()
    {
        var config = ReadConfig(_v.GetProperty("config"));
        foreach (var s in _v.GetProperty("scenarios").EnumerateArray())
        {
            var name = s.GetProperty("name").GetString();
            var detector = new ChangeDetector(config);
            int i = 0;
            foreach (var step in s.GetProperty("steps").EnumerateArray())
            {
                i++;
                if (step.TryGetProperty("reset", out var reset) && reset.GetBoolean())
                {
                    detector.Reset();
                    continue;
                }
                var actual = detector.Process(Vectors.Bytes(step.GetProperty("samples")), step.GetProperty("nowMs").GetDouble());
                var expected = Enum.Parse<ChangeDetector.Event>(step.GetProperty("expected").GetString()!, ignoreCase: true);
                Assert.True(expected == actual, $"{name} bước {i}: mong đợi {expected}, nhận {actual}");
            }
        }
    }
}

public class PromptBuilderVectors
{
    private readonly JsonElement _v = Vectors.Load("prompt_builder");

    [Fact]
    public void BaseInstruction() => Assert.Equal(PromptBuilder.BaseInstruction, _v.GetProperty("baseInstruction").GetString());

    [Fact]
    public void SystemInstruction()
    {
        foreach (var c in _v.GetProperty("systemInstruction").EnumerateArray())
        {
            var glossary = c.GetProperty("glossary").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            Assert.Equal(c.GetProperty("expected").GetString(), PromptBuilder.SystemInstruction(glossary));
        }
    }

    [Fact]
    public void UserContent()
    {
        foreach (var c in _v.GetProperty("userContent").EnumerateArray())
        {
            var context = c.GetProperty("context").EnumerateArray()
                .Select(p => new ContextPair(p.GetProperty("source").GetString()!, p.GetProperty("target").GetString()!)).ToList();
            var request = new TranslationRequest(c.GetProperty("text").GetString()!, context);
            Assert.Equal(c.GetProperty("expected").GetString(), PromptBuilder.UserContent(request));
        }
    }
}

public class CacheKeyVectors
{
    [Fact]
    public void Keys()
    {
        foreach (var c in Vectors.Load("cache_key").GetProperty("cases").EnumerateArray())
            Assert.Equal(c.GetProperty("key").GetString(),
                TranslationCache.Key(c.GetProperty("profileId").GetString()!, c.GetProperty("source").GetString()!));
    }
}

public class RateLimiterVectors
{
    private readonly JsonElement _v = Vectors.Load("rate_limiter");

    [Fact]
    public void DayKeys()
    {
        foreach (var c in _v.GetProperty("dayKey").EnumerateArray())
            Assert.Equal(c.GetProperty("dayKey").GetString(),
                RateLimiter.DayKeyFor(DateTimeOffset.FromUnixTimeSeconds(c.GetProperty("unixSeconds").GetInt64())));
    }

    [Fact]
    public void Scenarios()
    {
        foreach (var s in _v.GetProperty("scenarios").EnumerateArray())
        {
            var name = s.GetProperty("name").GetString();
            var start = DateTimeOffset.FromUnixTimeSeconds(s.GetProperty("startUnixSeconds").GetInt64());
            (string, int)? restored = s.TryGetProperty("restored", out var r)
                ? (r.GetProperty("dayKey").GetString()!, r.GetProperty("count").GetInt32())
                : null;
            var limiter = new RateLimiter(s.GetProperty("perMinute").GetInt32(), s.GetProperty("perDay").GetInt32(), start, restored);
            int i = 0;
            foreach (var step in s.GetProperty("steps").EnumerateArray())
            {
                i++;
                var at = start.AddSeconds(step.GetProperty("offsetSeconds").GetDouble());
                var op = step.GetProperty("op").GetString();
                var expected = step.GetProperty("expected");
                if (op == "acquire")
                    Assert.True(expected.GetBoolean() == limiter.TryAcquire(at), $"{name} bước {i}: acquire");
                else
                    Assert.True(expected.GetInt32() == limiter.RemainingToday(at), $"{name} bước {i}: remaining");
            }
        }
    }
}

public class ProfileVectors
{
    private readonly JsonElement _v = Vectors.Load("profile");

    [Fact]
    public void Decode()
    {
        foreach (var c in _v.GetProperty("decode").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var profile = Profile.FromJson(c.GetProperty("input").GetString()!)!;
            // Đọc rồi ghi lại phải ra đúng JSON mong đợi.
            using var round = JsonDocument.Parse(profile.ToJson());
            Vectors.AssertJsonEqual(c.GetProperty("expected"), round.RootElement, name!);
            Assert.Equal(c.GetProperty("activeModel").GetString(), profile.Translate.ActiveModel);
            Vectors.AssertJsonEqual(c.GetProperty("allRegions"), Vectors.ToElement(profile.AllRegions), name + ".allRegions");
            Vectors.AssertJsonEqual(c.GetProperty("unionRegion"), Vectors.ToElement(profile.UnionRegion), name + ".union");
        }
    }

    [Fact]
    public void BuiltIns()
    {
        var b = _v.GetProperty("builtIns");
        using var d = JsonDocument.Parse(Profile.Default.ToJson());
        using var z = JsonDocument.Parse(Profile.ZeldaTotk.ToJson());
        Vectors.AssertJsonEqual(b.GetProperty("default"), d.RootElement);
        Vectors.AssertJsonEqual(b.GetProperty("zeldaTOTK"), z.RootElement);
    }

    [Fact]
    public void RegionClamped()
    {
        foreach (var c in _v.GetProperty("regionClamped").EnumerateArray())
        {
            var i = c.GetProperty("input");
            var region = new Region(i.GetProperty("x").GetDouble(), i.GetProperty("y").GetDouble(),
                i.GetProperty("width").GetDouble(), i.GetProperty("height").GetDouble());
            Vectors.AssertJsonEqual(c.GetProperty("expected"), Vectors.ToElement(region.Clamped()));
        }
    }

    [Fact]
    public void Providers()
    {
        foreach (var c in _v.GetProperty("providers").EnumerateArray())
        {
            var p = Enum.Parse<TranslateProvider>(c.GetProperty("provider").GetString()!, ignoreCase: true);
            Assert.Equal(c.GetProperty("title").GetString(), p.Title());
            Assert.Equal(c.GetProperty("keyPlaceholder").GetString(), p.KeyPlaceholder());
            var presets = c.GetProperty("presets").EnumerateArray()
                .Select(x => (x.GetProperty("id").GetString()!, x.GetProperty("title").GetString()!)).ToList();
            Assert.Equal(presets, p.Presets().ToList());
        }
    }
}

public class TranslatorVectors
{
    private readonly JsonElement _v = Vectors.Load("translators");

    private sealed class StubHandler(int status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body) });
    }

    private TranslationRequest Request()
    {
        var r = _v.GetProperty("request");
        var ctx = r.GetProperty("context").EnumerateArray()
            .Select(p => new ContextPair(p.GetProperty("source").GetString()!, p.GetProperty("target").GetString()!)).ToList();
        var glossary = r.GetProperty("glossary").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        return new TranslationRequest(r.GetProperty("text").GetString()!, ctx, glossary);
    }

    private string ApiKey => _v.GetProperty("request").GetProperty("apiKey").GetString()!;

    private static void AssertRequest(JsonElement expected, HttpRequestMessage message)
    {
        Assert.Equal(expected.GetProperty("method").GetString(), message.Method.Method);
        Assert.Equal(expected.GetProperty("url").GetString(), message.RequestUri!.ToString());
        foreach (var h in expected.GetProperty("headers").EnumerateObject())
        {
            if (h.Name == "Content-Type")
                Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            else
                Assert.Equal(h.Value.GetString(), message.Headers.GetValues(h.Name).Single());
        }
        using var body = JsonDocument.Parse(message.Content!.ReadAsStringAsync().Result);
        Vectors.AssertJsonEqual(expected.GetProperty("body"), body.RootElement);
    }

    [Fact]
    public void ClaudeRequests()
    {
        foreach (var c in _v.GetProperty("claude").GetProperty("requests").EnumerateArray())
        {
            var t = new ClaudeTranslator(c.GetProperty("model").GetString()!, () => ApiKey);
            AssertRequest(c, t.MakeRequest(Request(), ApiKey));
        }
        Assert.Equal(_v.GetProperty("claude").GetProperty("id").GetString(), new ClaudeTranslator("claude-haiku-5-5", () => ApiKey).Id);
    }

    [Fact]
    public void GeminiRequests()
    {
        foreach (var c in _v.GetProperty("gemini").GetProperty("requests").EnumerateArray())
        {
            var t = new GeminiTranslator(c.GetProperty("model").GetString()!, () => ApiKey);
            AssertRequest(c, t.MakeRequest(Request(), ApiKey));
        }
        Assert.Equal(_v.GetProperty("gemini").GetProperty("id").GetString(), new GeminiTranslator("gemini-flash-lite-latest", () => ApiKey).Id);
    }

    private static string Describe(Action act)
    {
        try { act(); return "ok"; }
        catch (TranslationException ex) { return ex.Describe(); }
    }

    [Fact]
    public void ParseResponses()
    {
        foreach (var provider in new[] { "claude", "gemini" })
        {
            foreach (var c in _v.GetProperty(provider).GetProperty("parse").EnumerateArray())
            {
                var response = c.GetProperty("response").GetString()!;
                Func<string, string> parse = provider == "claude" ? ClaudeTranslator.ParseResponse : GeminiTranslator.ParseResponse;
                if (c.TryGetProperty("error", out var error))
                    Assert.Equal(error.GetString(), Describe(() => parse(response)));
                else
                    Assert.Equal(c.GetProperty("result").GetString(), parse(response));
            }
        }
    }

    [Fact]
    public async Task HttpStatusMapping()
    {
        foreach (var provider in new[] { "claude", "gemini" })
        {
            foreach (var c in _v.GetProperty(provider).GetProperty("httpStatus").EnumerateArray())
            {
                var http = new HttpClient(new StubHandler(c.GetProperty("status").GetInt32(), c.GetProperty("response").GetString()!));
                ITranslator t = provider == "claude"
                    ? new ClaudeTranslator("claude-haiku-5-5", () => ApiKey, http: http)
                    : new GeminiTranslator("gemini-flash-lite-latest", () => ApiKey, http: http);
                if (c.TryGetProperty("error", out var error))
                {
                    var ex = await Assert.ThrowsAsync<TranslationException>(() => t.TranslateAsync(Request()));
                    Assert.Equal(error.GetString(), ex.Describe());
                }
                else
                {
                    Assert.Equal(c.GetProperty("result").GetString(), await t.TranslateAsync(Request()));
                }
            }
        }
    }

    [Fact]
    public async Task MissingKey()
    {
        var mk = _v.GetProperty("missingKey");
        var claude = await Assert.ThrowsAsync<TranslationException>(() => new ClaudeTranslator("m", () => null).TranslateAsync(Request()));
        var gemini = await Assert.ThrowsAsync<TranslationException>(() => new GeminiTranslator("m", () => "").TranslateAsync(Request()));
        Assert.Equal(mk.GetProperty("claude").GetString(), claude.Describe());
        Assert.Equal(mk.GetProperty("gemini").GetString(), gemini.Describe());
    }

    [Fact]
    public void EngineLabels()
    {
        var l = _v.GetProperty("engineLabels");
        Assert.Equal(l.GetProperty("apple").GetString(), TranslationEngine.Apple.Label);
        Assert.Equal(l.GetProperty("claude").GetString(), TranslationEngine.Claude("m").Label);
        Assert.Equal(l.GetProperty("gemini").GetString(), TranslationEngine.Gemini("m").Label);
        Assert.Equal(l.GetProperty("original").GetString(), TranslationEngine.Original.Label);
    }
}
