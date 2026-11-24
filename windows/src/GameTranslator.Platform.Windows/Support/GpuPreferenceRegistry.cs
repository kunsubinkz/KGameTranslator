using Microsoft.Win32;

namespace GameTranslator.Platform.Windows;

/// <summary>Ghi ưu tiên GPU của Windows cho exe này (HKCU, không cần admin). Chỉ gọi khi người dùng bấm nút trong app.</summary>
public static class GpuPreferenceRegistry
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <param name="preference">0 = để Windows quyết định, 1 = tiết kiệm điện, 2 = hiệu năng cao.</param>
    public static void Set(string exePath, int preference)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (preference == 0) key.DeleteValue(exePath, throwOnMissingValue: false);
        else key.SetValue(exePath, $"GpuPreference={preference};", RegistryValueKind.String);
    }

    public static int Get(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var v = key?.GetValue(exePath) as string;
        if (v is null) return 0;
        return v.Contains("GpuPreference=2") ? 2 : v.Contains("GpuPreference=1") ? 1 : 0;
    }
}
