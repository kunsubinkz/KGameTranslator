using GameTranslator.Core;
using GameTranslator.Core.LocalHost;
using GameTranslator.Core.Pipeline;
using GameTranslator.Platform.Windows;
using GameTranslator.Platform.Windows.LocalModel;
using GameTranslator.Platform.Windows.Audio;
using GameTranslator.Platform.Windows.Capture;
using GameTranslator.Platform.Windows.Gpu;
using GameTranslator.Platform.Windows.Ocr;
using GameTranslator.Platform.Windows.Render;

namespace GameTranslator.App;

/// <summary>Số liệu hiện trên thanh trạng thái và thẻ góc video.</summary>
public sealed record StatusSnapshot(
    string Source, string Model, string Quota, string Ocr, string Translate, double Cpu, string Gpu,
    double Fps, string Format, double LatencyMs, string? Message, string LastEngine, string Nllb = "");

/// <summary>Điều phối toàn bộ: capture, vẽ, tiếng, OCR, dịch. Không phụ thuộc giao diện.</summary>
public sealed class Session : IGpuFrameSink, IDisposable
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public ProfileStore Profiles { get; } = new(AppPaths.Profiles);
    public TranslationCache Cache { get; }
    public MfCaptureSource Capture { get; } = new();
    public TestPatternSource Test { get; } = new();
    public AudioMonitor Audio { get; } = new();
    public PipelineCoordinator Pipeline { get; }
    public ResourceMonitor Resources { get; } = new();
    public VideoRenderer? Renderer { get; private set; }
    /// <summary>Host NLLB tự quản lý (cài môi trường, tải model, chạy/dừng).</summary>
    public LocalHostManager LocalHost { get; private set; }
    /// <summary>Việc nền của dịch offline (cài, tải model); đóng cửa sổ không hủy.</summary>
    public LocalJobs Jobs { get; }

    public List<Profile> ProfileList { get; private set; } = [];
    public Profile Current { get; private set; } = Profile.Default;
    public IReadOnlyList<GpuInfo> Gpus { get; private set; } = [];

    public bool TranslateEnabled { get; private set; }
    public bool ShowSource { get; private set; }
    public bool SelectingRegion { get; set; }

    public event Action? StateChanged;
    public event Action<string>? Notice;

    private string? _videoMessage = "Chưa chọn nguồn hình";
    private string _formatLabel = "";
    private string? _subtitle, _source, _icon;
    private string _engineLabel = "—";
    private double _ocrMs, _translateMs;
    private RateLimiter? _limiter;
    private FallbackTranslator? _translator;
    private int _frameCounter;
    private bool _usingTest;

    public Session()
    {
        Directory.CreateDirectory(AppPaths.Local);
        Cache = new TranslationCache(AppPaths.Cache);
        LocalHost = NewLocalHost();
        Jobs = new LocalJobs(this);
        Pipeline = new PipelineCoordinator(new WindowsOcr(), Cache, OnPipelineEvent);
        Gpus = GpuCatalog.Enumerate();

        Profiles.SeedIfNeeded();
        ReloadProfiles();
        Capture.StatusChanged += m => { _videoMessage = string.IsNullOrEmpty(m) ? null : m; RefreshOverlay(); };
        Capture.Started += info => { _formatLabel = $"{(info.Format.Height >= 2160 ? "4K" : info.Format.Height + "p")} · {info.Format.Fps:0.#} fps · {info.Path}"; };
        Capture.Stopped += () => { };
        Audio.StatusChanged += m => Notice?.Invoke(m);
    }

    // ---------- Hồ sơ ----------

    public void ReloadProfiles()
    {
        ProfileList = [.. Profiles.List()];
        Current = ProfileList.FirstOrDefault(p => p.Id == Settings.ProfileId) ?? ProfileList.FirstOrDefault() ?? Profile.Default;
        ApplyProfile();
    }

    public void SelectProfile(string id)
    {
        if (ProfileList.FirstOrDefault(p => p.Id == id) is not { } p) return;
        Current = p;
        Settings.ProfileId = id;
        Settings.Save();
        ApplyProfile();
    }

    /// <summary>Dựng lại bộ dịch và đưa hồ sơ hiện tại vào pipeline (gọi sau khi sửa hồ sơ hoặc khóa API).</summary>
    public void ApplyProfile()
    {
        var t = Current.Translate;
        var restored = Cache.Meta("rate.day") is { } day && int.TryParse(Cache.Meta("rate.count"), out var count) ? (day, count) : ((string, int)?)null;
        _limiter = new RateLimiter(t.RequestsPerMinute, t.RequestsPerDay, restored: restored,
            persist: (d, c) => { Cache.SetMeta("rate.day", d); Cache.SetMeta("rate.count", c.ToString()); });
        ITranslator? online = t.Mode == TranslateMode.OfflineOnly ? null : t.Provider == TranslateProvider.Claude
            ? new ClaudeTranslator(t.Model, () => CredentialStore.Read("claude"))
            : new GeminiTranslator(t.GeminiModel, () => CredentialStore.Read("gemini"));
        _translator = new FallbackTranslator(t.Mode, online, t.ActiveModel, t.Provider, MakeOffline(), _limiter);
        Pipeline.Configure(Current, _translator);
        ApplyRenderSettings();
        RefreshOverlay();
        EnsureLocalHost();
    }

    // ---------- Dịch offline (NLLB) ----------

    private LocalHostManager NewLocalHost()
    {
        var l = Settings.Local;
        var paths = new NllbPaths(AppPaths.Nllb, AppPaths.NllbBundle, l.ModelsDir ?? Path.Combine(AppPaths.Nllb, "models"), l.ModelPaths);
        return new LocalHostManager(paths, Path.Combine(AppPaths.Logs, "nllb-host.log"), () => Settings.Local.ModelId) { Device = l.Device };
    }

    /// <summary>Dựng lại host quản lý khi đổi thư mục model.</summary>
    public void ReconfigureLocalHost()
    {
        var old = LocalHost;
        LocalHost = NewLocalHost();
        old.Dispose();
        ApplyProfile();
    }

    public LocalHostTranslator? OfflineTranslator() => MakeOffline() as LocalHostTranslator;

    private ITranslator? MakeOffline()
    {
        var l = Settings.Local;
        if (!l.Enabled) return null;
        return new LocalHostTranslator(
            () => l.Host == LocalHostKind.External ? l.ExternalUrl : LocalHost.BaseUrl,
            () => l.Host == LocalHostKind.External ? CredentialStore.Read("nllb-external") : LocalHostManager.Token(),
            () => Settings.Local.Beams, timeoutSeconds: l.TimeoutSeconds);
    }

    /// <summary>Tự khởi động host khi hồ sơ cần dịch offline và host đã cài đủ.</summary>
    public void EnsureLocalHost()
    {
        var l = Settings.Local;
        if (!l.Enabled || l.Host != LocalHostKind.Managed || !l.AutoStart || Current.Translate.Mode == TranslateMode.OnlineOnly) return;
        if (LocalHost.IsRunning) return;
        if (!LocalHost.Environment.IsInstalled || !LocalHost.ModelDownloaded(l.ModelId)) return;
        LocalHost.Device = l.Device;
        _ = LocalHost.StartAsync();
    }

    /// <summary>Trạng thái NLLB cho thanh trạng thái; rỗng khi không dùng dịch offline.</summary>
    public string NllbStatusText()
    {
        var l = Settings.Local;
        if (!l.Enabled || Current.Translate.Mode == TranslateMode.OnlineOnly) return "";
        if (l.Host == LocalHostKind.External) return string.IsNullOrWhiteSpace(l.ExternalUrl) ? "NLLB: chưa nhập địa chỉ" : "NLLB: host ngoài";
        return LocalHost.Status.State switch
        {
            HostState.Ready => "NLLB: sẵn sàng",
            HostState.Loading or HostState.Starting => "NLLB: đang nạp",
            HostState.Error => "NLLB: lỗi",
            HostState.NotInstalled => "NLLB: chưa cài",
            _ => "NLLB: chưa chạy",
        };
    }

    public void SaveCurrentProfile()
    {
        Profiles.Save(Current);
        ApplyProfile();
    }

    // ---------- Hình ----------

    public void AttachRenderer(nint hwnd)
    {
        var gpu = GpuCatalog.Select(Gpus, Settings.Gpu, Settings.GpuKey, null);
        Renderer = new VideoRenderer(hwnd, gpu);
        Renderer.StatusChanged += m => Notice?.Invoke(m);
        // Đổi GPU hoặc GPU bị reset: mở lại capture để bộ giải mã phần cứng dùng device mới (chạy trên luồng UI, tránh khóa chéo).
        Renderer.DeviceRecreated += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!string.IsNullOrEmpty(Settings.VideoDeviceId)) StartVideo(); });
        ApplyRenderSettings();
        RefreshOverlay();
    }

    public void ApplyRenderSettings()
    {
        if (Renderer is null) return;
        Renderer.Mode = Settings.Scaling;
        Renderer.AntiAlias = Settings.AntiAlias;
        Renderer.RcasStops = (float)Settings.SharpenStops;
        Renderer.VSync = Settings.VSync;
        Renderer.GpuPreference = Settings.Gpu;
        Renderer.SpecificGpuKey = Settings.GpuKey;
    }

    /// <summary>Chế độ Tự động: chuyển sang GPU đang nối với màn hình chứa cửa sổ. Gọi định kỳ từ UI.</summary>
    public void FollowWindowGpu((int X, int Y)? windowCenter)
    {
        if (Renderer is null) return;
        var pick = GpuCatalog.Select(Gpus, Settings.Gpu, Settings.GpuKey, windowCenter);
        if (pick is not null && pick.StableKey != Renderer.Gpu?.StableKey) Renderer.SwitchGpu(pick);
    }

    public void StartVideo()
    {
        StopVideo();
        _frameCounter = 0;
        if (Settings.VideoDeviceId == TestPatternSource.DeviceId)
        {
            _usingTest = true;
            _videoMessage = null;
            _formatLabel = "720p · 60 fps · thử";
            Test.Start(this);
        }
        else if (!string.IsNullOrEmpty(Settings.VideoDeviceId))
        {
            _usingTest = false;
            _videoMessage = "Đang mở thiết bị...";
            Capture.Start(new CaptureDevice(Settings.VideoDeviceId, Settings.VideoDeviceName ?? ""), this, Settings.FormatIndex);
        }
        else
        {
            _videoMessage = "Chưa chọn nguồn hình";
        }
        RefreshOverlay();
        StartAudio();
    }

    public void StopVideo()
    {
        Capture.Stop();
        Test.Stop();
        Audio.Stop();
    }

    public void StartAudio()
    {
        Audio.Stop();
        Audio.Volume = (float)Settings.Volume;
        if (Settings.AudioEnabled && !string.IsNullOrEmpty(Settings.AudioInputId))
            Audio.Start(Settings.AudioInputId, Settings.AudioOutputId, Settings.AudioDelayMs);
    }

    public void OnFrame(FrameFormat format, ReadOnlySpan<byte> data, int width, int height, int stride)
    {
        var r = Renderer;
        if (r is null) return;
        if (_videoMessage is not null)
        {
            _videoMessage = null;
            r.SetOverlay(BuildOverlay());
        }
        r.OnFrame(format, data, width, height, stride);
        if (++_frameCounter >= Math.Max(Current.Detector.SampleEveryNFrames, 1))
        {
            _frameCounter = 0;
            if (Pipeline.WantsFrame && r.CropRegions(Current.AllRegions) is { } crops) Pipeline.Submit(crops);
        }
    }

    /// <summary>Bộ giải mã MJPEG/H.264 dùng chung GPU với renderer (tắt được trong Cài đặt).</summary>
    public Vortice.MediaFoundation.IMFDXGIDeviceManager? DeviceManager => Settings.HardwareDecode ? Renderer?.DeviceManager : null;

    public void OnGpuFrame(Vortice.Direct3D11.ID3D11Texture2D texture, uint subresource, int width, int height)
    {
        var r = Renderer;
        if (r is null) return;
        if (_videoMessage is not null)
        {
            _videoMessage = null;
            r.SetOverlay(BuildOverlay());
        }
        r.OnGpuFrame(texture, subresource, width, height);
        if (++_frameCounter >= Math.Max(Current.Detector.SampleEveryNFrames, 1))
        {
            _frameCounter = 0;
            // Chỉ đọc khung về CPU khi pipeline sẵn sàng OCR (vài lần mỗi giây).
            if (Pipeline.WantsFrame && r.CropRegions(Current.AllRegions) is { } crops) Pipeline.Submit(crops);
        }
    }

    // ---------- Dịch ----------

    public void SetTranslate(bool on)
    {
        TranslateEnabled = on;
        Pipeline.SetEnabled(on);
        if (!on) { _subtitle = null; _source = null; Pipeline.ResetState(); }
        RefreshOverlay();
        StateChanged?.Invoke();
    }

    public void SetShowSource(bool on)
    {
        ShowSource = on;
        RefreshOverlay();
        StateChanged?.Invoke();
    }

    public void Retranslate() => Pipeline.Retranslate();

    private void OnPipelineEvent(PipelineEvent e)
    {
        AppLog.Write("pipeline: " + e);
        switch (e)
        {
            case PipelineEvent.Recognized r:
                _ocrMs = r.OcrMs;
                _source = r.Text;
                // Lúc chờ dịch không hiện chữ gốc: ẩn phụ đề cũ.
                _subtitle = null;
                break;
            case PipelineEvent.Translating:
                _subtitle = null;
                break;
            case PipelineEvent.Subtitle s:
                _source = s.Source;
                _subtitle = s.Target;
                _translateMs = s.TranslateMs;
                _engineLabel = s.Engine;
                _icon = s.Cached ? "cache" : s.Engine == "original" ? "chữ gốc" : s.Engine.StartsWith("apple") ? "offline (NLLB)" : null;
                break;
            case PipelineEvent.Cleared:
                _subtitle = null;
                _source = null;
                break;
        }
        RefreshOverlay();
    }

    // ---------- Lớp phủ ----------

    private OverlayState BuildOverlay() => new()
    {
        SubtitleText = _subtitle,
        SourceText = _source,
        ShowSource = ShowSource,
        SubtitleIcon = _icon,
        Subtitle = Current.Subtitle,
        Union = Current.UnionRegion,
        RegionFrames = Current.AllRegions,
        ShowRegionFrames = Settings.ShowRegionFrame || SelectingRegion,
        Badge = BadgeText(),
        Message = _videoMessage,
    };

    public void RefreshOverlay()
    {
        var r = Renderer;
        if (r is null) return;
        r.SetOverlay(BuildOverlay());
        r.Invalidate();
    }

    private string BadgeText()
    {
        var fps = _usingTest ? Test.MeasuredFps : Capture.MeasuredFps;
        if (fps <= 0 && string.IsNullOrEmpty(_formatLabel)) return "";
        var fmt = _formatLabel.Split(" · ");
        string res = fmt.Length > 0 ? fmt[0] : "";
        string kind = fmt.Length > 2 ? fmt[2] : "";
        var gpu = Renderer?.Gpu?.Name.Replace("Intel(R) ", "").Replace("Graphics", "").Replace("(TM)", "").Trim() ?? "";
        // Hai thẻ như bản thiết kế (tách bằng "|"): định dạng + FPS thực nhận, và độ trễ trong app.
        return $"{res} · {fps:0} fps ({kind}, trực nhận) | Trễ hình ~{Renderer?.Stats.InAppLatencyMs ?? 0:0} ms";
    }

    public StatusSnapshot Snapshot()
    {
        var t = Current.Translate;
        string quota = _limiter is null ? "—" : $"{_limiter.RemainingToday()}/{_limiter.DailyLimit}";
        double fps = _usingTest ? Test.MeasuredFps : Capture.MeasuredFps;
        return new StatusSnapshot(
            t.Mode == TranslateMode.OfflineOnly ? "Offline (NLLB)" : t.Provider == TranslateProvider.Claude ? "Claude" : "Gemini",
            t.ActiveModel, quota,
            _ocrMs > 0 ? $"{_ocrMs:0} ms" : "—", _translateMs > 0 ? $"{_translateMs:0} ms" : "—",
            Resources.SampleCpuPercent(), Renderer?.Gpu?.Name ?? "—", fps, _formatLabel,
            Renderer?.Stats.InAppLatencyMs ?? 0, _videoMessage, _engineLabel, NllbStatusText());
    }

    /// <summary>Thử khóa API: dịch một câu mẫu bằng nhà cung cấp đã chọn.</summary>
    public async Task<string> TestKeyAsync(TranslateProvider provider, string model)
    {
        ITranslator tr = provider == TranslateProvider.Claude
            ? new ClaudeTranslator(model, () => CredentialStore.Read("claude"), timeoutSeconds: 15)
            : new GeminiTranslator(model, () => CredentialStore.Read("gemini"), timeoutSeconds: 15);
        try
        {
            var text = await tr.TranslateAsync(new TranslationRequest("Hello, traveler!"));
            return "Hoạt động: " + text;
        }
        catch (TranslationException ex)
        {
            return ex.Kind switch
            {
                TranslationErrorKind.MissingApiKey => "Chưa nhập API key.",
                TranslationErrorKind.QuotaExceeded => "Hết hạn mức hoặc hết credit.",
                TranslationErrorKind.Timeout => "Quá thời gian chờ.",
                _ => "Lỗi: " + ex.Message,
            };
        }
    }

    public void Dispose()
    {
        StopVideo();
        LocalHost.Dispose();
        Renderer?.Dispose();
        Cache.Dispose();
    }
}
