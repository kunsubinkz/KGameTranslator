namespace GameTranslator.Platform.Windows.Capture;

/// <summary>Định dạng khung hình giao cho renderer. NV12/YUY2 được đổi sang RGB ngay trên GPU (nhẹ hơn 3 lần so với BGRA).</summary>
public enum FrameFormat
{
    /// <summary>BGRA 8 bit, hàng trên cùng trước.</summary>
    Bgra,
    /// <summary>Mặt phẳng Y (stride x height) rồi mặt phẳng UV xen kẽ (stride x height/2).</summary>
    Nv12,
    /// <summary>Gói Y0 U Y1 V, 2 byte mỗi điểm ảnh.</summary>
    Yuy2,
}

/// <summary>Nơi nhận khung hình. Được gọi trên luồng capture; không được giữ `data` sau khi trả về.</summary>
public interface IFrameSink
{
    void OnFrame(FrameFormat format, ReadOnlySpan<byte> data, int width, int height, int stride);
}

/// <summary>Nơi nhận khung đã nằm sẵn trên GPU (bộ giải mã phần cứng của Media Foundation dùng chung device của renderer).</summary>
public interface IGpuFrameSink : IFrameSink
{
    /// <summary>null = không dùng giải mã phần cứng (capture sẽ giải mã bằng CPU).</summary>
    Vortice.MediaFoundation.IMFDXGIDeviceManager? DeviceManager { get; }

    /// <summary>Texture NV12 (có thể là một phần tử của mảng texture). Không được giữ texture sau khi trả về.</summary>
    void OnGpuFrame(Vortice.Direct3D11.ID3D11Texture2D texture, uint subresource, int width, int height);
}
