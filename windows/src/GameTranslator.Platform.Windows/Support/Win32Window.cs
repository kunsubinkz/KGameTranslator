using System.Runtime.InteropServices;

namespace GameTranslator.Platform.Windows;

/// <summary>Cửa sổ Win32 nhỏ dùng làm bề mặt cho swap chain (con của cửa sổ Avalonia, hoặc cửa sổ ẩn khi selftest).</summary>
public sealed partial class Win32Window : IDisposable
{
    private const string ClassName = "KGameTranslatorVideo";
    private const uint WM_SIZE = 0x0005, WM_ERASEBKGND = 0x0014, WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint CS_DBLCLKS = 0x0008;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string cls, string? title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);

    private static readonly WndProc ProcDelegate = StaticProc;
    private static bool _registered;
    private static readonly Dictionary<nint, Win32Window> Windows = [];

    public nint Handle { get; private set; }
    public event Action? SizeChanged;
    /// <summary>Chuột trong cửa sổ: (loại, x, y) với loại "down" | "move" | "up" | "dblclick".</summary>
    public event Action<string, int, int>? Mouse;

    public static Win32Window Create(nint parent, int width = 640, int height = 360)
    {
        EnsureClass();
        var w = new Win32Window();
        uint style = parent != 0 ? WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN : WS_POPUP;
        w.Handle = CreateWindowExW(0, ClassName, null, style, 0, 0, width, height, parent, 0, GetModuleHandleW(null), 0);
        if (w.Handle == 0) throw new InvalidOperationException("Không tạo được cửa sổ video: " + Marshal.GetLastWin32Error());
        lock (Windows) Windows[w.Handle] = w;
        return w;
    }

    private static void EnsureClass()
    {
        if (_registered) return;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_DBLCLKS,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ProcDelegate),
            hInstance = GetModuleHandleW(null),
            hCursor = LoadCursorW(0, 32512),
            lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) // 1410 = đã đăng ký
            throw new InvalidOperationException("RegisterClassEx lỗi " + Marshal.GetLastWin32Error());
        _registered = true;
    }

    private static nint StaticProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        Win32Window? w;
        lock (Windows) Windows.TryGetValue(hwnd, out w);
        switch (msg)
        {
            case WM_ERASEBKGND:
                return 1;
            case WM_SIZE:
                w?.SizeChanged?.Invoke();
                return 0;
            case WM_MOUSEACTIVATE:
                // Trả focus về cửa sổ chính để phím tắt hoạt động.
                SetFocus(GetAncestor(hwnd, 2));
                return 3; // MA_NOACTIVATE
            case WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP:
                PostMessageW(GetAncestor(hwnd, 2), msg, wParam, lParam);
                return 0;
            case WM_LBUTTONDOWN:
                SetCapture(hwnd);
                w?.Mouse?.Invoke("down", (short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case WM_MOUSEMOVE:
                w?.Mouse?.Invoke("move", (short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case WM_LBUTTONUP:
                ReleaseCapture();
                w?.Mouse?.Invoke("up", (short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case WM_LBUTTONDBLCLK:
                w?.Mouse?.Invoke("dblclick", (short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        lock (Windows) Windows.Remove(Handle);
        DestroyWindow(Handle);
        Handle = 0;
    }
}
