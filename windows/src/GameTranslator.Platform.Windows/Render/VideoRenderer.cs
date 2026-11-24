using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using GameTranslator.Core;
using GameTranslator.Core.Pipeline;
using GameTranslator.Platform.Windows.Capture;
using GameTranslator.Platform.Windows.Gpu;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DCommon;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DXGI.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using Filter = Vortice.Direct3D11.Filter;

namespace GameTranslator.Platform.Windows.Render;

/// <summary>Swap chain Direct3D 11 (flip model, độ trễ thấp) gắn vào một cửa sổ con Win32.
///
/// Độ trễ thấp nhờ mô hình "hộp thư": luồng capture chỉ chép khung vào bộ đệm rồi trả về ngay (không bao giờ chờ GPU/VSync,
/// nên hàng đợi của Media Foundation không phình ra). Một luồng vẽ riêng chờ swap chain sẵn sàng rồi vẽ KHUNG MỚI NHẤT,
/// khung cũ chưa kịp vẽ bị bỏ. NV12/YUY2 được tải thẳng lên GPU và đổi màu bằng shader.
/// Phụ đề, khung vùng và thẻ góc vẽ bằng Direct2D lên cùng swap chain.</summary>
public sealed class VideoRenderer : IFrameSink, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Vector2 SrcSize;
        public Vector2 DstSize;
        public int Mode;
        public float Sharp;
        public int Fmt;
        public int Hd;
        public Vector2 Offset;
        public Vector2 Pad;
    }

    private readonly object _sync = new();     // thiết bị GPU, swap chain, Direct2D
    private readonly object _bufLock = new();  // bộ đệm khung (giữ rất ngắn)
    private readonly nint _hwnd;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _disposed;

    private GpuInfo? _gpu;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDXGISwapChain1? _swap;
    private IDXGISwapChain2? _swap2;
    private nint _waitable;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps, _psEasu, _psRcas, _psFxaa;
    private ID3D11Texture2D? _fxaaTex;
    private ID3D11RenderTargetView? _fxaaRtv;
    private ID3D11ShaderResourceView? _fxaaSrv;
    private int _fxaaW, _fxaaH;
    private ID3D11Texture2D? _easuTex;
    private ID3D11RenderTargetView? _easuRtv;
    private ID3D11ShaderResourceView? _easuSrv;
    private int _easuW, _easuH;
    private ID3D11SamplerState? _linear, _point;
    private ID3D11Buffer? _cb;
    private ID3D11Texture2D? _texA, _texB;
    private ID3D11ShaderResourceView? _srvA, _srvB;
    private FrameFormat _texFmt;
    private int _texW, _texH;
    private bool _texIsGpu;
    private ID3D11Texture2D? _gpuTex, _staging;
    private bool _gpuFrame;
    private long _cpuSeq = -1;
    private Vortice.MediaFoundation.IMFDXGIDeviceManager? _mfManager;
    private int _backW, _backH;
    private bool _tearing;

    // Direct2D / DirectWrite
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2d;
    private ID2D1Bitmap1? _d2dTarget;
    private IDWriteFactory? _dwrite;

    // Bộ đệm khung (hai bộ đệm hoán đổi): luồng capture ghi vào bộ đệm rảnh rồi hoán đổi.
    private readonly byte[][] _bufs = [[], []];
    private int _readIdx, _writeIdx = 1;
    private FrameFormat _frameFmt;
    private int _frameW, _frameH, _frameStride;
    private long _frameSeq, _drawnSeq;
    private long _frameArrivalTicks;
    private volatile bool _dirty;

    private OverlayState _overlay = new();
    private string? _shownText;
    private long _textChangedTicks;
    private double _lastLatencyMs;
    private (float X, float Y, float W, float H) _videoRect;

    public ScalingMode Mode { get; set; } = ScalingMode.Default;
    public bool VSync { get; set; } = false;
    public GpuMode GpuPreference { get; set; } = GpuMode.Auto;
    public string? SpecificGpuKey { get; set; }
    /// <summary>Vẽ ngay trong OnFrame thay vì chờ luồng vẽ (dùng cho selftest/test).</summary>
    public bool SynchronousRender { get; set; }

    public event Action<string>? StatusChanged;

    public VideoRenderer(nint hwnd, GpuInfo? gpu)
    {
        _hwnd = hwnd;
        Initialize(gpu);
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Render", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public GpuInfo? Gpu => _gpu;
    public RenderStats Stats => new(_gpu?.Name ?? "", _frameW, _frameH, _lastLatencyMs);

    /// <summary>Vùng video đã trình chiếu trong cửa sổ (pixel), dùng cho chuột.</summary>
    public (float X, float Y, float W, float H) VideoRect => _videoRect;

    // ---------- Khởi tạo / dựng lại ----------

    private void Initialize(GpuInfo? gpu)
    {
        bool reinit = _device is not null;
        lock (_sync)
        {
            DisposeDeviceObjects();
            _gpu = gpu;
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            IDXGIAdapter1? adapter = null;
            if (gpu is not null)
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
                {
                    var d = a.Description1;
                    if (d.VendorId == gpu.VendorId && d.DeviceId == gpu.DeviceId && d.Description == gpu.Name) { adapter = a; break; }
                    a.Dispose();
                }
            }

            // VideoSupport: cho bộ giải mã phần cứng của Media Foundation dùng chung device này.
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            Result hr;
            ID3D11Device device;
            ID3D11DeviceContext ctx;
            if (adapter is not null)
            {
                hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, levels, out device!, out ctx!);
                adapter.Dispose();
            }
            else
            {
                hr = D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, flags, levels, out device!, out ctx!);
            }
            hr.CheckError();
            _device = device;
            _ctx = ctx;
            // Media Foundation gọi device từ luồng của nó: bật khóa đa luồng của D3D11.
            using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>()) mt?.SetMultithreadProtected(true);
            try
            {
                Vortice.MediaFoundation.MediaFactory.MFStartup();
                _mfManager ??= Vortice.MediaFoundation.MediaFactory.MFCreateDXGIDeviceManager();
                _mfManager.ResetDevice(_device).CheckError();
            }
            catch (Exception) { _mfManager = null; }

            _tearing = factory.QueryInterfaceOrNull<IDXGIFactory5>() is { } f5 && f5.PresentAllowTearing;

            var (w, h) = ClientSize();
            _backW = Math.Max(w, 1);
            _backH = Math.Max(h, 1);
            var desc = new SwapChainDescription1
            {
                Width = (uint)_backW,
                Height = (uint)_backH,
                Format = Format.B8G8R8A8_UNorm,
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                SampleDescription = new SampleDescription(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.FrameLatencyWaitableObject | (_tearing ? SwapChainFlags.AllowTearing : 0),
            };
            _swap = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
            _swap2 = _swap.QueryInterfaceOrNull<IDXGISwapChain2>();
            if (_swap2 is not null)
            {
                _swap2.MaximumFrameLatency = 1;
                _waitable = _swap2.FrameLatencyWaitableObject;
            }

            using (var dxgiDevice = _device.QueryInterface<IDXGIDevice1>()) dxgiDevice.MaximumFrameLatency = 1;

            CreateShaders();
            _linear = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
            _point = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
            _cb = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer, ResourceUsage.Default));

            CreateBackBufferViews();
            CreateD2D();
            _drawnSeq = -1; // vẽ lại khung hiện có trên thiết bị mới
            _dirty = true;
        }
        _wake.Set();
        if (reinit) DeviceRecreated?.Invoke();
    }

    private void CreateShaders()
    {
        var vsBlob = Compiler.Compile(Shaders.Source, "VS", "shader.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        var psBlob = Compiler.Compile(Shaders.Source, "PS", "shader.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        _vs = _device!.CreateVertexShader(vsBlob.Span);
        _ps = _device.CreatePixelShader(psBlob.Span);
        try
        {
            _psEasu = _device.CreatePixelShader(Compiler.Compile(Shaders.Source, "PSEasu", "shader.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
            _psRcas = _device.CreatePixelShader(Compiler.Compile(Shaders.Source, "PSRcas", "shader.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
            _psFxaa = _device.CreatePixelShader(Compiler.Compile(Shaders.Source, "PSFxaa", "shader.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        }
        catch (Exception ex) { _psEasu = _psRcas = _psFxaa = null; StatusChanged?.Invoke("Không dựng được FSR, dùng Catmull-Rom: " + ex.Message); }
    }

    private void CreateBackBufferViews()
    {
        using var back = _swap!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(back);
        if (_d2d is not null)
        {
            using var surface = back.QueryInterface<IDXGISurface>();
            _d2dTarget = _d2d.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
            _d2d.Target = _d2dTarget;
        }
    }

    private void CreateD2D()
    {
        _dwrite ??= DWrite.DWriteCreateFactory<IDWriteFactory>();
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2d = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        using var back = _swap!.GetBuffer<ID3D11Texture2D>(0);
        using var surface = back.QueryInterface<IDXGISurface>();
        _d2dTarget = _d2d.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        _d2d.Target = _d2dTarget;
    }

    private void ReleaseBackBufferViews()
    {
        if (_d2d is not null) _d2d.Target = null;
        _d2dTarget?.Dispose(); _d2dTarget = null;
        _rtv?.Dispose(); _rtv = null;
        _ctx?.ClearState();
        _ctx?.Flush();
    }

    private void DisposeDeviceObjects()
    {
        _d2dTarget?.Dispose(); _d2dTarget = null;
        _d2d?.Dispose(); _d2d = null;
        _d2dDevice?.Dispose(); _d2dDevice = null;
        _d2dFactory?.Dispose(); _d2dFactory = null;
        _srvA?.Dispose(); _srvA = null;
        _srvB?.Dispose(); _srvB = null;
        _texA?.Dispose(); _texA = null;
        _texB?.Dispose(); _texB = null;
        _gpuTex?.Dispose(); _gpuTex = null;
        _staging?.Dispose(); _staging = null;
        _texIsGpu = false; _gpuFrame = false; _cpuSeq = -1;
        _texW = _texH = 0;
        DisposeGpuTimer();
        _cb?.Dispose(); _cb = null;
        _point?.Dispose(); _point = null;
        _linear?.Dispose(); _linear = null;
        _ps?.Dispose(); _ps = null;
        _psEasu?.Dispose(); _psEasu = null;
        _psRcas?.Dispose(); _psRcas = null;
        _psFxaa?.Dispose(); _psFxaa = null;
        _fxaaSrv?.Dispose(); _fxaaSrv = null;
        _fxaaRtv?.Dispose(); _fxaaRtv = null;
        _fxaaTex?.Dispose(); _fxaaTex = null;
        _fxaaW = _fxaaH = 0;
        _easuSrv?.Dispose(); _easuSrv = null;
        _easuRtv?.Dispose(); _easuRtv = null;
        _easuTex?.Dispose(); _easuTex = null;
        _easuW = _easuH = 0;
        _vs?.Dispose(); _vs = null;
        _rtv?.Dispose(); _rtv = null;
        _swap2?.Dispose(); _swap2 = null;
        _waitable = 0;
        _swap?.Dispose(); _swap = null;
        _ctx?.Dispose(); _ctx = null;
        _device?.Dispose(); _device = null;
    }

    /// <summary>Đổi GPU khi đang chạy: dựng lại device và swap chain (khoảng nửa giây đen hình).</summary>
    public void SwitchGpu(GpuInfo? gpu)
    {
        if (gpu?.StableKey == _gpu?.StableKey && _device is not null) return;
        Initialize(gpu);
        StatusChanged?.Invoke($"Đã chuyển sang GPU: {gpu?.Name ?? "mặc định"}");
    }

    // ---------- Cửa sổ ----------

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint ms);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private (int, int) ClientSize()
    {
        GetClientRect(_hwnd, out var r);
        return (r.Right - r.Left, r.Bottom - r.Top);
    }

    public void Resize()
    {
        lock (_sync)
        {
            if (_swap is null) return;
            var (w, h) = ClientSize();
            w = Math.Max(w, 1); h = Math.Max(h, 1);
            if (w == _backW && h == _backH) return;
            ReleaseBackBufferViews();
            _swap.ResizeBuffers(0, (uint)w, (uint)h, Format.Unknown,
                SwapChainFlags.FrameLatencyWaitableObject | (_tearing ? SwapChainFlags.AllowTearing : 0)).CheckError();
            _backW = w; _backH = h;
            CreateBackBufferViews();
            _dirty = true;
        }
        _wake.Set();
    }

    // ---------- Lớp phủ ----------

    public void SetOverlay(OverlayState state)
    {
        lock (_bufLock)
        {
            if (state.SubtitleText != _shownText)
            {
                _shownText = state.SubtitleText;
                _textChangedTicks = _clock.ElapsedTicks;
            }
            _overlay = state;
        }
    }

    /// <summary>Vẽ lại khung cuối kèm lớp phủ mới (khi không có khung mới đến, ví dụ mất tín hiệu hoặc đang chọn vùng).</summary>
    public void Invalidate()
    {
        _dirty = true;
        _wake.Set();
    }

    // ---------- Nhận khung ----------

    public void OnFrame(FrameFormat format, ReadOnlySpan<byte> data, int width, int height, int stride)
    {
        long arrival = Stopwatch.GetTimestamp();
        int need = format switch
        {
            FrameFormat.Nv12 => stride * height * 3 / 2,
            _ => stride * height,
        };
        if (data.Length < need || width <= 0 || height <= 0) return;

        // Chép vào bộ đệm rảnh (ngoài khóa), rồi hoán đổi: luồng vẽ không bao giờ đọc bộ đệm đang ghi.
        int w = _writeIdx;
        var buf = _bufs[w];
        if (buf.Length < need) _bufs[w] = buf = new byte[need];
        if (format == FrameFormat.Nv12)
        {
            // Một số card đệm mặt phẳng Y lên bội số của 16 dòng (1080 → 1088): mặt phẳng UV khi đó bắt đầu sau 1088 dòng.
            // Đọc sai chỗ này làm màu lệch theo dải ngang, nên tính chiều cao thật từ kích thước bộ đệm.
            int planeH = height;
            int fullRows = data.Length / stride;
            if (fullRows * 2 % 3 == 0)
            {
                int alignedH = fullRows * 2 / 3;
                if (alignedH > height && alignedH - height < 64) planeH = alignedH;
            }
            data[..(stride * height)].CopyTo(buf);
            data.Slice(stride * planeH, stride * height / 2).CopyTo(buf.AsSpan(stride * height));
        }
        else data[..need].CopyTo(buf);
        lock (_bufLock)
        {
            (_readIdx, _writeIdx) = (w, _readIdx);
            _frameFmt = format; _frameW = width; _frameH = height; _frameStride = stride;
            _frameArrivalTicks = arrival;
            _gpuFrame = false;
            _frameSeq++;
        }
        if (SynchronousRender)
        {
            lock (_sync) { DrawIfNeeded(); }
        }
        else _wake.Set();
    }

    // ---------- Khung trên GPU (giải mã phần cứng) ----------

    /// <summary>Bộ quản lý thiết bị cho Media Foundation: bộ giải mã MJPEG/H.264 chạy trên chính GPU đang vẽ,
    /// khung ra là texture NV12 nằm sẵn trong VRAM (không qua CPU).</summary>
    public Vortice.MediaFoundation.IMFDXGIDeviceManager? DeviceManager => _mfManager;

    /// <summary>Đổi GPU hoặc dựng lại device: capture phải mở lại để bộ giải mã dùng device mới.</summary>
    public event Action? DeviceRecreated;

    /// <summary>Nhận khung NV12 do bộ giải mã phần cứng tạo ra. Chép GPU→GPU sang texture của renderer rồi trả về ngay.</summary>
    public void OnGpuFrame(ID3D11Texture2D source, uint subresource, int width, int height)
    {
        long arrival = Stopwatch.GetTimestamp();
        width &= ~1; height &= ~1;
        lock (_sync)
        {
            if (_device is null || _ctx is null) return;
            try
            {
                if (!_texIsGpu || _gpuTex is null || _texW != width || _texH != height)
                {
                    _srvA?.Dispose(); _srvB?.Dispose(); _texA?.Dispose(); _texB?.Dispose(); _gpuTex?.Dispose(); _staging?.Dispose();
                    _texA = _texB = null; _staging = null;
                    _gpuTex = _device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                    });
                    // Hai view trên cùng texture NV12: R8 = mặt phẳng Y, R8G8 = mặt phẳng UV (shader dùng chung với NV12 từ CPU).
                    _srvA = _device.CreateShaderResourceView(_gpuTex, new ShaderResourceViewDescription(_gpuTex, ShaderResourceViewDimension.Texture2D, Format.R8_UNorm));
                    _srvB = _device.CreateShaderResourceView(_gpuTex, new ShaderResourceViewDescription(_gpuTex, ShaderResourceViewDimension.Texture2D, Format.R8G8_UNorm));
                    _texIsGpu = true; _texFmt = FrameFormat.Nv12; _texW = width; _texH = height;
                }
                _ctx.CopySubresourceRegion(_gpuTex, 0, 0, 0, 0, source, subresource, new Box(0, 0, 0, width, height, 1));
            }
            catch (SharpGenException ex) when (IsDeviceLost(ex.ResultCode)) { RecoverFromDeviceLoss(); return; }
            lock (_bufLock)
            {
                _frameFmt = FrameFormat.Nv12; _frameW = width; _frameH = height; _frameStride = width;
                _frameArrivalTicks = arrival;
                _gpuFrame = true;
                _frameSeq++;
            }
            if (SynchronousRender) DrawIfNeeded();
        }
        if (!SynchronousRender) _wake.Set();
    }

    /// <summary>Khung đang nằm trên GPU: đọc về CPU (chỉ khi OCR hoặc chọn vùng cần, vài lần mỗi giây). Gọi khi giữ _sync và _bufLock.</summary>
    private void EnsureCpuCopy()
    {
        if (!_gpuFrame || _cpuSeq == _frameSeq || _gpuTex is null || _ctx is null) return;
        int w = _frameW, h = _frameH;
        _staging ??= _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        _ctx.CopyResource(_staging, _gpuTex);
        var map = _ctx.Map(_staging, 0, MapMode.Read);
        try
        {
            int need = w * h * 3 / 2;
            var buf = _bufs[_readIdx];
            if (buf.Length < need) _bufs[_readIdx] = buf = new byte[need];
            for (int y = 0; y < h; y++)
                Marshal.Copy(map.DataPointer + (nint)(y * (long)map.RowPitch), buf, y * w, w);
            nint uv = map.DataPointer + (nint)(h * (long)map.RowPitch);
            for (int y = 0; y < h / 2; y++)
                Marshal.Copy(uv + (nint)(y * (long)map.RowPitch), buf, w * h + y * w, w);
        }
        finally { _ctx.Unmap(_staging, 0); }
        _cpuSeq = _frameSeq;
    }

    /// <summary>Khóa cần để đọc khung về CPU: thêm _sync khi khung nằm trên GPU.</summary>
    private T WithCpuFrame<T>(Func<T> read)
    {
        if (!_gpuFrame)
        {
            // Khung CPU: chỉ cần khóa bộ đệm, không đụng tới luồng vẽ.
            lock (_bufLock)
                if (!_gpuFrame) return read();
        }
        lock (_sync)
        lock (_bufLock)
        {
            if (_gpuFrame)
            {
                try { EnsureCpuCopy(); }
                catch (Exception) { return default!; }
            }
            return read();
        }
    }

    /// <summary>Bản sao khung mới nhất đã đổi sang BGRA, dùng cho màn hình chọn vùng. null nếu chưa có hình.</summary>
    public (byte[] Pixels, int Width, int Height)? Snapshot() => WithCpuFrame(SnapshotCore);

    private (byte[] Pixels, int Width, int Height)? SnapshotCore()
    {
        {
            if (_frameW == 0) return null;
            var img = FrameConvert.CropToBgra(_frameFmt, _bufs[_readIdx], _frameW, _frameH, _frameStride, 0, 0, _frameW, _frameH);
            return (img.Pixels, _frameW, _frameH);
        }
    }

    /// <summary>Cắt các vùng (tọa độ tương đối) từ khung mới nhất để OCR.</summary>
    public IReadOnlyList<BgraImage>? CropRegions(IReadOnlyList<Region> regions) => WithCpuFrame(() => CropCore(regions));

    private IReadOnlyList<BgraImage>? CropCore(IReadOnlyList<Region> regions)
    {
        {
            if (_frameW == 0) return null;
            var list = new List<BgraImage>(regions.Count);
            foreach (var region in regions)
            {
                var r = region.Clamped();
                int x = Math.Min((int)(r.X * _frameW), _frameW - 1), y = Math.Min((int)(r.Y * _frameH), _frameH - 1);
                int w = Math.Clamp((int)Math.Round(r.Width * _frameW), 1, _frameW - x);
                int h = Math.Clamp((int)Math.Round(r.Height * _frameH), 1, _frameH - y);
                list.Add(FrameConvert.CropToBgra(_frameFmt, _bufs[_readIdx], _frameW, _frameH, _frameStride, x, y, w, h));
            }
            return list;
        }
    }

    // ---------- Luồng vẽ ----------

    private void RenderLoop()
    {
        using var mmcss = Mmcss.Enter("Games");
        while (!_disposed)
        {
            _wake.WaitOne(250);
            if (_disposed) break;
            // Có việc thì mới chờ swap chain sẵn sàng (chờ xong là phải Present, nếu không sẽ mất một lượt chờ).
            if (!HasWork()) continue;
            nint waitable = _waitable;
            if (waitable != 0) WaitForSingleObject(waitable, 100);
            lock (_sync)
            {
                try { DrawIfNeeded(); }
                catch (SharpGenException ex) when (IsDeviceLost(ex.ResultCode)) { RecoverFromDeviceLoss(); }
                catch (Exception ex) { StatusChanged?.Invoke("Lỗi vẽ: " + ex.Message); Thread.Sleep(200); }
            }
        }
    }

    private bool HasWork()
    {
        lock (_bufLock) return _frameW > 0 && (_frameSeq != _drawnSeq || _dirty);
    }

    private static bool IsDeviceLost(Result r) =>
        r.Code == unchecked((int)0x887A0005) || r.Code == unchecked((int)0x887A0007) || r.Code == unchecked((int)0x887A0020);

    private void RecoverFromDeviceLoss()
    {
        StatusChanged?.Invoke("Card màn hình bị reset (driver/TDR), đang dựng lại...");
        try
        {
            var gpus = GpuCatalog.Enumerate();
            var pick = GpuCatalog.Select(gpus, GpuPreference, SpecificGpuKey, null);
            Initialize(pick);
            StatusChanged?.Invoke($"Đã dựng lại trên {pick?.Name ?? "GPU mặc định"}.");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Không dựng lại được GPU: " + ex.Message);
        }
    }

    /// <summary>Gọi khi đang giữ _sync.</summary>
    private void DrawIfNeeded()
    {
        if (_device is null || _swap is null || _rtv is null) return;
        long arrival;
        lock (_bufLock)
        {
            if (_frameW == 0) return;
            if (_frameSeq == _drawnSeq && !_dirty) return;
            if (_frameSeq != _drawnSeq && !_gpuFrame) Upload();   // giữ _bufLock trong lúc đọc bộ đệm; khung GPU đã chép sẵn
            _drawnSeq = _frameSeq;
            _dirty = false;
            arrival = _frameArrivalTicks;
        }
        Draw();
        _lastLatencyMs = Stopwatch.GetElapsedTime(arrival).TotalMilliseconds;
    }

    private void Upload()
    {
        int w = _frameW, h = _frameH;
        var fmt = _frameFmt;
        if (_texA is null || _texIsGpu || _texFmt != fmt || _texW != w || _texH != h)
        {
            _srvA?.Dispose(); _srvB?.Dispose(); _texA?.Dispose(); _texB?.Dispose(); _gpuTex?.Dispose(); _staging?.Dispose();
            _srvB = null; _texB = null; _gpuTex = null; _staging = null; _texIsGpu = false;
            var (fa, wa, ha) = fmt switch
            {
                FrameFormat.Nv12 => (Format.R8_UNorm, w, h),
                FrameFormat.Yuy2 => (Format.R8G8B8A8_UNorm, w / 2, h),
                _ => (Format.B8G8R8A8_UNorm, w, h),
            };
            _texA = _device!.CreateTexture2D(DynamicTexture(fa, wa, ha));
            _srvA = _device.CreateShaderResourceView(_texA);
            if (fmt == FrameFormat.Nv12)
            {
                _texB = _device.CreateTexture2D(DynamicTexture(Format.R8G8_UNorm, w / 2, h / 2));
                _srvB = _device.CreateShaderResourceView(_texB);
            }
            _texFmt = fmt; _texW = w; _texH = h;
        }
        var src = _bufs[_readIdx];
        switch (fmt)
        {
            case FrameFormat.Nv12:
                CopyPlane(_texA!, src, 0, _frameStride, w, h);
                CopyPlane(_texB!, src, _frameStride * h, _frameStride, w, h / 2);
                break;
            case FrameFormat.Yuy2:
                CopyPlane(_texA!, src, 0, _frameStride, w * 2, h);
                break;
            default:
                CopyPlane(_texA!, src, 0, _frameStride, w * 4, h);
                break;
        }
    }

    private static Texture2DDescription DynamicTexture(Format format, int w, int h) => new()
    {
        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format,
        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Dynamic,
        BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write,
    };

    private void CopyPlane(ID3D11Texture2D tex, byte[] src, int offset, int srcStride, int rowBytes, int rows)
    {
        var map = _ctx!.Map(tex, 0, MapMode.WriteDiscard);
        try
        {
            unsafe
            {
                fixed (byte* p = src)
                {
                    for (int y = 0; y < rows; y++)
                        Buffer.MemoryCopy(p + offset + (long)y * srcStride, (byte*)map.DataPointer + (long)y * map.RowPitch, map.RowPitch, rowBytes);
                }
            }
        }
        finally { _ctx.Unmap(tex, 0); }
    }

    // ---------- Đọc lại ảnh đã vẽ (selftest) ----------

    /// <summary>Bật để lần vẽ kế tiếp lưu ảnh back buffer vào <see cref="LastRendered"/>.</summary>
    public bool CaptureRender { get; set; }
    public (byte[] Bgra, int Width, int Height)? LastRendered { get; private set; }

    private void ReadBackBuffer()
    {
        using var back = _swap!.GetBuffer<ID3D11Texture2D>(0);
        var d = back.Description;
        d.Usage = ResourceUsage.Staging;
        d.BindFlags = BindFlags.None;
        d.CPUAccessFlags = CpuAccessFlags.Read;
        d.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device!.CreateTexture2D(d);
        _ctx!.CopyResource(staging, back);
        var map = _ctx.Map(staging, 0, MapMode.Read);
        try
        {
            int w = (int)d.Width, h = (int)d.Height;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(map.DataPointer + (int)(y * map.RowPitch), px, y * w * 4, w * 4);
            LastRendered = (px, w, h);
        }
        finally { _ctx.Unmap(staging, 0); }
    }

    // ---------- Vẽ ----------

    /// <summary>Độ nét RCAS theo "stops" của FSR: 0 = nét nhất, 2 = gần như không làm nét. AMD khuyên 0.2.</summary>
    public float RcasStops { get; set; } = 0.2f;

    /// <summary>Khử răng cưa (FXAA) ở độ phân giải gốc trước khi phóng, chỉ dùng trong chế độ Mịn 4K.</summary>
    public bool AntiAlias { get; set; } = true;

    // ---------- Đo thời gian GPU (timestamp query, đọc trễ 4 khung để không bao giờ chờ GPU) ----------

    private const int QueryRing = 4;
    private ID3D11Query?[] _qDisjoint = new ID3D11Query?[QueryRing], _qStart = new ID3D11Query?[QueryRing], _qEnd = new ID3D11Query?[QueryRing];
    private int _qFrame;
    private bool[] _qPending = new bool[QueryRing];
    private double _gpuMsEma;
    private int _slowCount;
    private bool _fsrTooSlow;
    private ScalingMode _lastMode;

    /// <summary>Thời gian GPU cho phần đổi màu + phóng ảnh mỗi khung (ms, trung bình trượt).</summary>
    public double GpuMs => _gpuMsEma;
    public int TimerReads { get; private set; }
    public int TimerMisses { get; private set; }

    private void BeginGpuTimer()
    {
        int i = _qFrame % QueryRing;
        if (_qDisjoint[i] is null)
        {
            _qDisjoint[i] = _device!.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            _qStart[i] = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _qEnd[i] = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        }
        else if (_qPending[i])
        {
            // Kết quả của 4 khung trước: lấy nếu đã có, không chờ.
            var ctx = _ctx!;
            if (ctx.GetData(_qDisjoint[i]!, AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint dj) && !dj.Disjoint
                && ctx.GetData(_qStart[i]!, AsyncGetDataFlags.DoNotFlush, out ulong t0)
                && ctx.GetData(_qEnd[i]!, AsyncGetDataFlags.DoNotFlush, out ulong t1) && t1 > t0 && dj.Frequency > 0)
            {
                double ms = (t1 - t0) * 1000.0 / dj.Frequency;
                TimerReads++;
                _gpuMsEma = _gpuMsEma <= 0 ? ms : _gpuMsEma * 0.9 + ms * 0.1;
            }
            else TimerMisses++;
            _qPending[i] = false;
        }
        _ctx!.Begin(_qDisjoint[i]!);
        _ctx.End(_qStart[i]!);
    }

    private void EndGpuTimer()
    {
        int i = _qFrame % QueryRing;
        _ctx!.End(_qEnd[i]!);
        _ctx.End(_qDisjoint[i]!);
        _qPending[i] = true;
        _qFrame++;
    }

    private void DisposeGpuTimer()
    {
        for (int i = 0; i < QueryRing; i++)
        {
            _qDisjoint[i]?.Dispose(); _qStart[i]?.Dispose(); _qEnd[i]?.Dispose();
            _qDisjoint[i] = _qStart[i] = _qEnd[i] = null;
            _qPending[i] = false;
        }
        _gpuMsEma = 0; _slowCount = 0; _fsrTooSlow = false;
    }

    /// <summary>GPU yếu (ví dụ GPU tích hợp ở 4K): FSR tốn quá nhiều thời gian mỗi khung thì tạm dùng song tuyến để giữ 60 fps.</summary>
    private void CheckFsrBudget(bool fsrActive)
    {
        if (!fsrActive || _fsrTooSlow) { _slowCount = 0; return; }
        _slowCount = _gpuMsEma > 12 ? _slowCount + 1 : 0;
        if (_slowCount < 120) return;
        _fsrTooSlow = true;
        StatusChanged?.Invoke($"Card màn hình chưa đủ mạnh cho Mịn 4K ở cỡ này (~{_gpuMsEma:0} ms/khung), tạm dùng Mặc định để giữ 60 fps.");
    }

    private void EnsureFxaaTarget(int w, int h)
    {
        if (_fxaaTex is not null && _fxaaW == w && _fxaaH == h) return;
        _fxaaSrv?.Dispose(); _fxaaRtv?.Dispose(); _fxaaTex?.Dispose();
        _fxaaTex = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R10G10B10A2_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _fxaaRtv = _device.CreateRenderTargetView(_fxaaTex);
        _fxaaSrv = _device.CreateShaderResourceView(_fxaaTex);
        _fxaaW = w; _fxaaH = h;
    }

    private void EnsureEasuTarget(int w, int h)
    {
        if (_easuTex is not null && _easuW == w && _easuH == h) return;
        _easuSrv?.Dispose(); _easuRtv?.Dispose(); _easuTex?.Dispose();
        _easuTex = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            // 10 bit mỗi kênh giữa hai pass để không bị dải màu khi làm nét.
            Format = Format.R10G10B10A2_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _easuRtv = _device.CreateRenderTargetView(_easuTex);
        _easuSrv = _device.CreateShaderResourceView(_easuTex);
        _easuW = w; _easuH = h;
    }

    private void Draw()
    {
        var ctx = _ctx!;
        int fw = _texW, fh = _texH;

        // Hình chữ nhật video: giữ tỉ lệ; chế độ Điểm ảnh ưu tiên hệ số nguyên.
        float scale = Math.Min((float)_backW / fw, (float)_backH / fh);
        if (Mode == ScalingMode.Pixel && scale >= 1) scale = MathF.Floor(scale);
        // Kích thước nguyên để mỗi điểm ảnh ra khớp đúng một điểm ảnh màn hình (1080p → 4K là đúng 2x).
        float vw = MathF.Round(fw * scale), vh = MathF.Round(fh * scale);
        float vx = MathF.Floor((_backW - vw) / 2), vy = MathF.Floor((_backH - vh) / 2);
        _videoRect = (vx, vy, vw, vh);

        // Mịn 4K khi phóng to: FSR 1 hai pass (EASU vào texture trung gian, RCAS ra back buffer).
        // Khi thu nhỏ hoặc gần 1:1 thì EASU không giúp gì: dùng Catmull-Rom như trước.
        if (Mode != _lastMode) { _lastMode = Mode; _fsrTooSlow = false; _slowCount = 0; }
        bool fsr = Mode == ScalingMode.Smooth4K && scale > 1.05f && _psEasu is not null && _psRcas is not null && !_fsrTooSlow;

        var p = new Params
        {
            SrcSize = new Vector2(fw, fh),
            DstSize = new Vector2(vw, vh),
            Mode = Mode == ScalingMode.Smooth4K && _fsrTooSlow ? 0 : (int)Mode,
            Sharp = fsr ? MathF.Pow(2f, -RcasStops) : 0.35f,
            Fmt = (int)_texFmt,
            Hd = fh >= 720 ? 1 : 0,
            Offset = new Vector2(vx, vy),
        };
        // Với YUY2 kích thước gốc là w x h dù texture rộng w/2 (shader tự chia đôi tọa độ x).
        ctx.UpdateSubresource(in p, _cb!);
        BeginGpuTimer();
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.PSSetShaderResource(0, _srvA!);
        ctx.PSSetShaderResource(1, (_srvB ?? _srvA)!);
        ctx.PSSetSampler(0, _linear);
        ctx.PSSetSampler(1, _point);

        if (fsr)
        {
            if (AntiAlias && _psFxaa is not null)
            {
                // Pass 0: FXAA ở độ phân giải gốc, rồi EASU đọc kết quả này như một khung BGRA.
                EnsureFxaaTarget(fw, fh);
                ctx.OMSetRenderTargets(_fxaaRtv!);
                ctx.RSSetViewport(new Viewport(0, 0, fw, fh, 0, 1));
                ctx.PSSetShader(_psFxaa);
                ctx.Draw(3, 0);
                ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
                var p2 = p with { Fmt = 0 };
                ctx.UpdateSubresource(in p2, _cb!);
                ctx.PSSetShaderResource(0, _fxaaSrv!);
                ctx.PSSetShaderResource(1, _fxaaSrv!);
            }
            EnsureEasuTarget((int)vw, (int)vh);
            ctx.OMSetRenderTargets(_easuRtv!);
            ctx.RSSetViewport(new Viewport(0, 0, vw, vh, 0, 1));
            ctx.PSSetShader(_psEasu);
            ctx.Draw(3, 0);

            ctx.OMSetRenderTargets(_rtv!);
            ctx.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 1));
            ctx.RSSetViewport(new Viewport(vx, vy, vw, vh, 0, 1));
            ctx.PSSetShader(_psRcas);
            ctx.PSSetShaderResource(2, _easuSrv!);
            ctx.Draw(3, 0);
            ctx.PSSetShaderResource(2, null!);
        }
        else
        {
            ctx.OMSetRenderTargets(_rtv!);
            ctx.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 1));
            ctx.RSSetViewport(new Viewport(vx, vy, vw, vh, 0, 1));
            ctx.PSSetShader(_ps);
            ctx.Draw(3, 0);
        }
        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(1, null!);
        EndGpuTimer();
        CheckFsrBudget(fsr);

        DrawOverlay();

        if (CaptureRender) { CaptureRender = false; ReadBackBuffer(); }

        PresentFlags flags = PresentFlags.None;
        uint interval = 1;
        if (!VSync && _tearing) { flags = PresentFlags.AllowTearing; interval = 0; }
        else if (!VSync) interval = 0;
        _swap!.Present(interval, flags).CheckError();
    }

    private void DrawOverlay()
    {
        var d2d = _d2d;
        if (d2d is null || _d2dTarget is null || _dwrite is null) return;
        OverlayState o;
        long textTicks;
        lock (_bufLock) { o = _overlay; textTicks = _textChangedTicks; }
        var (vx, vy, vw, vh) = _videoRect;

        d2d.BeginDraw();
        try
        {
            if (o.ShowRegionFrames)
            {
                using var brush = d2d.CreateSolidColorBrush(new Color4(1f, 0.85f, 0.1f, 0.95f));
                foreach (var r in o.RegionFrames)
                {
                    var c = r.Clamped();
                    d2d.DrawRectangle(new Rect(vx + (float)c.X * vw, vy + (float)c.Y * vh, (float)c.Width * vw, (float)c.Height * vh), brush, 2f);
                }
            }

            if (!string.IsNullOrEmpty(o.SubtitleText)) DrawSubtitle(d2d, o, textTicks, vx, vy, vw, vh);

            if (!string.IsNullOrEmpty(o.Badge)) DrawBadges(d2d, o.Badge!, vx, vy, vw);
            if (!string.IsNullOrEmpty(o.Message)) DrawMessage(d2d, o.Message!, vx, vy, vw, vh);
        }
        finally
        {
            d2d.EndDraw();
        }
    }

    private void DrawSubtitle(ID2D1DeviceContext d2d, OverlayState o, long textTicks, float vx, float vy, float vw, float vh)
    {
        var s = o.Subtitle;
        var union = o.Union.Clamped();
        float regionX = vx + (float)union.X * vw, regionY = vy + (float)union.Y * vh;
        float regionW = (float)union.Width * vw, regionH = (float)union.Height * vh;
        float fontSize = (float)s.FontSize * vh / 1080f * 1.5f;
        float pad = fontSize * 0.45f;
        float maxW = Math.Max(regionW, 200);

        string text = o.SubtitleText!;
        if (o.ShowSource && !string.IsNullOrEmpty(o.SourceText)) text = o.SourceText + "\n" + text;

        using var format = _dwrite!.CreateTextFormat("Segoe UI", null, FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal, fontSize, "vi-vn");
        format.TextAlignment = s.Alignment switch
        {
            SubtitleAlignment.Left => TextAlignment.Leading,
            SubtitleAlignment.Right => TextAlignment.Trailing,
            SubtitleAlignment.Justified => TextAlignment.Justified,
            _ => TextAlignment.Center,
        };
        format.WordWrapping = WordWrapping.Wrap;
        using var layout = _dwrite.CreateTextLayout(text, format, maxW - 2 * pad, vh);

        // Số dòng tối đa: cắt bớt phần dư.
        var lineMetrics = new LineMetrics[64];
        layout.GetLineMetrics(lineMetrics, out uint lineCount);
        float textH = layout.Metrics.Height;
        if (lineCount > s.MaxLines)
        {
            textH = lineMetrics.Take(s.MaxLines).Sum(l => l.Height);
            layout.MaxHeight = textH;
        }
        float boxW = maxW, boxH = textH + 2 * pad;

        float x, y;
        switch (s.Position)
        {
            case SubtitlePosition.Above: x = regionX + (regionW - boxW) / 2; y = regionY - boxH - 6; break;
            case SubtitlePosition.Below: x = regionX + (regionW - boxW) / 2; y = regionY + regionH + 6; break;
            case SubtitlePosition.Over: x = regionX + (regionW - boxW) / 2; y = regionY + (regionH - boxH) / 2; break;
            default: x = vx + (float)s.FreeX * vw - boxW / 2; y = vy + (float)s.FreeY * vh - boxH / 2; break;
        }
        // Không để phụ đề tràn khỏi vùng video (vd. "trên hộp thoại" khi hộp thoại sát mép trên).
        if (y < vy) y = s.Position == SubtitlePosition.Above ? regionY + regionH + 6 : vy;
        if (y + boxH > vy + vh) y = vy + vh - boxH;
        x = Math.Clamp(x, vx, Math.Max(vx, vx + vw - boxW));

        float fade = Math.Clamp((float)Stopwatch.GetElapsedTime(textTicks).TotalMilliseconds / 150f, 0f, 1f);
        using var bg = d2d.CreateSolidColorBrush(new Color4(0, 0, 0, (float)s.BackgroundOpacity * fade));
        d2d.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(x, y, boxW, boxH), RadiusX = pad * 0.6f, RadiusY = pad * 0.6f }, bg);

        using var outline = d2d.CreateSolidColorBrush(new Color4(0, 0, 0, fade));
        using var fill = d2d.CreateSolidColorBrush(new Color4(1, 1, 1, fade));
        var origin = new Vector2(x + pad, y + pad);
        float o2 = Math.Max(1.5f, fontSize / 18f);
        foreach (var (dx, dy) in new[] { (-o2, 0f), (o2, 0f), (0f, -o2), (0f, o2), (-o2, -o2), (o2, o2), (-o2, o2), (o2, -o2) })
            d2d.DrawTextLayout(new Vector2(origin.X + dx, origin.Y + dy), layout, outline);
        d2d.DrawTextLayout(origin, layout, fill);

        if (!string.IsNullOrEmpty(o.SubtitleIcon))
        {
            using var iconFormat = _dwrite.CreateTextFormat("Segoe UI", null, FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal,
                FontStretch.Normal, fontSize * 0.5f, "vi-vn");
            using var dim = d2d.CreateSolidColorBrush(new Color4(1, 1, 1, 0.6f * fade));
            d2d.DrawText(o.SubtitleIcon, iconFormat, new Rect(x + boxW - pad * 3.5f, y + boxH - pad * 1.6f, pad * 3.4f, pad * 1.5f), dim);
        }
    }

    /// <summary>Hai thẻ nhỏ ở góc trên bên phải video (như bản thiết kế): "1080p · 60 fps (thô, trực nhận)" và "Trễ hình ~N ms".</summary>
    private void DrawBadges(ID2D1DeviceContext d2d, string text, float vx, float vy, float vw)
    {
        using var format = _dwrite!.CreateTextFormat("Segoe UI", null, FontWeight.Medium, Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal, 12, "vi-vn");
        using var bg = d2d.CreateSolidColorBrush(new Color4(0.08f, 0.08f, 0.1f, 0.62f));
        using var fg = d2d.CreateSolidColorBrush(new Color4(1, 1, 1, 0.92f));
        float right = vx + vw - 12, top = vy + 12;
        foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            using var layout = _dwrite.CreateTextLayout(part.Trim(), format, 700, 24);
            float w = layout.Metrics.Width + 18, h = layout.Metrics.Height + 8;
            float x = right - w;
            d2d.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(x, top, w, h), RadiusX = 6, RadiusY = 6 }, bg);
            d2d.DrawTextLayout(new Vector2(x + 9, top + 4), layout, fg);
            right = x - 8;
        }
    }

    private void DrawMessage(ID2D1DeviceContext d2d, string text, float vx, float vy, float vw, float vh)
    {
        using var format = _dwrite!.CreateTextFormat("Segoe UI", null, FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal, 18, "vi-vn");
        format.TextAlignment = TextAlignment.Center;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        using var fg = d2d.CreateSolidColorBrush(new Color4(1, 1, 1, 0.85f));
        d2d.DrawText(text, format, new Rect(vx + 20, vy, vw - 40, vh), fg);
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(1500);
        lock (_sync)
        {
            DisposeDeviceObjects();
            _dwrite?.Dispose();
            _dwrite = null;
        }
    }
}
