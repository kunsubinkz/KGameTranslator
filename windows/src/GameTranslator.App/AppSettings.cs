using System.Text.Json;
using System.Text.Json.Serialization;
using GameTranslator.Platform.Windows.Gpu;
using GameTranslator.Platform.Windows.Render;

namespace GameTranslator.App;

/// <summary>Thư mục dữ liệu: `%APPDATA%\GameTranslator` (hồ sơ), `%LOCALAPPDATA%\GameTranslator` (cache, log).
/// Bản portable (có file `portable.txt` cạnh exe) dùng thư mục `data\`.</summary>
public static class AppPaths
{
    public static readonly bool Portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    public static string Roaming => Portable
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameTranslator");

    public static string Local => Portable
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameTranslator");

    public static string Profiles => Path.Combine(Roaming, "Profiles");
    public static string Cache => Path.Combine(Local, "cache.sqlite");
    public static string Logs => Path.Combine(Local, "logs");
    public static string SettingsFile => Path.Combine(Roaming, "settings.json");
}

/// <summary>Cài đặt chung của app (không thuộc hồ sơ game).</summary>
public sealed class AppSettings
{
    public string? VideoDeviceId { get; set; }
    public string? VideoDeviceName { get; set; }
    /// <summary>Chỉ số định dạng gốc; null = tự chọn (1080p ≥ 60 fps, thô trước).</summary>
    public int? FormatIndex { get; set; }
    public string? AudioInputId { get; set; }
    public string? AudioOutputId { get; set; }
    public bool AudioEnabled { get; set; } = true;
    public double Volume { get; set; } = 1.0;
    public int AudioDelayMs { get; set; }
    public GpuMode Gpu { get; set; } = GpuMode.Auto;
    public string? GpuKey { get; set; }
    public ScalingMode Scaling { get; set; } = ScalingMode.Smooth4K;
    /// <summary>Khử răng cưa FXAA trước khi phóng (chế độ Mịn 4K).</summary>
    public bool AntiAlias { get; set; } = true;
    /// <summary>Độ nét RCAS của FSR tính bằng "stops": 0 = nét nhất, 2 = không làm nét. AMD khuyên 0.2.</summary>
    public double SharpenStops { get; set; } = 0.2;
    /// <summary>Bật: không xé hình. Độ trễ vẫn thấp vì luồng vẽ chờ swap chain rồi mới lấy khung mới nhất.</summary>
    public bool VSync { get; set; } = true;
    /// <summary>Bật dịch khi mở app (giống bản macOS, mặc định bật).</summary>
    public bool TranslateEnabled { get; set; } = true;
    /// <summary>Giải mã MJPEG/H.264 bằng GPU (DXVA). Tắt nếu card màn hình gặp lỗi.</summary>
    public bool HardwareDecode { get; set; } = true;
    /// <summary>Tăng khi đổi mặc định để áp lên cài đặt cũ.</summary>
    public int SettingsVersion { get; set; }
    public bool ShowRegionFrame { get; set; }
    public string ProfileId { get; set; } = "default";
    public string OcrEngine { get; set; } = "windows";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new();
                // v3: bật lại đồng bộ dọc (tắt thì thỉnh thoảng thấy dải xé ngang giữa màn hình) và bật dịch mặc định.
                if (loaded.SettingsVersion < 3) { loaded.VSync = true; loaded.TranslateEnabled = true; }
                // v4: "Mịn 4K" giờ là FSR 1 + FXAA; ai đang ở Mặc định (song tuyến, nhòe và răng cưa trên màn 4K) thì chuyển sang.
                if (loaded.SettingsVersion < 4 && loaded.Scaling == ScalingMode.Default) loaded.Scaling = ScalingMode.Smooth4K;
                loaded.SettingsVersion = 4;
                return loaded;
            }
        }
        catch (Exception) { }
        return new();
    }

    public void Save()
    {
        try
        {
            SettingsVersion = 4;
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception) { }
    }
}
