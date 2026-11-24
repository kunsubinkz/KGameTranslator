using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameTranslator.Core;

/// <summary>Gọi Gemini qua REST. API key lấy qua hàm để đọc từ Credential Manager mỗi lần gọi.</summary>
public sealed class GeminiTranslator(string model, Func<string?> apiKey, double temperature = 0.3,
    int maxOutputTokens = 300, double timeoutSeconds = 8, HttpClient? http = null)
    : HttpTranslatorBase(apiKey, http, TimeSpan.FromSeconds(timeoutSeconds))
{
    public string Model { get; } = model;
    public override string Id => "gemini:" + Model;

    internal override HttpRequestMessage MakeRequest(TranslationRequest request, string apiKey)
    {
        if (!Uri.TryCreate($"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent", UriKind.Absolute, out var url))
            throw new TranslationException(TranslationErrorKind.Unavailable, "Tên model không hợp lệ");
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = PromptBuilder.SystemInstruction(request.Glossary) + PromptBuilder.ParagraphNote(request.Text) }),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = PromptBuilder.UserContent(request) }),
            }),
            ["generationConfig"] = new JsonObject { ["temperature"] = temperature, ["maxOutputTokens"] = maxOutputTokens },
        };
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(body) };
        message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        return message;
    }

    internal override TranslationException? MapStatus(int status, string body) => status == 429
        ? new TranslationException(TranslationErrorKind.QuotaExceeded)
        : new TranslationException(TranslationErrorKind.Network, $"HTTP {status}");

    internal override string Parse(string body) => ParseResponse(body);

    /// <summary>Lấy văn bản từ phản hồi `generateContent`.</summary>
    public static string ParseResponse(string body)
    {
        var root = ParseObject(body) ?? throw new TranslationException(TranslationErrorKind.EmptyResponse);
        if (root["promptFeedback"] is JsonObject feedback && feedback["blockReason"] is not null)
            throw new TranslationException(TranslationErrorKind.Blocked);
        var candidate = (root["candidates"] as JsonArray)?.FirstOrDefault() as JsonObject
            ?? throw new TranslationException(TranslationErrorKind.EmptyResponse);
        if (candidate["finishReason"]?.GetValueKind() == JsonValueKind.String && candidate["finishReason"]!.GetValue<string>() == "SAFETY")
            throw new TranslationException(TranslationErrorKind.Blocked);
        var parts = (candidate["content"] as JsonObject)?["parts"] as JsonArray ?? [];
        var text = JoinTexts(parts.OfType<JsonObject>().Select(p => p["text"]));
        if (text.Length == 0) throw new TranslationException(TranslationErrorKind.EmptyResponse);
        return text;
    }
}
