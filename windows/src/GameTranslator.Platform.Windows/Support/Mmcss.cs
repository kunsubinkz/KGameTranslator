using System.Runtime.InteropServices;

namespace GameTranslator.Platform.Windows;

/// <summary>Đăng ký luồng với Multimedia Class Scheduler để không bị luồng khác chen.</summary>
public static partial class Mmcss
{
    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristics(string task, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(nint handle);

    public static IDisposable Enter(string task = "Games")
    {
        uint index = 0;
        var handle = AvSetMmThreadCharacteristics(task, ref index);
        return new Scope(handle);
    }

    private sealed class Scope(nint handle) : IDisposable
    {
        public void Dispose()
        {
            if (handle != 0) AvRevertMmThreadCharacteristics(handle);
        }
    }
}
