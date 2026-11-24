namespace GameTranslator.Platform.Windows.Capture;

/// <summary>Một định dạng gốc do thiết bị liệt kê. Không bao giờ tạo định dạng không có trong danh sách.</summary>
public sealed record NativeFormat(int Index, int Width, int Height, double Fps, string SubtypeName, bool IsRaw)
{
    public string Label => $"{(Height >= 2160 ? "4K" : Height + "p")} · {Fps:0.#} fps · {(IsRaw ? "thô" : SubtypeName)}";
}

public static class MediaTypeSelector
{
    private static readonly HashSet<string> Compressed = new(StringComparer.OrdinalIgnoreCase)
    {
        "MJPG", "H264", "HEVC", "H265", "MP4V", "WMV3", "MPG1", "MPG2",
    };

    public static bool IsRaw(string subtype) => !Compressed.Contains(subtype);

    /// <summary>Ưu tiên 1920x1080, ≥ 60 fps, định dạng thô trước MJPEG/H.264 (phải giải mã nên trễ hơn).
    /// Chỉ chọn trong danh sách thiết bị báo; trả về null nếu danh sách rỗng.</summary>
    public static NativeFormat? Choose(IReadOnlyList<NativeFormat> formats, int targetHeight = 1080, double targetFps = 60)
    {
        if (formats.Count == 0) return null;
        return formats
            .OrderByDescending(f => f.Height == targetHeight && f.Width == targetHeight * 16 / 9)
            .ThenBy(f => Math.Abs(f.Height - targetHeight))
            .ThenByDescending(f => f.Fps >= targetFps - 0.5)
            .ThenByDescending(f => f.IsRaw)
            .ThenBy(f => Math.Abs(f.Fps - targetFps))
            .First();
    }
}
