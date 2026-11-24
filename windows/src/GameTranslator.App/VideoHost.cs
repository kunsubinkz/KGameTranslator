using Avalonia.Controls;
using Avalonia.Platform;
using GameTranslator.Platform.Windows;

namespace GameTranslator.App;

/// <summary>Cửa sổ con Win32 chứa swap chain Direct3D 11 (xem PLAN.md mục 2: tránh chép khung về CPU/Skia).
/// Phụ đề và khung vùng được vẽ trong swap chain nên không bị lỗi "airspace".</summary>
public sealed class VideoHost : NativeControlHost
{
    private Win32Window? _window;

    public event Action<nint>? HandleReady;
    public event Action? SurfaceResized;
    /// <summary>(loại, x, y) theo pixel của cửa sổ con.</summary>
    public event Action<string, int, int>? SurfaceMouse;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _window = Win32Window.Create(parent.Handle);
        _window.SizeChanged += () => SurfaceResized?.Invoke();
        _window.Mouse += (k, x, y) => SurfaceMouse?.Invoke(k, x, y);
        // Báo sau khi control đã gắn xong để renderer đọc được kích thước thật.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleReady?.Invoke(_window.Handle));
        return new PlatformHandle(_window.Handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _window?.Dispose();
        _window = null;
    }
}
