namespace GameTranslator.Core.LocalHost;

public sealed record LocalModelInfo(string RepoId, string Title, long DownloadBytes, double VramGb, string Note)
{
    /// <summary>Tên thư mục lưu model: "facebook/nllb-200-distilled-600M" → "facebook--nllb-200-distilled-600M".</summary>
    public string FolderName => LocalModelCatalog.FolderName(RepoId);
}

/// <summary>Danh mục model NLLB-200. Dung lượng và VRAM là ước tính, chỉnh theo số đo thật.</summary>
public static class LocalModelCatalog
{
    public const string DefaultRepo = "facebook/nllb-200-distilled-600M";
    public const string SourceLanguage = "eng_Latn";
    public const string TargetLanguage = "vie_Latn";

    private const long GB = 1024L * 1024 * 1024;

    public static IReadOnlyList<LocalModelInfo> Models { get; } =
    [
        new(DefaultRepo, "NLLB-200 distilled 600M", (long)(2.5 * GB), 1.5, "Mặc định, nhanh nhất"),
        new("facebook/nllb-200-distilled-1.3B", "NLLB-200 distilled 1.3B", (long)(5.5 * GB), 3, "Dịch tốt hơn, chậm hơn"),
        new("facebook/nllb-200-1.3B", "NLLB-200 1.3B", (long)(5.5 * GB), 3, "Bản không chưng cất"),
        new("facebook/nllb-200-3.3B", "NLLB-200 3.3B", 17 * GB, 7, "Chỉ dùng nếu tốc độ chấp nhận được"),
    ];

    public static string FolderName(string repoId) => repoId.Replace("/", "--");

    public static LocalModelInfo? Find(string repoId) =>
        Models.FirstOrDefault(m => string.Equals(m.RepoId, repoId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Các file cần tải (không tải bản trọng số thừa).</summary>
    public static readonly string[] SmallFiles =
    [
        "config.json", "generation_config.json", "tokenizer.json", "tokenizer_config.json",
        "special_tokens_map.json", "sentencepiece.bpe.model",
    ];
}
