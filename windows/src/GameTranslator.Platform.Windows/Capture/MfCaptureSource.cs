using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace GameTranslator.Platform.Windows.Capture;


public sealed record CaptureInfo(NativeFormat Format, string DeviceName, string Path = "");

/// <summary>Thu hình bằng Media Foundation Source Reader trên một luồng riêng (MMCSS), vẽ ngay khi khung đến.</summary>
public sealed class MfCaptureSource : IDisposable
{
    private Thread? _thread;
    private volatile bool _stop;
    private IMFMediaSource? _source;
    private IMFSourceReader? _reader;

    public event Action<string>? StatusChanged;
    public event Action<CaptureInfo>? Started;
    public event Action? Stopped;

    /// <summary>FPS thực nhận, cập nhật mỗi 2 giây.</summary>
    public double MeasuredFps { get; private set; }
    public bool IsRunning => _thread is { IsAlive: true };

    public static IReadOnlyList<NativeFormat> ListFormats(CaptureDevice device)
    {
        DeviceDiscovery.EnsureMediaFoundation();
        try
        {
            using var src = ActivateSource(device);
            using var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(src, null!);
            return EnumerateNative(reader);
        }
        catch (Exception) { return []; }
    }

    private static IMFMediaSource ActivateSource(CaptureDevice device)
    {
        using var attrs = MediaFactory.MFCreateAttributes(2);
        attrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        attrs.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, device.Id);
        return MediaFactory.MFCreateDeviceSource(attrs);
    }

    private static List<NativeFormat> EnumerateNative(IMFSourceReader reader)
    {
        var list = new List<NativeFormat>();
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, i); }
            catch (Exception) { break; }
            using (type)
            {
                ulong size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                ulong rate = type.GetUInt64(MediaTypeAttributeKeys.FrameRate);
                var sub = type.GetGUID(MediaTypeAttributeKeys.Subtype);
                int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
                uint num = (uint)(rate >> 32), den = (uint)(rate & 0xFFFFFFFF);
                double fps = den == 0 ? 0 : (double)num / den;
                var name = SubtypeName(sub);
                list.Add(new NativeFormat(i, w, h, fps, name, MediaTypeSelector.IsRaw(name)));
            }
        }
        return list;
    }

    private static string SubtypeName(Guid g)
    {
        // Subtype dạng FOURCC: Data1 là bốn ký tự.
        if (g.ToString().EndsWith("-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = BitConverter.GetBytes(g.ToByteArray().Length >= 4 ? BitConverter.ToUInt32(g.ToByteArray(), 0) : 0);
            if (bytes.All(b => b is >= 32 and < 127)) return System.Text.Encoding.ASCII.GetString(bytes);
            return (BitConverter.ToUInt32(g.ToByteArray(), 0)) switch
            {
                20 => "RGB24",
                22 => "RGB32",
                21 => "ARGB32",
                _ => "RGB",
            };
        }
        return g.ToString();
    }

    public void Start(CaptureDevice device, IFrameSink sink, int? formatIndex = null)
    {
        Stop();
        _stop = false;
        _thread = new Thread(() => Run(device, sink, formatIndex)) { IsBackground = true, Name = "Capture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        var t = _thread;
        if (t is not null && t != Thread.CurrentThread) t.Join(2000);
        _thread = null;
    }


    /// <summary>Mở source + reader và chọn định dạng. `manager` khác null = cho bộ giải mã dùng GPU (DXVA), xuất NV12.
    /// Trả về định dạng đã chọn (luôn nằm trong danh sách thiết bị báo).</summary>
    private NativeFormat? OpenReader(CaptureDevice device, int? formatIndex, IMFDXGIDeviceManager? manager)
    {
        CloseReader();
        _source = ActivateSource(device);
        using var attrs = MediaFactory.MFCreateAttributes(4);
        attrs.Set(new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee"), 1u); // MF_LOW_LATENCY
        if (manager is not null)
        {
            attrs.Set(SourceReaderAttributeKeys.D3DManager, manager);
            attrs.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
        }
        else
        {
            attrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
            attrs.Set(SourceReaderAttributeKeys.DisableDxva, 1u);
        }
        _reader = MediaFactory.MFCreateSourceReaderFromMediaSource(_source, attrs);

        var formats = EnumerateNative(_reader);
        var chosen = formatIndex is { } fi ? formats.FirstOrDefault(f => f.Index == fi) : MediaTypeSelector.Choose(formats);
        if (chosen is null) return null;

        // Định dạng gốc (có trong danh sách thiết bị) trước, rồi mới xin định dạng ra.
        using (var native = _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, chosen.Index))
            _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, native);

        // NV12/YUY2 thô: giữ nguyên, GPU đổi màu bằng shader. MJPEG/H.264: GPU giải mã ra NV12, hoặc CPU giải mã ra RGB32.
        if (chosen.SubtypeName is not ("NV12" or "YUY2"))
        {
            using var output = MediaFactory.MFCreateMediaType();
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, manager is not null ? VideoFormatGuids.NV12 : VideoFormatGuids.Rgb32);
            _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, output);
        }
        return chosen;
    }

    private void CloseReader()
    {
        try { _reader?.Dispose(); } catch (Exception) { }
        try { _source?.Shutdown(); _source?.Dispose(); } catch (Exception) { }
        _reader = null;
        _source = null;
    }
    private unsafe void Run(CaptureDevice device, IFrameSink sink, int? formatIndex)
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        using var mmcss = Mmcss.Enter("Capture");
        try
        {
            DeviceDiscovery.EnsureMediaFoundation();
            var gpuSink = sink as IGpuFrameSink;
            var manager = gpuSink?.DeviceManager;

            // Thử đường giải mã phần cứng trước (MJPEG/H.264 → texture NV12 trên GPU); lỗi thì quay về đường CPU.
            NativeFormat? chosen = null;
            bool hardware = false;
            if (manager is not null)
            {
                try { chosen = OpenReader(device, formatIndex, manager); hardware = chosen is not null; }
                catch (Exception) { CloseReader(); }
            }
            if (!hardware) chosen = OpenReader(device, formatIndex, null);
            if (chosen is null || _reader is null)
            {
                StatusChanged?.Invoke("Thiết bị không báo định dạng hình nào.");
                return;
            }
            bool direct = chosen.SubtypeName is "NV12" or "YUY2";

            using var current = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            ulong size = current.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            int width = (int)(size >> 32), height = (int)(size & 0xFFFFFFFF);
            var outSub = current.GetGUID(MediaTypeAttributeKeys.Subtype);
            int defaultStride = 0;
            try { defaultStride = (int)current.GetUInt32(MediaTypeAttributeKeys.DefaultStride); } catch (Exception) { }
            var fmt = outSub == VideoFormatGuids.NV12 ? FrameFormat.Nv12 : outSub == VideoFormatGuids.YUY2 ? FrameFormat.Yuy2 : FrameFormat.Bgra;
            bool bottomUp = fmt == FrameFormat.Bgra && defaultStride < 0;
            int bytesPerPixel = fmt switch { FrameFormat.Bgra => 4, FrameFormat.Yuy2 => 2, _ => 1 };
            int stride = defaultStride > 0 ? defaultStride : width * bytesPerPixel;

            string path = direct ? "thô" : hardware ? chosen.SubtypeName + ", GPU giải mã" : chosen.SubtypeName + ", CPU giải mã";
            Started?.Invoke(new CaptureInfo(chosen with { Width = width, Height = height }, device.Name, path));
            StatusChanged?.Invoke("");

            byte[]? flipped = bottomUp ? new byte[width * height * 4] : null;
            var clock = Stopwatch.StartNew();
            long count = 0, windowStart = 0;
            var texGuid = typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID;
            while (!_stop)
            {
                var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out _);
                if ((flags & SourceReaderFlag.EndOfStream) != 0) { StatusChanged?.Invoke("Thiết bị ngừng gửi hình."); break; }
                if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
                {
                    using var t = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                    ulong s = t.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                    int nw = (int)(s >> 32), nh = (int)(s & 0xFFFFFFFF);
                    if (nw != width || nh != height)
                    {
                        width = nw; height = nh;
                        stride = width * bytesPerPixel;
                        if (bottomUp) flipped = new byte[width * height * 4];
                    }
                }
                if (sample is null) continue;
                using (sample)
                {
                    using var buffer = sample.GetBufferByIndex(0);
                    // Khung nằm trên GPU (bộ giải mã phần cứng): chép GPU sang GPU, không qua CPU.
                    using var dxgi = gpuSink is null ? null : buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
                    if (dxgi is not null)
                    {
                        var res = dxgi.GetResource(texGuid);
                        using var tex = new Vortice.Direct3D11.ID3D11Texture2D(res);
                        gpuSink!.OnGpuFrame(tex, dxgi.SubresourceIndex, width, height);
                    }
                    else
                    {
                        using var contiguous = sample.ConvertToContiguousBuffer();
                        contiguous.Lock(out var ptr, out _, out var len);
                        try
                        {
                            int need = fmt == FrameFormat.Nv12 ? stride * height * 3 / 2 : stride * height;
                            if (len >= need)
                            {
                                if (bottomUp)
                                {
                                    for (int y = 0; y < height; y++)
                                        Marshal.Copy(ptr + (height - 1 - y) * stride, flipped!, y * stride, stride);
                                    sink.OnFrame(fmt, flipped!, width, height, stride);
                                }
                                else
                                {
                                    // Giao thẳng bộ nhớ của Media Foundation cho renderer (nó chép vào bộ đệm riêng rồi trả về ngay).
                                    sink.OnFrame(fmt, new ReadOnlySpan<byte>((void*)ptr, len), width, height, stride);
                                }
                            }
                        }
                        finally { contiguous.Unlock(); }
                    }
                }

                count++;
                var ms = clock.ElapsedMilliseconds;
                if (ms - windowStart >= 2000)
                {
                    MeasuredFps = count * 1000.0 / (ms - windowStart);
                    count = 0;
                    windowStart = ms;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            StatusChanged?.Invoke("Windows chặn camera. Bật Settings > Privacy & security > Camera > \"Let desktop apps access your camera\".");
        }
        catch (COMException ex)
        {
            StatusChanged?.Invoke($"Lỗi capture (0x{ex.HResult:X8}): {ex.Message}");
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            StatusChanged?.Invoke(ex.ResultCode.Code == unchecked((int)0x80070005)
                ? "Windows chặn camera. Bật Settings > Privacy & security > Camera > \"Let desktop apps access your camera\"."
                : $"Lỗi capture (0x{ex.ResultCode.Code:X8}): {ex.Message}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Lỗi capture: " + ex.Message);
        }
        finally
        {
            try { _reader?.Dispose(); } catch (Exception) { }
            try { _source?.Shutdown(); _source?.Dispose(); } catch (Exception) { }
            _reader = null;
            _source = null;
            MeasuredFps = 0;
            Stopped?.Invoke();
        }
    }

    public void Dispose() => Stop();
}
