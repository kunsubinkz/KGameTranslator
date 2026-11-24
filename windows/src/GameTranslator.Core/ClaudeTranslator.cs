using System.Text.Json.Nodes;

namespace GameTranslator.Core;

/// <summary>Gọi Claude (Anthropic Messages API) qua REST. API key lấy qua hàm để đọc từ Credential Manager mỗi lần gọi.</summary>
public sealed class ClaudeTranslator(string model, Func<string?> apiKey, int maxOutputTokens = 1024,
    double timeoutSeconds = 8, HttpClient? http = null)
    : HttpTranslatorBase(apiKey, http, TimeSpan.FromSeconds(timeoutSeconds))
{
    public string Model { get; } = model;
    public override string Id => "claude:" + Model;

    internal override HttpRequestMessage MakeRequest(TranslationRequest request, string apiKey)
    {
        // Không gửi temperature/top_p (các model 5.x từ chối giá trị khác mặc định).
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = maxOutputTokens,
            ["system"] = PromptBuilder.SystemInstruction(request.Glossary) + PromptBuilder.ParagraphNote(request.Text),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = PromptBuilder.UserContent(request),
            }),
        };
        // Giảm suy nghĩ để dịch nhanh: Haiku 5.5 tắt hẳn được, Sonnet 5.5 chỉ có "between_tools",
        // Opus 5.5 không tắt được nên hạ effort xuống thấp.
        var name = Model.ToLowerInvariant();
        if (name.Contains("haiku")) body["thinking"] = new JsonObject { ["type"] = "disabled" };
        else if (name.Contains("sonnet-5-5")) body["thinking"] = new JsonObject { ["type"] = "between_tools" };
        else if (name.Contains("opus-5-5")) body["output_config"] = new JsonObject { ["effort"] = "low" };

        var message = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent(body),
        };
        message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return message;
    }

    internal override TranslationException? MapStatus(int status, string body) => status switch
    {
        429 => new TranslationException(TranslationErrorKind.QuotaExceeded),
        401 or 403 => new TranslationException(TranslationErrorKind.Network, $"API key Claude không hợp lệ (HTTP {status})"),
        // Hết credit trả về 400 kèm "credit balance"; coi như hết hạn mức để app tự lùi về offline.
        400 when body.Contains("credit balance", StringComparison.Ordinal) => new TranslationException(TranslationErrorKind.QuotaExceeded),
        _ => new TranslationException(TranslationErrorKind.Network, $"HTTP {status}"),
    };

    /// <summary>Lấy văn bản từ phản hồi Messages API.</summary>
    internal override string Parse(string body) => ParseResponse(body);

    public static string ParseResponse(string body)
    {
        var root = ParseObject(body) ?? throw new TranslationException(TranslationErrorKind.EmptyResponse);
        if (root["stop_reason"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            && root["stop_reason"]!.GetValue<string>() == "refusal")
            throw new TranslationException(TranslationErrorKind.Blocked);
        var blocks = root["content"] as JsonArray ?? [];
        var text = JoinTexts(blocks
            .OfType<JsonObject>()
            .Where(b => b["type"]?.GetValueKind() == System.Text.Json.JsonValueKind.String && b["type"]!.GetValue<string>() == "text")
            .Select(b => b["text"]));
        if (text.Length == 0) throw new TranslationException(TranslationErrorKind.EmptyResponse);
        return text;
    }
}
