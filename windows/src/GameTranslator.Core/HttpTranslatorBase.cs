using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameTranslator.Core;

/// <summary>Phần chung của Claude/Gemini: gửi request, ánh xạ lỗi, đọc văn bản.</summary>
public abstract class HttpTranslatorBase : ITranslator
{
    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;

    public abstract string Id { get; }

    protected HttpTranslatorBase(Func<string?> apiKey, HttpClient? http, TimeSpan timeout)
    {
        _apiKey = apiKey;
        _http = http ?? new HttpClient { Timeout = timeout };
    }

    internal abstract HttpRequestMessage MakeRequest(TranslationRequest request, string apiKey);

    /// <summary>Trả về lỗi tương ứng với mã HTTP lỗi, hoặc null nếu là phản hồi thành công.</summary>
    internal abstract TranslationException? MapStatus(int status, string body);

    internal abstract string Parse(string body);

    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        var key = _apiKey();
        if (string.IsNullOrEmpty(key)) throw new TranslationException(TranslationErrorKind.MissingApiKey);

        string body;
        int status;
        try
        {
            using var message = MakeRequest(request, key);
            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TranslationException(TranslationErrorKind.Timeout);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException(TranslationErrorKind.Network, ex.Message);
        }

        if (status is < 200 or >= 300) throw MapStatus(status, body)!;
        return Parse(body);
    }

    protected static string JoinTexts(IEnumerable<JsonNode?> texts) =>
        string.Concat(texts.Select(t => t?.GetValue<string>() ?? "")).Trim();

    protected static JsonObject? ParseObject(string body)
    {
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return null; }
    }

    protected static HttpContent JsonContent(JsonObject body) =>
        new StringContent(body.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), System.Text.Encoding.UTF8, "application/json");
}
