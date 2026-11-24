using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTranslator.Core;

/// <summary>Vùng hộp thoại theo tọa độ tương đối 0...1, gốc ở góc trên trái.</summary>
public sealed record Region(double X, double Y, double Width, double Height)
{
    public Region Clamped()
    {
        double nx = Math.Min(Math.Max(X, 0), 0.99);
        double ny = Math.Min(Math.Max(Y, 0), 0.99);
        double nw = Math.Min(Math.Max(Width, 0.01), 1 - nx);
        double nh = Math.Min(Math.Max(Height, 0.01), 1 - ny);
        return new Region(nx, ny, nw, nh);
    }
}

public enum OcrLevel
{
    [JsonStringEnumMemberName("accurate")] Accurate,
    [JsonStringEnumMemberName("fast")] Fast,
}

public sealed class OcrSettings
{
    public OcrLevel Level { get; set; } = OcrLevel.Accurate;
    public double Scale { get; set; } = 2.0;
    public bool Invert { get; set; }
    public double Contrast { get; set; } = 1.2;
    public double MinimumTextHeight { get; set; } = 0.03;
}

public sealed class DetectorSettings
{
    public int SampleEveryNFrames { get; set; } = 15;
    public double ChangeThreshold { get; set; } = 2.0;
    public double StableMs { get; set; } = 350;
    public double HideDelayMs { get; set; } = 800;
}

public enum TranslateProvider
{
    [JsonStringEnumMemberName("claude")] Claude,
    [JsonStringEnumMemberName("gemini")] Gemini,
}

public static class TranslateProviderInfo
{
    public static string Title(this TranslateProvider p) => p == TranslateProvider.Claude ? "Claude (Anthropic)" : "Gemini (Google)";

    public static string KeyPlaceholder(this TranslateProvider p) =>
        p == TranslateProvider.Claude ? "Dán API key từ console.anthropic.com" : "Dán API key từ Google AI Studio";

    /// <summary>Model gợi ý cho dropdown; người dùng vẫn nhập tên khác được.</summary>
    public static IReadOnlyList<(string Id, string Title)> Presets(this TranslateProvider p) => p switch
    {
        TranslateProvider.Claude =>
        [
            ("claude-haiku-5-5", "Claude Haiku 5.5 · nhanh, rẻ nhất"),
            ("claude-sonnet-5-5", "Claude Sonnet 5.5 · cân bằng"),
            ("claude-opus-5-5", "Claude Opus 5.5 · chất lượng cao nhất"),
        ],
        _ =>
        [
            ("gemini-flash-lite-latest", "Gemini Flash-Lite · nhanh, rẻ"),
            ("gemini-flash-latest", "Gemini Flash · cân bằng"),
            ("gemini-pro-latest", "Gemini Pro · chất lượng cao"),
        ],
    };
}

public enum TranslateMode
{
    [JsonStringEnumMemberName("geminiOnly")] OnlineOnly,
    [JsonStringEnumMemberName("offlineOnly")] OfflineOnly,
    [JsonStringEnumMemberName("geminiWithOfflineFallback")] OnlineWithOfflineFallback,
}

public static class TranslateModeInfo
{
    public static string Title(this TranslateMode m) => m switch
    {
        TranslateMode.OnlineOnly => "Chỉ online (AI)",
        TranslateMode.OfflineOnly => "Chỉ offline",
        _ => "Online, dự phòng offline",
    };
}

public sealed class TranslateSettings : IJsonOnDeserialized
{
    public TranslateMode Mode { get; set; } = TranslateMode.OnlineWithOfflineFallback;
    public TranslateProvider Provider { get; set; } = TranslateProvider.Claude;
    /// <summary>Model Claude.</summary>
    public string Model { get; set; } = "claude-haiku-5-5";
    public string GeminiModel { get; set; } = "gemini-flash-lite-latest";
    public int ContextLines { get; set; } = 3;
    public int RequestsPerMinute { get; set; } = 12;
    public int RequestsPerDay { get; set; } = 450;
    /// <summary>Thay bản dịch offline bằng bản online khi còn hạn mức.</summary>
    public bool UpgradeOffline { get; set; } = true;

    [JsonIgnore]
    public string ActiveModel => Provider == TranslateProvider.Claude ? Model : GeminiModel;

