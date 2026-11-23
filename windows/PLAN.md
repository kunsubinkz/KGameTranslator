# Game Translator cho Windows — Kế hoạch triển khai

Mục tiêu: bản Windows có tính năng và hành vi giống bản macOS ([../macos](../macos)), cùng định dạng hồ sơ game và cache để dùng chung. Có bộ cài `.exe` (NSIS) và bản portable `.zip` cho **x64 + ARM64**.

Cách làm: phát triển, build, kiểm thử và đóng gói **trực tiếp trên máy Windows** bằng Claude Code (quy ước cho Claude ở [CLAUDE.md](CLAUDE.md), cách giao việc ở mục 11). Phần capture, GPU và độ trễ phải kiểm thử trên **máy Windows thật** (máy ảo không đo được). Riêng việc xuất test vector cần máy Mac và đã làm sẵn.

Tài liệu tham chiếu:
- Kế hoạch gốc bản macOS: [../ke-hoach-game-translator-macos.md](../ke-hoach-game-translator-macos.md). Một số chi tiết trong đó đã cũ; bản macOS hiện tại ghi trong [../README.md](../README.md).
- Thiết kế giao diện: [../Game Translator macOS - Giao diện.html](<../Game Translator macOS - Giao diện.html>) (5 màn hình: cửa sổ chính, chọn vùng, cài đặt dịch, lịch sử, hồ sơ game).

## 0. Trạng thái thực hiện

Cập nhật 2026-11-23: mới xong giai đoạn 1 (làm trên Mac). Các giai đoạn còn lại làm trên Windows.

| Giai đoạn | Trạng thái |
|---|---|
| 0. Chuẩn bị (máy Windows) | Chưa làm |
| 1. Test vector từ Swift | **Xong** (7 file trong `tests/vectors/`, xuất bằng `VectorExportTests.swift`) |
| 2. Core C# | Chưa làm |
| 3. Capture + hiển thị GPU | Chưa làm |
| 4. Pipeline OCR + dịch | Chưa làm |
| 5. Giao diện Avalonia | Chưa làm |
| 6. Đóng gói | Chưa làm |
| 7. Kiểm thử trên Windows | Chưa làm |

Khác với kế hoạch ban đầu:

- Phát triển trên Windows thay vì build chéo từ macOS. Script đóng gói là `build_release.ps1`.
- Shader viết cho Direct3D 11 (Shader Model 5) nên biên dịch bằng `D3DCompile` có sẵn trong Windows, không cần `dxc`.

## 1. Những gì bản macOS đã có (phải giữ nguyên hành vi)

Bản Windows lấy hành vi hiện tại của bản macOS làm chuẩn, không lấy kế hoạch macOS cũ:

- Nhận hình và tiếng từ capture card UVC, ưu tiên 1920x1080 ở 60 fps, **ưu tiên định dạng thô** (MJPEG/H.264 phải giải mã nên trễ hơn). Chỉ chọn frame rate **có trong danh sách thiết bị báo**, không tự tạo giá trị (lỗi văng app khi cắm UGREEN trên Mac là do điều này).
- Hình và tiếng chạy **hai luồng riêng**, không phụ thuộc nhau.
- Thẻ góc video hiện FPS thực nhận (cập nhật mỗi 2 giây) và định dạng (thô/MJPEG).
- Chế độ hiển thị hình: Mặc định, Mịn 4K, Điểm ảnh.
- **Nhiều vùng OCR**: vùng chính và các vùng phụ; chữ các vùng được ghép rồi dịch chung.
- Change detector chỉ OCR khi chữ đã ổn định. Khi đang hiện phụ đề, app **OCR kiểm tra mỗi 600 ms**, hai lần liền không thấy chữ thì ẩn (để nền game chuyển động vẫn ẩn được).
- Lúc chờ dịch **không hiện chữ gốc tiếng Anh**: phụ đề cũ ẩn đi, có bản dịch mới hiện. Cache thì hiện ngay.
- Dịch online: nhà cung cấp **Claude** (mặc định, `claude-haiku-5-5`) hoặc **Gemini**, mỗi bên một API key; model chọn bằng dropdown hoặc nhập tay. Gửi tham số theo model Claude: Haiku tắt thinking, Sonnet 5.5 dùng `between_tools`, Opus 5.5 hạ effort xuống `low`; không gửi `temperature`.
- Chế độ dịch: Chỉ online, Online + dự phòng offline, Chỉ offline. Có rate limiter theo phút/ngày, cache SQLite, ngữ cảnh câu trước, từ điển theo game, nâng cấp bản offline lên bản online.
- Phụ đề rộng bằng vùng OCR, tự xuống dòng, căn **trái/giữa/phải/đều**, số dòng tối đa chỉnh được (mặc định 12), vị trí trên/dưới/đè lên/tự do.
- Lịch sử: xem, sửa tay, dịch lại bằng AI online, xuất CSV.

## 2. Quyết định công nghệ

