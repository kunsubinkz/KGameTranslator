using GameTranslator.Core;

namespace GameTranslator.Platform.Windows.Render;

public enum ScalingMode
{
    /// <summary>Song tuyến (nhẹ nhất).</summary>
    Default,
    /// <summary>Catmull-Rom + làm nét.</summary>
    Smooth4K,
    /// <summary>Nearest neighbor, ưu tiên hệ số nguyên.</summary>
    Pixel,
}

/// <summary>Mọi thứ vẽ đè lên video (Direct2D trong cùng swap chain). Bất biến; đặt cả khối mỗi lần đổi.</summary>
public sealed record OverlayState
{
    public string? SubtitleText { get; init; }
    public string? SourceText { get; init; }
    public bool ShowSource { get; init; }
    public string? SubtitleIcon { get; init; }
    public SubtitleSettings Subtitle { get; init; } = new();
    public Region Union { get; init; } = Profile.Default.Region!;
    public IReadOnlyList<Region> RegionFrames { get; init; } = [];
    public bool ShowRegionFrames { get; init; }
    public string? Badge { get; init; }
    public string? Message { get; init; }
}

public readonly record struct RenderStats(string GpuName, int Width, int Height, double InAppLatencyMs);
