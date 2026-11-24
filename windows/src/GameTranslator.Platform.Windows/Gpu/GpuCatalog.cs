using Vortice.DXGI;

namespace GameTranslator.Platform.Windows.Gpu;

/// <summary>Một card màn hình (bỏ adapter phần mềm).</summary>
public sealed record GpuInfo(
    string Name, uint VendorId, uint DeviceId, long DedicatedVideoMemory, long Luid,
    bool IsIntegrated, IReadOnlyList<string> Outputs, IReadOnlyList<RectI> OutputBounds, bool SupportsTearing)
{
    /// <summary>Khóa lưu cài đặt. LUID đổi mỗi lần khởi động nên không dùng.</summary>
    public string StableKey => $"{VendorId:X4}:{DeviceId:X4}:{Name}";

    public string Vendor => VendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x5143 => "Qualcomm",
        _ => "GPU",
    };

    public string MemoryText => IsIntegrated || DedicatedVideoMemory < 512L * 1024 * 1024
        ? "bộ nhớ dùng chung"
        : $"{DedicatedVideoMemory / (1024.0 * 1024 * 1024):0.#} GB";

    public string DisplayText =>
        $"{Name} · {MemoryText}" + (Outputs.Count > 0 ? $" · nối {string.Join(", ", Outputs)}" : " · không nối màn hình");
}

public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public enum GpuMode { Auto, HighPerformance, PowerSaving, Specific }

/// <summary>Liệt kê GPU bằng DXGI, theo thứ tự hiệu năng.</summary>
public static class GpuCatalog
{
    public static string? LastError { get; private set; }

    public static IReadOnlyList<GpuInfo> Enumerate()
    {
        var result = new List<GpuInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            bool tearing = false;
            using (var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
            {
                if (f5 is not null) tearing = f5.PresentAllowTearing;
            }
            using var f6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
            for (uint i = 0; ; i++)
            {
                IDXGIAdapter1? adapter;
                if (f6 is not null)
                {
                    if (f6.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out adapter).Failure) break;
                }
                else
                {
                    if (factory.EnumAdapters1(i, out adapter).Failure) break;
                }
                using (adapter!)
                {
                    var d = adapter!.Description1;
                    if ((d.Flags & AdapterFlags.Software) != 0) continue;
                    var names = new List<string>();
                    var bounds = new List<RectI>();
                    for (uint o = 0; adapter!.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            var od = output.Description;
                            var r = od.DesktopCoordinates;
                            names.Add(od.DeviceName.TrimStart('\\', '.'));
                            bounds.Add(new RectI(r.Left, r.Top, r.Right, r.Bottom));
                        }
                    }
                    long dedicated = (long)(ulong)d.DedicatedVideoMemory.Value;
                    long luid = ((long)d.Luid.HighPart << 32) | d.Luid.LowPart;
                    result.Add(new GpuInfo(d.Description, d.VendorId, d.DeviceId, dedicated, luid,
                        IsIntegrated: dedicated < 512L * 1024 * 1024, names, bounds, tearing));
                }
            }
        }
        catch (Exception ex) { LastError = ex.ToString(); }
        return result;
    }

    /// <summary>Chọn GPU theo tùy chọn. `windowCenter` là tọa độ tâm cửa sổ (pixel màn hình) cho chế độ Tự động.</summary>
    public static GpuInfo? Select(IReadOnlyList<GpuInfo> gpus, GpuMode preference, string? specificKey,
        (int X, int Y)? windowCenter)
    {
        if (gpus.Count == 0) return null;
        switch (preference)
        {
            case GpuMode.Specific:
                if (gpus.FirstOrDefault(g => g.StableKey == specificKey) is { } s) return s;
                break;
            case GpuMode.HighPerformance:
                return gpus.OrderByDescending(g => g.DedicatedVideoMemory).First();
            case GpuMode.PowerSaving:
                return gpus.FirstOrDefault(g => g.IsIntegrated) ?? gpus[^1];
        }
        // Tự động: GPU đang nối với màn hình chứa cửa sổ.
        if (windowCenter is { } c)
        {
            var host = gpus.FirstOrDefault(g => g.OutputBounds.Any(b => b.Contains(c.X, c.Y)));
            if (host is not null) return host;
        }
        return gpus.FirstOrDefault(g => g.Outputs.Count > 0) ?? gpus[0];
    }
}