| Hạng mục | Lựa chọn | Lý do |
|---|---|---|
| Ngôn ngữ, runtime | C# trên .NET 10 (LTS), `Nullable` + `TreatWarningsAsErrors` | Giống KVietKey |
| Core | Port `TranslatorCore` (Swift) sang class library C# không phụ thuộc nền tảng | Kiểm chứng bằng test vector xuất từ Swift |
| Giao diện | Avalonia UI 11.3 (Fluent, compiled bindings) | Cùng hướng với KVietKey, xuất ảnh UI không cần cửa sổ thật (Headless) |
| Khung video | Cửa sổ con Win32 (`NativeControlHost`) chứa swap chain **Direct3D 11**, flip model | Kiểm soát được GPU, độ trễ và bộ phóng ảnh |
| Binding DirectX/MF | Vortice.Windows (Direct3D11, DXGI, Direct2D1, DirectWrite, MediaFoundation, D3DCompiler) | Managed, có sẵn trên NuGet, không cần C++ |
| Shader | HLSL Shader Model 5, biên dịch bằng `D3DCompile` (`d3dcompiler_47.dll` có sẵn trong Windows) lúc build, nhúng `.cso` làm resource | Không cần cài thêm trình biên dịch shader |
| Thu hình | Media Foundation Source Reader (async) dùng chung D3D11 device | Khung nằm trên GPU, giải mã MJPEG bằng phần cứng |
| Thu hình dự phòng | DirectShow (giai đoạn sau) | Một số card chỉ chạy tốt qua DirectShow (OBS dùng DirectShow) |
| Tiếng | WASAPI: capture event-driven từ card, render bằng `IAudioClient3` chu kỳ nhỏ (NAudio hoặc tự gọi) | Trễ thấp, tách hẳn khỏi hình |
| Phụ đề, khung vùng, thẻ | Vẽ bằng Direct2D + DirectWrite **lên cùng swap chain** | Tránh lỗi "airspace" (Avalonia không vẽ đè lên cửa sổ con native được), đúng cả khi toàn màn hình |
| OCR mặc định | `Windows.Media.Ocr` (WinRT, có sẵn) | Không cần cài thêm |
| OCR chính xác hơn (tùy chọn) | PaddleOCR (PP-OCR tiếng Anh) qua ONNX Runtime + DirectML | Chạy trên GPU người dùng chọn, tốt hơn với font game |
| Dịch online | Claude: SDK C# chính thức của Anthropic (gói NuGet, kiểm tra tên gói khi làm); Gemini: REST bằng `HttpClient` | Giữ đúng tham số như bản macOS |
| Dịch offline | Windows không có API dịch offline có sẵn. Bản 1.0 chỉ có online + hiện chữ gốc khi lỗi; model cục bộ để giai đoạn mở rộng | Xem mục 9 |
| Cache | SQLite qua `Microsoft.Data.Sqlite`, schema trùng bản macOS | Dùng chung được file cache |
| Lưu API key | Windows Credential Manager (`CredWriteW`, target `KGameTranslator/claude`, `KGameTranslator/gemini`) | Tương đương Keychain |
| Đóng gói | `dotnet publish -r win-x64` / `win-arm64`, self-contained, single-file, ReadyToRun → NSIS (`makensis`) + `.zip`, chạy bằng `build_release.ps1` trên Windows | Giống KVietKey |
| Ký số (tùy chọn) | `signtool` (Windows SDK) | Authenticode |

> Dự án nền tảng dùng TFM `net10.0-windows10.0.19041.0` (cần cho WinRT OCR). `GameTranslator.Core` dùng `net10.0` thuần nên test chạy được cả trên macOS. Vì build trên Windows nên có thể thử NativeAOT sau này; bản 1.0 dùng self-contained + ReadyToRun cho chắc.

### Vì sao không dùng thẳng control video của Avalonia

Avalonia vẽ qua Skia (hoặc ANGLE). Đẩy 60 khung/giây 1080p qua `WriteableBitmap` phải chép khung về CPU, tốn thêm ít nhất 1 khung trễ, và không chọn được GPU hay bộ phóng ảnh. Vì vậy khung video là một swap chain D3D11 riêng. Đánh đổi: phụ đề, khung vùng OCR và thẻ FPS phải vẽ bằng Direct2D trong swap chain đó, không dùng control Avalonia. Màn hình chọn vùng vẫn làm bằng Avalonia vì nó dùng ảnh tĩnh, không cần video chạy bên dưới.

## 3. Chọn card màn hình (GPU) và hiển thị tối ưu

Đây là phần riêng của bản Windows. Máy Windows thường có nhiều GPU (laptop có GPU tích hợp + GPU rời, desktop có thể có 2 card). Chọn sai GPU làm hình bị chép qua lại giữa hai card, thêm trễ và tốn điện.

### 3.1 Liệt kê GPU

- Dùng `IDXGIFactory6.EnumAdapterByGpuPreference` để lấy danh sách theo thứ tự hiệu năng.
- Bỏ adapter phần mềm (`DXGI_ADAPTER_FLAG_SOFTWARE`, "Microsoft Basic Render Driver").
- Với mỗi GPU, lấy:
  - Tên (`Description`), hãng theo `VendorId`: `0x10DE` NVIDIA, `0x1002` AMD, `0x8086` Intel, `0x5143` Qualcomm.
  - VRAM riêng (`DedicatedVideoMemory`), hoặc ghi "bộ nhớ dùng chung" với GPU tích hợp.
  - GPU đó **có nối màn hình nào không** (`EnumOutputs`), và màn hình nào.
  - Tính năng hỗ trợ: cho phép xé hình (`DXGI_FEATURE_PRESENT_ALLOW_TEARING`), Video Super Resolution của hãng (mục 3.4), giải mã MJPEG/H.264 phần cứng.
- Lưu GPU đã chọn theo `VendorId + DeviceId + Description`. LUID đổi sau mỗi lần khởi động lại nên không dùng làm khóa lưu, chỉ dùng trong phiên.

### 3.2 Tùy chọn trong Cài đặt > Chung > "Card màn hình (GPU)"

