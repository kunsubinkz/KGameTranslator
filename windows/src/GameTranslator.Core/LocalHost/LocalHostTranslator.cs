using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GameTranslator.Core.LocalHost;

/// <summary>Dịch offline bằng host NLLB cục bộ (hoặc host ở máy khác) qua HTTP. Không dùng ngữ cảnh và từ điển game.</summary>
public sealed class LocalHostTranslator : ITranslator
{
    private readonly Func<string> _baseUrl;
    private readonly Func<string?> _token;
    private readonly Func<int> _beams;
    private readonly HttpClient _http;

    public string Id => "nllb";

    public LocalHostTranslator(Func<string> baseUrl, Func<string?> token, Func<int>? beams = null,
        HttpClient? http = null, int timeoutSeconds = 10)
    {
        _baseUrl = baseUrl;
        _token = token;
        _beams = beams ?? (() => 1);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        var sentences = SentenceSplitter.Split(request.Text);
        if (sentences.Count == 0) throw new TranslationException(TranslationErrorKind.EmptyResponse);

        var payload = new HostTranslateRequest(sentences, LocalModelCatalog.SourceLanguage,
            LocalModelCatalog.TargetLanguage, Math.Max(1, _beams()), 256);
        var response = await SendAsync(HttpMethod.Post, "/v1/translate", payload, cancellationToken).ConfigureAwait(false);
        var parsed = ParseTranslations(response);
        var text = string.Join(" ", parsed.Select(s => s.Trim()).Where(s => s.Length > 0));
        if (text.Length == 0) throw new TranslationException(TranslationErrorKind.EmptyResponse);
        return text;
    }

    /// <summary>Gọi /v1/health. Ném TranslationException khi không kết nối được hoặc sai token.</summary>
    public async Task<HostHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        var body = await SendAsync(HttpMethod.Get, "/v1/health", null, cancellationToken, requireReady: false).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<HostHealth>(body) ?? throw new TranslationException(TranslationErrorKind.EmptyResponse);
        }
        catch (JsonException)
        {
            throw new TranslationException(TranslationErrorKind.EmptyResponse);
        }
    }

    private static IReadOnlyList<string> ParseTranslations(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<HostTranslateResponse>(body)?.Translations ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object? payload, CancellationToken ct, bool requireReady = true)
    {
        var url = _baseUrl().TrimEnd('/') + path;
        try
        {
            using var message = new HttpRequestMessage(method, url);
            if (_token() is { Length: > 0 } token)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (payload is not null)
                message.Content = new StringContent(JsonSerializer.Serialize(payload, payload.GetType()), System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300) return body;
            throw status switch
            {
                (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden =>
                    new TranslationException(TranslationErrorKind.Unavailable, "sai token"),
                (int)HttpStatusCode.ServiceUnavailable =>
                    new TranslationException(TranslationErrorKind.Unavailable, "đang nạp model"),
                _ => new TranslationException(TranslationErrorKind.Network, $"HTTP {status}"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TranslationException(TranslationErrorKind.Timeout);
        }
        catch (HttpRequestException)
        {
            throw new TranslationException(TranslationErrorKind.Unavailable, "host chưa chạy");
        }
    }
}
