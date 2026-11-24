namespace GameTranslator.Core;

public sealed record ContextPair(string Source, string Target);

public sealed record TranslationRequest(
    string Text,
    IReadOnlyList<ContextPair>? ContextPairs = null,
    IReadOnlyDictionary<string, string>? GlossaryMap = null)
{
    public IReadOnlyList<ContextPair> Context => ContextPairs ?? [];
    public IReadOnlyDictionary<string, string> Glossary => GlossaryMap ?? new Dictionary<string, string>();
}

public enum TranslationErrorKind
{
    MissingApiKey,
    /// <summary>HTTP 429 hoặc hết hạn mức nội bộ.</summary>
    QuotaExceeded,
    Network,
    EmptyResponse,
    Blocked,
    Unavailable,
    Timeout,
}

public sealed class TranslationException : Exception
{
    public TranslationErrorKind Kind { get; }

    public TranslationException(TranslationErrorKind kind, string? detail = null)
        : base(detail ?? kind.ToString())
    {
        Kind = kind;
    }

    /// <summary>Dạng chuỗi dùng trong test vector: "quotaExceeded", "network:HTTP 500"...</summary>
    public string Describe() => Kind switch
    {
        TranslationErrorKind.MissingApiKey => "missingAPIKey",
        TranslationErrorKind.QuotaExceeded => "quotaExceeded",
        TranslationErrorKind.Network => "network:" + Message,
        TranslationErrorKind.EmptyResponse => "emptyResponse",
        TranslationErrorKind.Blocked => "blocked",
        TranslationErrorKind.Unavailable => "unavailable:" + Message,
        TranslationErrorKind.Timeout => "timeout",
        _ => Kind.ToString(),
    };
}

public interface ITranslator
{
    string Id { get; }
    Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);
}

public enum EngineKind { Claude, Gemini, Apple, Original }

public sealed record TranslationEngine(EngineKind Kind, string Model = "")
{
    public static TranslationEngine Claude(string model) => new(EngineKind.Claude, model);
    public static TranslationEngine Gemini(string model) => new(EngineKind.Gemini, model);
    public static TranslationEngine Apple { get; } = new(EngineKind.Apple);
    /// <summary>Không dịch được, hiện chữ gốc.</summary>
    public static TranslationEngine Original { get; } = new(EngineKind.Original);

    public string Label => Kind switch
    {
        EngineKind.Claude => "claude:" + Model,
        EngineKind.Gemini => "gemini:" + Model,
        EngineKind.Apple => "apple",
        _ => "original",
    };
}

public sealed record TranslationOutcome(string Text, TranslationEngine Engine);