| Lựa chọn | Hành vi |
|---|---|
| **Tự động (khuyên dùng)** | Dùng GPU đang nối với màn hình chứa cửa sổ app. Kéo cửa sổ sang màn hình của GPU khác thì app tự chuyển (dựa vào `MonitorFromWindow` so với `IDXGIOutput.DesktopCoordinates`). Hình không phải chép sang GPU khác, trễ thấp nhất |
| Hiệu năng cao | GPU mạnh nhất (`DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE`) |
| Tiết kiệm điện | GPU tích hợp (`DXGI_GPU_PREFERENCE_MINIMUM_POWER`) |
| Từng GPU cụ thể | Ví dụ "NVIDIA GeForce RTX 4070 · 12 GB · nối màn hình 1" |

Kèm theo:
- Dòng ghi chú dưới dropdown khi GPU đã chọn **không nối màn hình đang dùng** (thường gặp ở laptop NVIDIA Optimus, màn hình laptop nối GPU tích hợp): "Hình được xử lý trên GPU rời rồi chép sang GPU tích hợp để hiện lên màn hình, có thể thêm khoảng 1 khung trễ. Muốn trễ thấp nhất hãy chọn Tự động, hoặc cắm màn hình ngoài vào cổng của GPU rời."
- Nút "Đặt ưu tiên GPU trong Windows": ghi `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`, giá trị `<đường dẫn exe>` = `GpuPreference=2;` (hiệu năng cao) hoặc `1;` (tiết kiệm điện). Không cần quyền admin. Phần giao diện Avalonia (ANGLE) cũng chạy theo GPU này sau khi mở lại app.
- Mục "GPU cho OCR" (chỉ hiện khi bật PaddleOCR): mặc định "Giống GPU hiển thị", hoặc chọn GPU khác để OCR không tranh tài nguyên với game trên GPU hiển thị. Truyền `device_id` của adapter cho DirectML.

### 3.3 Áp dụng GPU đã chọn

- Tạo **một** `ID3D11Device` trên adapter đó (`D3D_DRIVER_TYPE_UNKNOWN`, cờ `BGRA_SUPPORT` + `VIDEO_SUPPORT`). Dùng chung cho:
  - Media Foundation (`IMFDXGIDeviceManager`): giải mã và đổi màu ngay trên GPU đó, khung không về CPU.
  - Video Processor để đổi YUV→RGB.
  - Shader phóng ảnh.
  - Direct2D (`ID2D1Device` từ DXGI device) vẽ phụ đề.
- Đổi GPU khi đang chạy: dựng lại device, swap chain và Source Reader (khoảng nửa giây đen hình), giữ nguyên trạng thái pipeline dịch.
- Mất device (`DXGI_ERROR_DEVICE_REMOVED`, `DEVICE_RESET`: cập nhật driver, TDR, rút eGPU): tự dựng lại trên GPU còn lại theo cùng quy tắc chọn, báo một dòng trên thanh trạng thái.
- Thẻ góc video hiện: `1080p · 60 fps · thô · RTX 4070`.

### 3.4 Chế độ hiển thị hình (Cài đặt > Chung > "Hiển thị hình")

Giống 3 chế độ của bản macOS, thêm một chế độ dùng tính năng riêng của GPU:

| Chế độ | Cách làm trên Windows | Ghi chú |
|---|---|---|
| Mặc định | `ID3D11VideoProcessor`: đổi YUV→RGB và phóng bằng phần cứng video | Nhẹ nhất, tương đương "hệ thống" trên Mac |
| **Mịn 4K** | AMD FidelityFX Super Resolution 1 (EASU phóng + RCAS làm nét), viết bằng HLSL compute shader | Mã nguồn mở (MIT), chạy trên mọi GPU kể cả Intel/Qualcomm; tương đương Lanczos + làm nét của bản Mac |
| Điểm ảnh | Phóng nearest neighbor, ưu tiên hệ số nguyên (1080p lên 4K là đúng 2x) | Nét, khối vuông |
| Siêu phân giải của GPU | NVIDIA RTX Video Super Resolution (RTX 30 trở lên) hoặc Intel Video Super Resolution, bật qua extension của Video Processor | Chỉ hiện khi driver hỗ trợ; đẹp hơn nhưng trễ và tốn điện hơn. Để giai đoạn mở rộng |

Có thể thay FSR 1 bằng NVIDIA Image Scaling (NIS, cũng mã nguồn mở) nếu đo thấy đẹp hơn; chọn một cái làm mặc định sau khi so trên máy thật.

### 3.5 Độ trễ thấp khi trình chiếu

Bản macOS bị phản ánh "tay cầm hơi trễ, OBS thì không", nên trên Windows đặt độ trễ làm tiêu chí chính ngay từ đầu:

- Swap chain flip model (`DXGI_SWAP_EFFECT_FLIP_DISCARD`), 2 buffer, `FRAME_LATENCY_WAITABLE_OBJECT`, `SetMaximumFrameLatency(1)`.
- Vẽ **ngay khi khung đến** từ Source Reader, không chờ timer của UI.
- Toàn màn hình dạng cửa sổ không viền phủ kín màn hình, để Windows dùng independent flip (trễ thấp như toàn màn hình độc quyền).
- Tùy chọn "Đồng bộ dọc": Bật (mặc định, không xé hình) hoặc **Tắt, trễ thấp nhất** (`DXGI_PRESENT_ALLOW_TEARING`, hợp với màn hình G-Sync/FreeSync).
- Luồng vẽ và luồng capture đăng ký MMCSS (`AvSetMmThreadCharacteristics("Capture")`/`("Games")`) để không bị luồng khác chen.
- Media Foundation đặt `MF_LOW_LATENCY = TRUE` và `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS`.
- Đo trễ trong app: lấy thời điểm khung đến (QPC) và thời điểm present thật (`GetFrameStatistics`), hiện "Trễ trong app ~N ms" trên thẻ góc video. Đo cả hệ thống bằng PresentMon và quay chậm bằng điện thoại khi kiểm thử.

