using System.Text.Json.Serialization;

namespace GameTranslator.Core.LocalHost;

/// <summary>Bản ghi JSON của giao thức host dịch cục bộ (xem tools/nllb/nllb_host.py).</summary>
public sealed record HostHealth(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("device")] string? Device,
    [property: JsonPropertyName("dtype")] string? Dtype,
    [property: JsonPropertyName("error")] string? Error)
{
    public bool Ready => State == "ready";
}

public sealed record HostTranslateRequest(
    [property: JsonPropertyName("texts")] IReadOnlyList<string> Texts,
    [property: JsonPropertyName("src")] string Src,
    [property: JsonPropertyName("tgt")] string Tgt,
    [property: JsonPropertyName("beams")] int Beams,
    [property: JsonPropertyName("maxNewTokens")] int MaxNewTokens);

public sealed record HostTranslateResponse(
    [property: JsonPropertyName("translations")] IReadOnlyList<string>? Translations,
    [property: JsonPropertyName("ms")] double Ms);

public sealed record HostLoadRequest(
    [property: JsonPropertyName("modelDir")] string ModelDir,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("dtype")] string Dtype);