    void IJsonOnDeserialized.OnDeserialized()
    {
        // Hồ sơ cũ lưu tên model Gemini trong `model`: tách sang `geminiModel`.
        if (Model.StartsWith("gemini", StringComparison.Ordinal))
        {
            GeminiModel = Model;
            Model = new TranslateSettings().Model;
        }
    }
}

public enum SubtitlePosition
{
    [JsonStringEnumMemberName("above")] Above,
    [JsonStringEnumMemberName("below")] Below,
    [JsonStringEnumMemberName("over")] Over,
    [JsonStringEnumMemberName("free")] Free,
}

public enum SubtitleAlignment
{
    [JsonStringEnumMemberName("left")] Left,
    [JsonStringEnumMemberName("center")] Center,
    [JsonStringEnumMemberName("right")] Right,
    [JsonStringEnumMemberName("justified")] Justified,
}

public sealed class SubtitleSettings : IJsonOnDeserialized
{
    public SubtitlePosition Position { get; set; } = SubtitlePosition.Above;
    public double FontSize { get; set; } = 28;
    public double FreeX { get; set; } = 0.5;
    public double FreeY { get; set; } = 0.85;
    /// <summary>Độ đậm của nền đen sau phụ đề, 0...1.</summary>
    public double BackgroundOpacity { get; set; } = 0.6;
    public SubtitleAlignment Alignment { get; set; } = SubtitleAlignment.Center;
    /// <summary>Số dòng tối đa trước khi cắt bớt.</summary>
    public int MaxLines { get; set; } = 12;

    void IJsonOnDeserialized.OnDeserialized() => MaxLines = Math.Min(Math.Max(MaxLines, 1), 30);
}

public sealed class Profile : IJsonOnDeserialized
{
    public required string Id { get; set; }
    public string? Name { get; set; }
    public Region? Region { get; set; }
    /// <summary>Vùng phụ (vd. ô tên nhân vật): chữ của mọi vùng được ghép lại rồi dịch chung.</summary>
    public List<Region> ExtraRegions { get; set; } = [];
    public OcrSettings Ocr { get; set; } = new();
    public DetectorSettings Detector { get; set; } = new();
    public TranslateSettings Translate { get; set; } = new();
    public SubtitleSettings Subtitle { get; set; } = new();
    public Dictionary<string, string> Glossary { get; set; } = new(StringComparer.Ordinal);

    public Profile() { }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public Profile(string id, string name, Region region)
    {
        Id = id;
        Name = name;
        Region = region;
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        Name ??= Id;
        Region ??= Default.Region!;
        ExtraRegions ??= [];
        Ocr ??= new();
        Detector ??= new();
        Translate ??= new();
        Subtitle ??= new();
        Glossary = Glossary is null ? new(StringComparer.Ordinal) : new(Glossary, StringComparer.Ordinal);
    }

    [JsonIgnore] public string DisplayName => Name ?? Id;
    [JsonIgnore] public Region MainRegion => Region ?? Default.Region!;

    /// <summary>Vùng chính rồi tới các vùng phụ.</summary>
    [JsonIgnore]
    public IReadOnlyList<Region> AllRegions => [MainRegion, .. ExtraRegions];

    /// <summary>Hình chữ nhật bao quanh mọi vùng, dùng để đặt phụ đề.</summary>
    [JsonIgnore]
    public Region UnionRegion
    {
        get
        {
            var all = AllRegions.Select(r => r.Clamped()).ToList();
            double minX = all.Min(r => r.X), minY = all.Min(r => r.Y);
            double maxX = all.Max(r => r.X + r.Width), maxY = all.Max(r => r.Y + r.Height);
            return new Region(minX, minY, maxX - minX, maxY - minY);
        }
    }

    public static Profile Default => new("default", "Mặc định", new Region(0.1, 0.72, 0.8, 0.22));

    public static Profile ZeldaTotk
    {
        get
        {
            var p = new Profile("zelda-totk", "Zelda: Tears of the Kingdom", new Region(0.18, 0.74, 0.64, 0.20));
            p.Glossary = new(StringComparer.Ordinal)
            {
                ["Link"] = "Link", ["Hyrule"] = "Hyrule", ["Rupee"] = "Rupee", ["Zelda"] = "Zelda",
            };
            return p;
        }
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Profile? FromJson(string json) => JsonSerializer.Deserialize<Profile>(json, JsonOptions);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