### 3.6 Lấy khung cho OCR mà không chậm hình

- Mỗi N khung (mặc định 15), cắt **riêng vùng OCR** trên GPU (`CopySubresourceRegion`) sang texture staging nhỏ rồi mới đọc về CPU. Không chép cả khung 1080p.
- Thu nhỏ còn 160 px cho change detector bằng shader, đọc về một mảng nhỏ.
- Nếu pipeline đang bận thì bỏ khung, giống bản macOS.

## 4. Ánh xạ thành phần macOS → Windows

| macOS ([../macos](../macos)) | Windows | Ghi chú |
|---|---|---|
| `Packages/TranslatorCore/ChangeDetector.swift` | `GameTranslator.Core/ChangeDetector.cs` | Port 1:1, thời gian truyền từ ngoài vào |
| `TextNormalizer.swift` | `TextNormalizer.cs` | Regex sửa lỗi OCR giữ nguyên; dùng `ToLowerInvariant`, so sánh `Ordinal` |
| `TranslationCache.swift` (SQLite3 + CryptoKit) | `TranslationCache.cs` (`Microsoft.Data.Sqlite` + `SHA256`) | Khóa = SHA-256 của `profileId + "\n" + source viết thường`, phải ra đúng cùng hash với Swift |
| `RateLimiter.swift` | `RateLimiter.cs` | Ngày đặt lại theo giờ `America/Los_Angeles` (`TimeZoneInfo` "Pacific Standard Time") |
| `PromptBuilder.swift` | `PromptBuilder.cs` | Chuỗi prompt giữ nguyên từng ký tự |
| `ClaudeTranslator.swift`, `GeminiTranslator.swift` | `ClaudeTranslator.cs`, `GeminiTranslator.cs` | Cùng tham số và cách đọc lỗi (429, hết credit, `refusal`) |
| `FallbackTranslator.swift`, `Translator.swift` | `FallbackTranslator.cs`, `ITranslator` | Backoff 1–30 s khi hết hạn mức |
| `Profile.swift`, `ProfileStore.swift` | `Profile.cs` (System.Text.Json), `ProfileStore.cs` | Cùng schema JSON; tự chuyển hồ sơ cũ (model Gemini trong `model`) như bản Mac |
| `App/Capture/CaptureController.swift` | `Platform.Windows/Capture/MfCaptureSource.cs` | Source Reader async trên D3D11 device đã chọn |
| `FormatSelector` | `MediaTypeSelector.cs` | 1080p, ≥ 60 fps, thô trước MJPEG; chỉ dùng media type thiết bị liệt kê |
| `DeviceDiscovery.swift` | `DeviceDiscovery.cs` | `MFEnumDeviceSources` cho hình, `MMDeviceEnumerator` cho tiếng; ghép tiếng theo tên card như `bestAudioMatch` |
| Session tiếng riêng (`AVCaptureAudioPreviewOutput`) | `AudioMonitor.cs` (WASAPI capture → render) | Âm lượng, chọn thiết bị ra, bù trễ 0–200 ms |
| `VideoView.swift` / `MetalVideoView.swift` | `VideoSurface` (`NativeControlHost` + `D3DRenderer`) | Chế độ hiển thị ở mục 3.4 |
| `SubtitleOverlay.swift` | `SubtitleRenderer.cs` (Direct2D + DirectWrite) | Căn đều dùng `DWRITE_TEXT_ALIGNMENT_JUSTIFIED`; viền chữ bằng stroke hình học |
| `RegionSelector.swift` | `RegionSelectorView.axaml` | Ảnh tĩnh từ khung mới nhất, nhiều vùng, tám chấm chỉnh kích thước, Space để di chuyển |
| `Pipeline/PipelineCoordinator.swift` | `PipelineCoordinator.cs` | Kênh (`System.Threading.Channels`) thay cho actor; giữ kiểm tra hiện diện 600 ms, 2 lần trượt |
| `Pipeline/Preprocessor.swift` (Core Image) | `Preprocessor.cs` (Direct2D effects: Scale, Invert, Contrast) | Ảnh vùng nhỏ, xử lý trên GPU đã chọn |
| `Pipeline/VisionOCR.swift` | `WindowsOcr.cs`, `PaddleOcr.cs` | Ghép dòng từ trên xuống, bỏ dòng tin cậy thấp (PaddleOCR có điểm tin cậy; Windows OCR thì không) |
| `Pipeline/AppleTranslator.swift` | Không có ở 1.0 | Xem mục 9 |
| `Support/Keychain.swift` | `CredentialStore.cs` | Credential Manager |
| `Support/CPUMonitor.swift` | `ResourceMonitor.cs` | CPU, GPU (`D3DKMT`/PDH "GPU Engine"), VRAM của app |
| `Support/Log.swift` (`os.Logger`) | `Microsoft.Extensions.Logging` → file `%LOCALAPPDATA%\GameTranslator\logs` | Không ghi API key |
| `AppState.swift` | `MainViewModel` + các service | MVVM (CommunityToolkit.Mvvm) |
| `SelfTest.swift` (`--selftest`) | `--selftest` | Thêm kiểm tra liệt kê GPU, tạo device trên từng GPU, vẽ thử 3 chế độ phóng và đọc lại hướng ảnh |
| `~/Library/Application Support/GameTranslator/` | `%APPDATA%\GameTranslator\` (hồ sơ), `%LOCALAPPDATA%\GameTranslator\` (cache, log) | Bản portable: thư mục `data\` cạnh exe khi có file `portable.txt` |

### Phím tắt

| macOS | Windows | Chức năng |
|---|---|---|
| ⌘T | Ctrl+T | Bật/tắt dịch |
| ⌘R | Ctrl+R | Chọn vùng hộp thoại |
| ⌘⇧R | Ctrl+Shift+R | Dịch lại (bỏ qua cache) |
| ⌘O | Ctrl+O | Hiện/ẩn chữ gốc |
| ⌃⌘F | F11 (và Alt+Enter) | Toàn màn hình |
| ⌘Y | Ctrl+H | Lịch sử bản dịch (Ctrl+Y là "làm lại" trên Windows) |
| ⌘⇧P | Ctrl+Shift+P | Hồ sơ game |
| ⌘, | Ctrl+, | Cài đặt |

## 5. Cấu trúc thư mục

```
windows/
  PLAN.md
  CLAUDE.md                        Quy ước cho Claude Code
  README.md
  GameTranslator.slnx
  Directory.Build.props            Phiên bản đọc từ ../VERSION, cấu hình chung
  global.json                      Ghim SDK .NET 10
  src/
    GameTranslator.Core/           net10.0: Profile, ChangeDetector, TextNormalizer, PromptBuilder,
                                   RateLimiter, TranslationCache, Claude/Gemini/FallbackTranslator,
                                   PipelineCoordinator; interface ICaptureSource, IVideoRenderer,
                                   IOcrEngine, IAudioMonitor, ICredentialStore, IGpuCatalog
    GameTranslator.Platform.Windows/ net10.0-windows10.0.19041.0:
      Gpu/                         GpuCatalog (DXGI), GpuSelector, DeviceManager (mất device, đổi GPU)
      Capture/                     DeviceDiscovery, MfCaptureSource, MediaTypeSelector, DShowCaptureSource (sau)
      Render/                      D3DRenderer, Scalers (VideoProcessor, Fsr1, Nearest, Vsr), SubtitleRenderer
      Audio/                       AudioMonitor (WASAPI)
      Ocr/                         WindowsOcr, PaddleOcr (ONNX Runtime + DirectML)
      Support/                     CredentialStore, ResourceMonitor, GpuPreferenceRegistry, Mmcss
      Shaders/                     *.hlsl (FSR 1 EASU/RCAS, nearest, crop + thu nhỏ), biên dịch thành .cso lúc build
    GameTranslator.Platform.Fake/  Chạy UI và pipeline không cần capture card: phát lại ảnh mẫu, OCR giả đọc đáp án
    GameTranslator.App/            Avalonia: cửa sổ chính, chọn vùng, cài đặt, lịch sử, hồ sơ game; vi/en
  tests/
    GameTranslator.Core.Tests/     xUnit: chạy test vector + test riêng của C#
    GameTranslator.Windows.Tests/  Chỉ chạy trên Windows: GPU, render đọc lại texture, OCR với ảnh mẫu
    vectors/                       *.json xuất từ bộ test Swift (đã có, không sửa tay)
    fixtures/                      Ảnh hộp thoại mẫu + đáp án (tạo ở giai đoạn 4)
  installer/
    gametranslator.nsi             NSIS: cài per-user, shortcut, gỡ cài đặt
    assets/                        icon .ico, ảnh banner
  tools/
    ShaderCompiler/                Công cụ nhỏ gọi D3DCompile, chạy trong bước build
    test/                          Script PowerShell kiểm thử
  build_release.ps1                test → biên dịch shader → publish x64/arm64 → ký → NSIS → zip
```

## 6. Các giai đoạn

### Giai đoạn 0 — Chuẩn bị trên máy Windows (0,5 ngày)
- Clone repo về máy Windows thật có capture card (tốt nhất có cả GPU tích hợp và GPU rời để thử mục 3). Mở Claude Code ở **thư mục gốc repo** để đọc được cả `../macos`.
- Cài công cụ bằng `winget` (Claude nêu tên gói, người dùng đồng ý; kiểm tra id bằng `winget search` nếu khác):
  - .NET 10 SDK (`Microsoft.DotNet.SDK.10`)
  - NSIS (`NSIS.NSIS`)
  - Git (`Git.Git`), nếu chưa có
  - Windows SDK (cho `signtool` và công cụ gỡ lỗi), không bắt buộc ở giai đoạn đầu
  - PresentMon để đo độ trễ trình chiếu, không bắt buộc
- Kiểm tra: capture card hiện trong OBS ở 1080p60; ghi lại các định dạng card báo (thô hay MJPEG) và các GPU trên máy (`dxdiag` hoặc Task Manager > Performance).
- Kiểm tra Settings > Privacy & security > Camera > "Let desktop apps access your camera" đang bật, và có gói OCR tiếng Anh (`OcrEngine.AvailableRecognizerLanguages` có `en`).
- Tạo solution, `Directory.Build.props`, `global.json`. `.gitignore` ở gốc repo đã có `bin/`, `obj/`, `artifacts/`.

**Xong khi:** OBS thấy hình card ở 1080p60 và `dotnet build` chạy được.

### Giai đoạn 1 — Xuất test vector từ Swift (đã xong trên Mac)
Mục tiêu: một nguồn sự thật cho logic dùng chung. Đã có 7 file trong `tests/vectors/`:

| File | Nội dung |
|---|---|
| `text_normalizer.json` | Chuẩn hóa chuỗi OCR, Levenshtein, độ giống, `isSameSentence`, CER |
| `change_detector.json` | 9 kịch bản (ảnh xám nhỏ + thời điểm → sự kiện), độ lệch chuẩn, độ khác trung bình |
| `prompt_builder.json` | System instruction theo từ điển (cả thứ tự sắp xếp), nội dung câu có ngữ cảnh |
| `cache_key.json` | Khóa cache SHA-256 với chữ hoa, có dấu, ký tự đặc biệt, emoji |
| `rate_limiter.json` | 6 kịch bản theo phút/ngày, khôi phục bộ đếm, mốc nửa đêm và đổi giờ mùa hè ở Los Angeles |
| `profile.json` | Đọc hồ sơ (cũ, thiếu khóa, đủ khóa, khóa lạ), vùng, giới hạn số dòng, danh sách model gợi ý |
| `translators.json` | Body request Claude theo từng model, Gemini; đọc phản hồi; ánh xạ mã HTTP sang lỗi |

- Muốn xuất lại (chỉ trên Mac): `cd macos/Packages/TranslatorCore && GT_EXPORT_VECTORS=$(pwd)/../../../windows/tests/vectors swift test --filter VectorExportTests`.
- `FallbackTranslator` và `PipelineCoordinator` chưa có vector (phụ thuộc thời gian và bất đồng bộ): port test tay từ `TranslatorTests.swift` và mô tả hành vi ở mục 1.

### Giai đoạn 2 — Core C# (3 ngày)
- Port theo thứ tự: `Profile` → `TextNormalizer` → `ChangeDetector` → `PromptBuilder` → `TranslationCache` → `RateLimiter` → `ClaudeTranslator`/`GeminiTranslator` → `FallbackTranslator` → `PipelineCoordinator`.
- Khác biệt Swift ↔ C# cần chú ý:
  - `lowercased()` của Swift và `ToLowerInvariant()` có thể khác ở vài ký tự Unicode; chuẩn hóa NFC trước khi hash. Test vector cache key phải khớp 100%.
  - Regex: `NSRegularExpression` (ICU) và .NET Regex khác nhau ở lookbehind và lớp ký tự; kiểm bằng vector.
  - Actor → `Channel<T>` một người đọc; hủy dịch bằng `CancellationToken` theo "thế hệ" như bản Mac.
- `PipelineCoordinator` chỉ phụ thuộc interface, test được với capture/OCR/translator giả: nhiều vùng ghép chữ, kiểm tra hiện diện 600 ms, ẩn phụ đề khi đang dịch, nâng cấp bản offline.

**Xong khi:** 100% vector pass bằng `dotnet test`.

### Giai đoạn 3 — GPU, capture và hiển thị (5–6 ngày)
- `GpuCatalog`, `GpuSelector` (mục 3.1–3.3), lưu lựa chọn, theo dõi cửa sổ đổi màn hình cho chế độ Tự động.
- `MfCaptureSource`: liệt kê media type, chọn theo `MediaTypeSelector`, Source Reader async trên D3D11 device đã chọn, khung dạng texture NV12/YUY2.
- `D3DRenderer` trong `NativeControlHost`: swap chain flip model, vẽ khi khung đến, các chế độ phóng Mặc định / Mịn 4K (FSR 1) / Điểm ảnh, tùy chọn Đồng bộ dọc.
- `AudioMonitor` WASAPI riêng, âm lượng, chọn thiết bị ra.
- Rút/cắm lại card (`CM_Register_Notification` cho lớp thiết bị camera), mất device GPU, đổi GPU khi đang chạy.
- Thẻ góc video: độ phân giải, FPS thực, định dạng, GPU, trễ trong app.
- `--selftest`: tạo device trên từng GPU, vẽ thử ảnh nửa đỏ nửa xanh qua cả 3 chế độ, đọc lại texture để kiểm tra hướng ảnh (giống bài kiểm tra Metal của bản Mac).

**Xong khi:** trên máy Windows thật, chơi 30 phút không giật, thẻ báo 60 fps, cảm giác tay cầm **ngang OBS** (quay chậm bằng điện thoại cạnh OBS), tiếng khớp hình; đổi GPU trong Cài đặt không phải mở lại app.

### Giai đoạn 4 — OCR, dịch và phụ đề (3–4 ngày)
- Lấy khung cho OCR theo mục 3.6, nhiều vùng.
- `Preprocessor` bằng Direct2D effects; `WindowsOcr` (mặc định); `PaddleOcr` qua ONNX Runtime + DirectML là tùy chọn, có mục "GPU cho OCR".
- Nối `PipelineCoordinator` với translator thật; API key trong Credential Manager; nút "Thử" key.
- `SubtitleRenderer`: phụ đề rộng bằng vùng OCR, xuống dòng, 4 kiểu căn lề, số dòng tối đa, nền đen mờ, viền chữ, hiệu ứng mờ dần 150 ms, biểu tượng cache/offline/lỗi; vị trí trên/dưới/đè lên/tự do (kéo thả qua sự kiện chuột của cửa sổ native).
- Đo độ chính xác OCR trên bộ ảnh mẫu dùng chung với bản Mac. Nếu Windows OCR dưới 90% ký tự thì đẩy PaddleOCR lên thành mặc định cho máy có GPU rời.

**Xong khi:** chơi 1 giờ, phụ đề hiện dưới 1,5 giây với Claude Haiku; tắt mạng thì hiện chữ gốc, không treo; phụ đề tự ẩn khi đóng hộp thoại kể cả nền chuyển động.

### Giai đoạn 5 — Giao diện Avalonia (4–5 ngày)
Theo 5 màn hình trong file thiết kế, đổi chi tiết sang kiểu Windows (Fluent, phím Ctrl):
- **Cửa sổ chính**: thanh công cụ (chọn capture card, hồ sơ game, âm lượng, Chọn vùng, công tắc Dịch, toàn màn hình, cài đặt), khung video, thanh trạng thái (nguồn dịch, model, quota, OCR ms, dịch ms, CPU, **GPU**).
- **Chọn vùng**: ảnh tĩnh, nhiều vùng, Thêm vùng / Xóa vùng / Thử OCR các vùng / Lưu.
- **Cài đặt**: tab Chung (capture card, nguồn tiếng, thiết bị ra, âm lượng, **Card màn hình (GPU)**, **Hiển thị hình**, **Đồng bộ dọc**, nút đặt ưu tiên GPU của Windows, khung vùng OCR), Dịch (nhà cung cấp, key, model, chế độ, ngữ cảnh, hạn mức), OCR (engine Windows/PaddleOCR, GPU cho OCR), Phụ đề, Phím tắt.
- **Lịch sử**: bảng, sửa tay, dịch lại bằng AI online, xuất CSV.
- **Hồ sơ game**: vùng (xem trước nhiều vùng), phát hiện chữ mới, OCR, phụ đề (căn lề, số dòng), từ điển.
- Đa ngôn ngữ vi/en; DPI cao per-monitor v2; Dark/Light theo hệ thống cho các cửa sổ ngoài khung video.
- `--render-ui <thư mục>` bằng Avalonia.Headless (khung video thay bằng ảnh mẫu) xuất PNG để so với file thiết kế.
- Làm thêm file thiết kế `Game Translator Windows - Giao diện.html` cho các mục chỉ có trên Windows (GPU, đồng bộ dọc, OCR engine).

**Xong khi:** người khác dùng thử được mà không cần đọc mã; ảnh `--render-ui` khớp bố cục thiết kế.

### Giai đoạn 6 — Đóng gói (1–2 ngày)
- `build_release.ps1`:
  1. `dotnet test` (Core)
  2. Biên dịch HLSL thành `.cso`, nhúng làm resource
  3. `dotnet publish src/GameTranslator.App -c Release -r win-x64|win-arm64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true`
  4. Ký exe bằng `signtool` nếu có chứng chỉ (biến môi trường `SIGN_CERT`, `SIGN_PASSWORD`)
  5. `makensis /DARCH=x64|arm64 /DVERSION=<nội dung ../VERSION> installer/gametranslator.nsi`
  6. Nén portable, ra `artifacts/`: 2 bộ cài, 2 `.zip`, `SHA256SUMS`
- Gói ONNX Runtime DirectML (khoảng 20–40 MB mỗi kiến trúc) chỉ tải khi người dùng bật PaddleOCR, để bộ cài mặc định nhỏ.
- NSIS cài per-user vào `%LOCALAPPDATA%\Programs\GameTranslator`, không cần admin; đóng app đang chạy trước khi cài đè hoặc gỡ.
- Không ký số thì SmartScreen cảnh báo; ghi rõ trong README.

### Giai đoạn 7 — Kiểm thử trên Windows (3 ngày, song song với giai đoạn 5–6)
- `--selftest` trên máy thật (và máy ảo Windows ARM nếu có, chỉ cho phần không cần GPU/capture).
- Ma trận thủ công:

  | Nhóm | Trường hợp |
  |---|---|
  | GPU | Desktop 1 GPU rời; laptop Intel + NVIDIA (Optimus); laptop AMD APU + GPU rời; máy 2 màn hình nối 2 GPU khác nhau; Windows ARM (Snapdragon X, Adreno) |
  | Đổi GPU | Đổi trong Cài đặt khi đang chơi; kéo cửa sổ sang màn hình khác ở chế độ Tự động; cập nhật driver khi app đang chạy (mất device) |
  | Hiển thị | 3 chế độ phóng trên màn 1080p, 1440p, 4K; DPI 100/150/200%; toàn màn hình; Đồng bộ dọc bật/tắt; màn hình 120/144 Hz và VRR |
  | Capture | UGREEN, Elgato, card rẻ chỉ có MJPEG; USB 2 và USB 3; rút/cắm khi đang chạy |
  | Tiếng | Loa, tai nghe USB, Bluetooth; đổi thiết bị ra khi đang chạy |
  | Dịch | Claude, Gemini, hết credit, mất mạng, nhiều vùng, phụ đề dài 10 dòng |
  | Độ trễ | So với OBS bằng quay chậm; ghi số "trễ trong app" và PresentMon |

## 7. Thứ tự và mốc

| Mốc | Nội dung | Ước lượng |
|---|---|---|
| M1 | Giai đoạn 0 và 2: Core C# pass 100% vector (giai đoạn 1 đã xong) | ~3,5 ngày |
| M2 | Giai đoạn 3: hình + tiếng trên máy Windows thật, chọn GPU, 3 chế độ phóng, trễ ngang OBS | ~6 ngày |
| M3 | Giai đoạn 4: dịch và phụ đề chạy đầy đủ | ~4 ngày |
| M4 | Giai đoạn 5–7: đủ UI, bộ cài x64/arm64, qua ma trận kiểm thử → phát hành 1.0.0 | ~8 ngày |

Tổng: khoảng 21–22 ngày làm việc.

## 8. Rủi ro và cách xử lý

| Rủi ro | Cách xử lý |
|---|---|
| Không vẽ được control Avalonia đè lên khung video native ("airspace") | Vẽ phụ đề, khung vùng, thẻ trong swap chain bằng Direct2D (mục 2) |
| Laptop Optimus: GPU rời không nối màn hình, hình bị chép chéo GPU | Chế độ Tự động chọn GPU nối màn hình; ghi chú rõ trong Cài đặt; nút đặt ưu tiên GPU của Windows |
| Card capture chỉ có MJPEG ở 1080p60 | Giải mã MJPEG bằng phần cứng trên GPU đã chọn; thẻ góc ghi "MJPEG" để người dùng biết |
| Card không chạy ổn với Media Foundation | `DShowCaptureSource` dự phòng, chọn được trong Cài đặt |
| Văng khi đặt định dạng/frame rate không hỗ trợ (lỗi đã gặp trên Mac) | Chỉ chọn trong media type thiết bị liệt kê; bắt mọi `HRESULT` lỗi, báo trạng thái thay vì crash |
| Mất device GPU (driver cập nhật, TDR) | Tự dựng lại device, swap chain, Source Reader |
| Quyền camera của Windows tắt cho ứng dụng desktop | Nhận `E_ACCESSDENIED`, hiện hướng dẫn và nút mở trang cài đặt `ms-settings:privacy-webcam` |
| Windows OCR đọc font game kém hơn Apple Vision | Tiền xử lý theo hồ sơ; PaddleOCR + DirectML; đo bằng bộ ảnh mẫu ở giai đoạn 4 |
| Thiếu gói OCR tiếng Anh trên Windows tiếng Việt | Kiểm tra lúc mở app, hướng dẫn thêm ngôn ngữ English trong Settings |
| Không có dịch offline sẵn như Apple Translation | 1.0 chỉ online + chữ gốc; model cục bộ ở giai đoạn mở rộng |
| Không kiểm thử GPU/độ trễ được trên máy ảo; Claude không nhìn thấy màn hình | Làm trên máy Windows thật từ đầu; người dùng so với OBS và báo lại; Claude dựa vào `--selftest`, log, số đo trên thẻ góc video |
| Hash cache hoặc regex lệch giữa Swift và C# | Test vector dùng chung; lỗi mới thì thêm test vào Swift trước rồi xuất lại |
| Antivirus/SmartScreen nghi ngờ exe không ký | Ký số khi phát hành, README hướng dẫn |

## 9. Ngoài phạm vi 1.0 (giai đoạn mở rộng)

- **Dịch offline**: chạy model dịch Anh → Việt cục bộ qua ONNX Runtime + DirectML trên GPU đã chọn (ví dụ dòng Opus-MT en-vi; cần kiểm tra giấy phép, chất lượng, dung lượng).
- **Siêu phân giải của GPU**: NVIDIA RTX Video Super Resolution, Intel VSR (mục 3.4).
- HDR passthrough từ capture card 4K HDR.
- Chế độ chỉ phụ đề: cửa sổ trong suốt luôn nằm trên cùng, để chơi trên TV qua splitter mà vẫn đọc phụ đề.
- Gửi ảnh vùng hộp thoại cho Claude/Gemini đọc thay OCR.
- Cập nhật tự động, winget/Scoop, Microsoft Store (MSIX).
- Đồng bộ hồ sơ game giữa Mac và Windows (hiện chỉ xuất/nhập JSON thủ công, cùng schema).

## 10. Việc cần quyết định trước khi bắt đầu

1. Máy Windows dùng để kiểm thử: CPU, các GPU có trên máy, màn hình nối vào GPU nào.
2. Model capture card dùng trên Windows và định dạng nó báo (thô hay MJPEG ở 1080p60).
3. Có cần bản ARM64 ngay 1.0 không (Snapdragon X), hay chỉ x64 trước.
4. Chọn FSR 1 hay NIS làm "Mịn 4K" mặc định (quyết định sau khi so trên máy thật ở giai đoạn 3).
5. Có cần dịch offline ở 1.0 không (ảnh hưởng dung lượng bộ cài và thời gian).
6. Có mua chứng chỉ ký số không.

## 11. Giao việc cho Claude Code trên Windows

1. Trên Mac: đẩy repo lên GitHub (private). Trên Windows: cài Git và Claude Code, `git clone`, mở Claude Code ở thư mục gốc repo.
2. Giao từng giai đoạn, ví dụ:
   - "Đọc windows/CLAUDE.md và windows/PLAN.md. Làm giai đoạn 0 và 2: cài công cụ bằng winget, tạo solution, port Core sang C#, chạy test với tests/vectors. Báo lại."
   - "Làm giai đoạn 3. Mình đã cắm capture card và bật Switch." Sau đó chơi thử, so với OBS và báo cảm nhận cho Claude.
   - "Làm giai đoạn 4", "giai đoạn 5", "giai đoạn 6" lần lượt.
3. Không dán API key vào chat; nhập trong màn hình Cài đặt của app khi chạy.
4. Đồng ý commit sau mỗi giai đoạn để dễ quay lại khi có lỗi.
5. Cần đổi hành vi dùng chung (chuẩn hóa chuỗi, prompt, khóa cache…) thì sửa bên Mac trước, xuất lại vector, đẩy lên, rồi mới cho Claude bên Windows port.
