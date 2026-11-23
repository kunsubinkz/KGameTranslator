# Kế hoạch thực hiện: Game Translator cho macOS (Apple Silicon)

Phiên bản: 1.0 - 2026-11-23

Tài liệu này là bản macOS của kế hoạch Windows (`ke-hoach-game-translator.md`). Mục tiêu và luồng xử lý giống nhau, nhưng công nghệ đổi sang công cụ gốc của Apple để chạy nhanh, nhẹ và tận dụng chip M.

## 1. Mục tiêu

Xây dựng ứng dụng macOS gốc (native) cho máy Mac chip Apple Silicon (M1 trở lên):

- Nhận hình và tiếng từ capture card (ví dụ Nintendo Switch 2), phát ra màn hình và loa với độ trễ thấp.
- Đọc chữ tiếng Anh trong hộp thoại game bằng Apple Vision (chạy trên Neural Engine).
- Dịch sang tiếng Việt bằng Gemini API (Flash-Lite), chi phí khoảng 10.000đ/tháng. Có thêm chế độ dịch offline miễn phí bằng Apple Translation.
- Hiện phụ đề tiếng Việt đè lên hình game.

### Ngoài phạm vi (phiên bản 1)

- Không livestream, không ghi hình dài.
- Không dịch lời thoại lồng tiếng (để giai đoạn mở rộng).
- Không hỗ trợ Mac chip Intel.
- Không phát hành lên App Store, không notarize (dùng cá nhân).
- Không can thiệp vào bộ nhớ game hay vượt qua cơ chế bảo vệ nội dung (HDCP).

## 2. Kiến trúc tổng quan

```
Capture card ──┬─ Video ─→ [AVCaptureVideoPreviewLayer] ──→ Màn hình
               │               │ AVCaptureVideoDataOutput (lấy mẫu 2-4 lần/giây, chỉ vùng hộp thoại)
               │               ↓
               │         [Phát hiện thay đổi] → [Apple Vision OCR] → [Cache] → [Gemini / Apple Translation]
               │                                                                │
               │                                  [Phụ đề tiếng Việt đè lên hình] ←┘
               └─ Audio ─→ [AVCaptureAudioPreviewOutput] ──→ Loa/tai nghe
```

### Điểm khác biệt so với bản Windows

| Hạng mục | Windows | macOS |
|---|---|---|
| Ngôn ngữ | Python + PySide6 | Swift 6 + SwiftUI/AppKit |
| Thu hình, tiếng | Qt Multimedia | AVFoundation (`AVCaptureSession`) |
| Phát tiếng | `QAudioOutput` | `AVCaptureAudioPreviewOutput` (phát thẳng, trễ rất thấp) |
| OCR | Windows OCR / PaddleOCR | Apple Vision (`VNRecognizeTextRequest`) |
| Dịch offline | Không có | Apple Translation framework (miễn phí) |
| Lưu API key | Windows Credential Manager | Keychain |
| Đóng gói | PyInstaller `.exe` | Xcode build ra `.app` |

### Nguyên tắc thiết kế

1. **Hình và tiếng không bao giờ bị chặn bởi việc dịch.** Hiển thị qua preview layer của AVFoundation (GPU), xử lý OCR và dịch chạy trên queue và actor riêng.
2. **Làm ít nhất có thể.** Chỉ xử lý vùng hộp thoại, chỉ OCR khi vùng đổi, chỉ gọi API khi câu mới.
3. **Lỗi dịch không làm hỏng trải nghiệm.** Hết quota hoặc mất mạng thì chuyển sang Apple Translation offline, nếu vẫn lỗi thì hiện chữ gốc.
4. **Mọi thứ cấu hình được theo từng game** (vùng chữ, từ điển tên riêng, cỡ chữ).

### Luồng dữ liệu chi tiết

| Bước | Thành phần | Tần suất | Đầu ra |
|---|---|---|---|
| 1 | `AVCaptureSession` + preview layer | 60 fps | Hình hiển thị |
| 2 | `FrameSampler` (video data output) | 4 lần/giây | `CVPixelBuffer` vùng hộp thoại |
| 3 | `ChangeDetector` | 4 lần/giây | Sự kiện "chữ mới đã ổn định" |
| 4 | `Preprocessor` (Core Image) | Khi có sự kiện | Ảnh đã phóng to, tăng tương phản |
| 5 | `VisionOCR` | Khi có sự kiện | Chuỗi tiếng Anh |
| 6 | `TextNormalizer` + `TranslationCache` | Khi có sự kiện | Bản dịch có sẵn, hoặc yêu cầu dịch |
| 7 | `GeminiTranslator` / `AppleTranslator` | Tối đa 15 lần/phút (Gemini) | Chuỗi tiếng Việt |
| 8 | `SubtitleOverlay` | Khi có bản dịch | Phụ đề trên màn hình |

## 3. Yêu cầu

### Phần cứng

- Mac chip Apple Silicon (M1 trở lên), RAM 8 GB.
- Capture card USB chuẩn UVC hỗ trợ 1080p60, nhận được trên macOS không cần driver. Kiểm tra bằng QuickTime Player (File > New Movie Recording, chọn nguồn là capture card).
- Bộ chuyển USB-C nếu card dùng cổng USB-A. Nên cắm thẳng vào máy, tránh hub rẻ tiền vì dễ rớt khung.
- Khuyến nghị: bộ chia HDMI (splitter) nếu muốn chơi trên TV không trễ.

### Phần mềm và tài khoản

- macOS 15 (Sequoia) trở lên. Apple Translation framework cần macOS 15; nếu chỉ dùng Gemini thì macOS 14 vẫn được.
- Xcode bản mới nhất (từ App Store), Swift 6.
- `xcodegen` (cài qua Homebrew) để sinh file `.xcodeproj` từ `project.yml`, giúp Claude Code dễ sửa cấu hình dự án.
- API key Gemini từ Google AI Studio.
- Tài khoản Apple ID miễn phí trong Xcode để ký ứng dụng chạy trên máy mình (Sign to Run Locally cũng được).

### Yêu cầu phi chức năng

| Chỉ số | Mục tiêu |
|---|---|
| Độ trễ hình thêm vào so với nguồn | dưới 50 ms |
| Từ lúc chữ ổn định đến lúc hiện phụ đề | dưới 1,5 giây (Gemini), dưới 500 ms (Apple Translation), dưới 50 ms (cache) |
| CPU khi đang chơi | dưới 15% |
| RAM | dưới 300 MB |
| Độ chính xác OCR trên bộ ảnh mẫu | trên 95% ký tự |
| Chi phí API | dưới 10.000đ/tháng với khoảng 20 giờ chơi |

## 4. Công nghệ

| Hạng mục | Lựa chọn | Lý do |
|---|---|---|
| Ngôn ngữ | Swift 6 (strict concurrency) | Gốc của Apple, nhanh, an toàn luồng |
| Giao diện | SwiftUI cho cửa sổ và cài đặt, AppKit (`NSViewRepresentable`) cho khung video | SwiftUI viết nhanh; AppKit cần cho preview layer và toàn màn hình |
| Thu hình, tiếng | AVFoundation: `AVCaptureSession`, `AVCaptureDevice.DiscoverySession` (loại `.external`) | API chuẩn cho capture card UVC |
| Hiển thị hình | `AVCaptureVideoPreviewLayer` | Vẽ bằng GPU, trễ thấp nhất |
| Lấy khung | `AVCaptureVideoDataOutput`, `alwaysDiscardsLateVideoFrames = true` | Không làm chậm hiển thị |
| Phát tiếng | `AVCaptureAudioPreviewOutput` | Chỉ có trên macOS, phát thẳng tiếng thu vào ra loa |
| Xử lý ảnh | Core Image + Accelerate (vImage) | Cắt, thu nhỏ, so sánh khung bằng GPU/SIMD |
| OCR | Vision `VNRecognizeTextRequest`, ngôn ngữ `en-US` | Chính xác cao, chạy Neural Engine, miễn phí |
| Dịch chính | Gemini API qua REST (`URLSession`) | Rẻ, dịch tự nhiên, không cần SDK |
| Dịch offline | Apple Translation framework (`TranslationSession`) | Miễn phí, không cần mạng; cần tải gói tiếng Anh và tiếng Việt một lần |
| So khớp chuỗi | Hàm Levenshtein tự viết (nhỏ) | Không cần thêm thư viện |
| Cache | SQLite qua GRDB.swift (Swift Package) | Nhẹ, nhanh, ổn định |
| Cấu hình profile | JSON với `Codable` | Có sẵn trong Swift, dễ xuất/nhập |
| Lưu API key | Keychain (`Security` framework) | Chuẩn bảo mật của macOS |
| Phím tắt toàn cục | KeyboardShortcuts (sindresorhus, Swift Package) | Cho người dùng tự đặt phím |
| Log | `os.Logger` (Unified Logging) | Xem trong Console.app |
| Kiểm thử | Swift Testing (`import Testing`) | Có sẵn trong Xcode |
| Sinh dự án | XcodeGen (`project.yml`) | File cấu hình dạng chữ, dễ đọc và sửa |

### Quyền hệ thống cần khai báo

Trong `Info.plist`:

- `NSCameraUsageDescription`: "Ứng dụng cần truy cập capture card để hiển thị hình game."
- `NSMicrophoneUsageDescription`: "Ứng dụng cần truy cập âm thanh từ capture card để phát tiếng game."

Trong entitlements (nếu bật App Sandbox):

- `com.apple.security.device.camera`
- `com.apple.security.device.audio-input`
- `com.apple.security.network.client` (gọi Gemini API)

Thiếu các khai báo này thì ứng dụng sẽ bị macOS chặn hoặc crash khi mở capture card.

## 5. Cấu trúc thư mục

```
GameTranslator/
├── project.yml                     # cấu hình XcodeGen
├── README.md
├── CLAUDE.md                       # quy ước cho Claude Code
├── Config/
│   ├── Info.plist
│   └── GameTranslator.entitlements
├── Profiles/
│   ├── default.json
│   └── zelda-totk.json             # ví dụ profile theo game
├── Sources/GameTranslator/
│   ├── App/
│   │   ├── GameTranslatorApp.swift # @main, các Scene
│   │   └── AppState.swift          # @Observable, nối các thành phần
│   ├── Capture/
│   │   ├── DeviceDiscovery.swift   # liệt kê capture card, thiết bị âm thanh
│   │   ├── CaptureController.swift # AVCaptureSession: hình + tiếng
│   │   ├── FormatSelector.swift    # chọn độ phân giải, FPS
│   │   └── FrameSampler.swift      # AVCaptureVideoDataOutputSampleBufferDelegate
│   ├── Pipeline/
│   │   ├── PipelineCoordinator.swift # actor điều phối OCR + dịch
│   │   ├── ChangeDetector.swift
│   │   ├── Preprocessor.swift      # Core Image
│   │   ├── TextNormalizer.swift
│   │   ├── OCR/
│   │   │   ├── OCREngine.swift     # protocol
│   │   │   └── VisionOCR.swift
│   │   └── Translate/
│   │       ├── Translator.swift    # protocol
│   │       ├── GeminiTranslator.swift
│   │       ├── AppleTranslator.swift
│   │       ├── FallbackTranslator.swift # Gemini rồi Apple rồi chữ gốc
│   │       ├── PromptBuilder.swift
│   │       ├── RateLimiter.swift   # actor, token bucket
│   │       └── TranslationCache.swift # GRDB
│   ├── UI/
│   │   ├── MainWindow.swift
│   │   ├── VideoView.swift         # NSViewRepresentable chứa preview layer
│   │   ├── SubtitleOverlay.swift   # SwiftUI overlay
│   │   ├── RegionSelector.swift    # kéo chuột chọn vùng
│   │   ├── SettingsView.swift
│   │   ├── HistoryView.swift
│   │   └── StatusBar.swift
│   ├── Config/
│   │   ├── Profile.swift           # Codable models
│   │   └── ProfileStore.swift      # đọc/ghi ~/Library/Application Support/GameTranslator
│   └── Support/
│       ├── Keychain.swift
│       └── Log.swift
└── Tests/GameTranslatorTests/
    ├── Fixtures/Screenshots/       # ảnh hộp thoại mẫu + đáp án
    ├── ChangeDetectorTests.swift
    ├── TextNormalizerTests.swift
    ├── TranslationCacheTests.swift
    ├── RateLimiterTests.swift
    ├── PromptBuilderTests.swift
    └── OCRAccuracyTests.swift
```

## 6. Thiết kế chi tiết từng thành phần

### 6.1 Capture và phát hình, tiếng

- Liệt kê capture card bằng `AVCaptureDevice.DiscoverySession(deviceTypes: [.external], mediaType: .video, position: .unspecified)`. Thiết bị âm thanh dùng `.microphone` và lọc theo tên giống card.
- Trước khi mở thiết bị, gọi `AVCaptureDevice.requestAccess(for: .video)` và `.audio`, hiện hướng dẫn mở System Settings nếu bị từ chối.
- Chọn định dạng: duyệt `device.formats`, ưu tiên 1920x1080 có `videoSupportedFrameRateRanges` đạt 60 fps; đặt `activeVideoMinFrameDuration` và `activeVideoMaxFrameDuration` bằng 1/60.
- Một `AVCaptureSession` duy nhất gồm:
  - Input video và input audio của card.
  - `AVCaptureVideoPreviewLayer` để hiển thị.
  - `AVCaptureVideoDataOutput` để lấy khung, định dạng `kCVPixelFormatType_420YpCbCr8BiPlanarFullRange` (Vision nhận trực tiếp, không cần đổi màu).
  - `AVCaptureAudioPreviewOutput` để phát tiếng, có thanh chỉnh âm lượng (`volume`) và chọn thiết bị ra (`outputDeviceUniqueID`).
- Chạy `startRunning()` trên một serial queue riêng, không chạy trên main thread.
- Theo dõi `AVCaptureSession.wasInterruptedNotification` và `AVCaptureDevice.wasDisconnectedNotification` để báo khi rút card và tự kết nối lại khi cắm lại.
- Toàn màn hình bằng `NSWindow.toggleFullScreen`, giữ tỉ lệ bằng `videoGravity = .resizeAspect`, nền đen.
- Thanh trượt "Bù trễ âm thanh" (0-200 ms): nếu cần, chuyển phát tiếng sang `AVAudioEngine` có delay node. Mặc định dùng audio preview output vì trễ thấp nhất.

### 6.2 Frame sampler

- Delegate `captureOutput(_:didOutput:from:)` chạy trên queue riêng, chỉ xử lý 1 trên mỗi N khung (mặc định 15, tức 4 lần/giây ở 60 fps).
- Cắt vùng hộp thoại theo tọa độ tương đối 0..1, giữ ở dạng `CIImage` (không sao chép bộ nhớ).
- Nếu pipeline đang bận thì bỏ khung (luôn chỉ giữ khung mới nhất).

### 6.3 Change detector

Mục tiêu: chỉ OCR khi có chữ mới và chữ đã chạy xong.

- Thu nhỏ vùng còn khoảng 160 px chiều rộng, chuyển grayscale (vImage).
- Tính độ khác biệt trung bình tuyệt đối với khung trước bằng Accelerate (`vDSP`).
- Trạng thái:
  - `idle`: vùng không đổi.
  - `changing`: độ khác vượt ngưỡng `changeThreshold`.
  - `stable`: sau khi đổi, vùng giữ nguyên liên tục trong `stableMs` (mặc định 350 ms) thì phát sự kiện OCR.
- Bỏ qua nếu vùng gần như trống (độ lệch chuẩn thấp), tức hộp thoại đã đóng; ẩn phụ đề sau `hideDelayMs`.
- Viết dưới dạng `struct` thuần, nhận thời gian từ ngoài vào để dễ test.

### 6.4 Tiền xử lý và OCR

- Tiền xử lý bằng Core Image (chạy GPU): phóng to 2x (`CILanczosScaleTransform`), tùy chọn đảo màu (`CIColorInvert`), tăng tương phản (`CIColorControls`). Mỗi profile chọn bộ lọc phù hợp.
- Protocol chung:

```swift
protocol OCREngine: Sendable {
    var name: String { get }
    func recognize(_ image: CIImage) async throws -> OCRResult
}

struct OCRResult: Sendable, Equatable {
    let text: String
    let confidence: Float
    let elapsedMs: Double
}
```

- `VisionOCR`:
  - `VNRecognizeTextRequest`, `recognitionLevel = .accurate` (đổi sang `.fast` nếu cần nhanh hơn).
  - `recognitionLanguages = ["en-US"]`, `usesLanguageCorrection = true`.
  - `customWords` lấy từ từ điển tên riêng của profile để Vision nhận đúng tên nhân vật.
  - `minimumTextHeight` theo profile để bỏ chữ quá nhỏ (ví dụ chú thích nút bấm).
  - Ghép các dòng theo tọa độ từ trên xuống, bỏ dòng có độ tin cậy dưới 0,3.
- Chế độ "Gemini đọc ảnh" (giai đoạn mở rộng): gửi ảnh JPEG vùng hộp thoại cho Gemini, bỏ qua OCR.

### 6.5 Chuẩn hóa chuỗi và chống trùng

- Gộp các dòng thành một câu, bỏ khoảng trắng thừa, sửa lỗi OCR phổ biến (`|` thành `I`...).
- Bỏ biểu tượng nút bấm và mũi tên "tiếp tục" ở cuối hộp thoại.
- Bỏ qua chuỗi ngắn hơn 3 ký tự hoặc không có chữ cái.
- So với câu vừa dịch bằng tỉ lệ Levenshtein; giống trên 92% thì coi là cùng câu.
- Khóa cache: SHA-256 (CryptoKit) của `profileId + chuỗi chuẩn hóa viết thường`.

### 6.6 Cache

Bảng SQLite `translations` (GRDB, file trong `~/Library/Application Support/GameTranslator/cache.sqlite`):

| Cột | Kiểu | Ghi chú |
|---|---|---|
| `key` | TEXT PRIMARY KEY | hash như trên |
| `profileId` | TEXT | game nào |
| `source` | TEXT | chuỗi tiếng Anh |
| `target` | TEXT | chuỗi tiếng Việt |
| `engine` | TEXT | `gemini:<model>` hoặc `apple` |
| `createdAt` | DATETIME | UTC |
| `hits` | INTEGER | số lần dùng lại |

- Cache trong bộ nhớ (từ điển giới hạn 500 mục) để tra nhanh.
- Bản dịch Apple Translation có thể được thay bằng bản Gemini khi có quota (tùy chọn "nâng cấp bản dịch").
- Cho phép sửa bản dịch thủ công trong màn hình lịch sử.

### 6.7 Translator

Protocol chung:

```swift
protocol Translator: Sendable {
    var id: String { get }
    func translate(_ request: TranslationRequest) async throws -> String
}

struct TranslationRequest: Sendable {
    let text: String
    let context: [(source: String, target: String)]
    let glossary: [String: String]
}
```

**GeminiTranslator**

- Gọi REST: `POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent`, header `x-goog-api-key`.
- Tên model cấu hình được (mặc định một model Flash-Lite hiện hành), `temperature` khoảng 0,3, `maxOutputTokens` khoảng 200.
- `systemInstruction` (cố định):

```
Bạn là người dịch hội thoại trò chơi điện tử từ tiếng Anh sang tiếng Việt.
- Dịch tự nhiên, đúng giọng nhân vật, ngắn gọn để làm phụ đề.
- Giữ nguyên tên riêng theo bảng thuật ngữ bên dưới.
- Văn bản là kết quả OCR, có thể sai vài ký tự; hãy đoán nghĩa hợp lý.
- Chỉ trả về bản dịch, không giải thích.

Thuật ngữ:
Link = Link
Hyrule = Hyrule
...
```

- Nội dung mỗi request: 2-3 câu trước (kèm bản dịch) làm ngữ cảnh, và câu cần dịch.
- Gom câu: nhiều câu mới trong 500 ms thì gom vào một request, yêu cầu trả JSON (`responseMimeType = "application/json"`).
- Timeout 8 giây (`URLSessionConfiguration.timeoutIntervalForRequest`).

**AppleTranslator**

- Dùng Translation framework, cặp ngôn ngữ `en` sang `vi`. Cần kiểm tra lại tiếng Việt có trong danh sách ngôn ngữ được hỗ trợ trên phiên bản macOS đang dùng.
- Kiểm tra trước bằng `LanguageAvailability().status(from:to:)`; nếu chưa tải gói ngôn ngữ thì hiện nút "Tải gói dịch offline" (dùng `TranslationSession.prepareTranslation()`).
- Tùy phiên bản macOS, `TranslationSession` có thể phải lấy qua modifier `.translationTask` của SwiftUI thay vì khởi tạo trực tiếp. Gói vào một view ẩn và chuyển yêu cầu qua `AsyncStream`.
- Không hỗ trợ từ điển tên riêng: thay tên riêng bằng ký hiệu tạm (`⟦1⟧`) trước khi dịch rồi thay lại sau.

**FallbackTranslator**

- Thứ tự mặc định: Gemini, nếu lỗi hoặc hết quota thì Apple Translation, nếu vẫn lỗi thì hiện chữ gốc.
- Người dùng chọn chế độ: "Chỉ Gemini", "Chỉ offline", "Gemini, dự phòng offline".
- Xử lý lỗi Gemini:
  - 429 (hết quota): lùi thời gian theo cấp số (1s, 2s, 4s, tối đa 30s), chuyển sang offline trong lúc chờ.
  - Lỗi mạng, timeout: chuyển offline ngay, thử Gemini lại ở câu sau.
  - Phản hồi rỗng hoặc bị chặn: dùng offline.

### 6.8 Rate limiter

- `actor RateLimiter`, token bucket theo phút (mặc định 12 RPM, chừa biên dưới 15 RPM).
- Bộ đếm theo ngày (mặc định 450 RPD), đặt lại lúc nửa đêm giờ Thái Bình Dương (`TimeZone(identifier: "America/Los_Angeles")`); lưu vào SQLite.
- Khi đang chờ token, chỉ giữ câu mới nhất.
- Hiển thị: "Gemini còn X/450 request hôm nay".

### 6.9 Phụ đề

- `VideoView` (AppKit) chứa preview layer; phía trên là `SubtitleOverlay` (SwiftUI) trong cùng `ZStack`, nên phụ đề luôn đè lên hình kể cả toàn màn hình.
- Tính vị trí phụ đề theo khung hình thực (`previewLayer.layerRectConverted(fromMetadataOutputRect:)`) để không lệch khi có viền đen.
- Kiểu: font hệ thống SF Pro (hỗ trợ tiếng Việt tốt), viền chữ bằng shadow nhiều lớp, nền `.ultraThinMaterial` hoặc đen mờ 60%, tối đa 3 dòng.
- Vị trí: trên hộp thoại, dưới hộp thoại, đè lên hộp thoại, hoặc kéo thả tự do.
- Hiệu ứng mờ dần khi đổi câu (`.transition(.opacity)`, 150 ms).
- Biểu tượng trạng thái nhỏ: đang dịch, cache, offline, lỗi.

### 6.10 Chọn vùng và profile

- Nhấn phím tắt chọn vùng: chụp khung hiện tại làm ảnh tĩnh, kéo chuột khoanh vùng hộp thoại, nhấn Return để lưu.
- Profile JSON (lưu trong `~/Library/Application Support/GameTranslator/Profiles/`) ví dụ:

```json
{
  "id": "zelda-totk",
  "name": "Zelda: Tears of the Kingdom",
  "region": { "x": 0.18, "y": 0.74, "width": 0.64, "height": 0.20 },
  "ocr": {
    "level": "accurate",
    "scale": 2.0,
    "invert": false,
    "contrast": 1.2,
    "minimumTextHeight": 0.03
  },
  "detector": {
    "sampleEveryNFrames": 15,
    "changeThreshold": 6.0,
    "stableMs": 350,
    "hideDelayMs": 800
  },
  "translate": {
    "mode": "geminiWithOfflineFallback",
    "model": "gemini-flash-lite-latest",
    "contextLines": 3
  },
  "subtitle": { "position": "above", "fontSize": 28 },
  "glossary": { "Link": "Link", "Hyrule": "Hyrule", "Rupee": "Rupee" }
}
```

### 6.11 Phím tắt

Mặc định (người dùng đổi được trong cài đặt):

| Phím | Chức năng |
|---|---|
| `⌘T` | Bật/tắt dịch |
| `⌘R` | Chọn vùng hộp thoại |
| `⌘⇧R` | Dịch lại câu hiện tại (bỏ qua cache) |
| `⌘O` | Hiện/ẩn chữ gốc bên dưới bản dịch |
| `⌃⌘F` | Toàn màn hình |
| `⌘Y` | Mở lịch sử bản dịch |
| `⌘,` | Cài đặt |

## 7. Lộ trình thực hiện

Ước lượng cho một người làm bán thời gian. Mỗi giai đoạn có tiêu chí hoàn thành rõ ràng.

### Giai đoạn 0: Chuẩn bị (0,5 ngày)

- [ ] Cắm capture card, mở QuickTime Player kiểm tra có hình và tiếng.
- [ ] Kiểm tra Switch 2 xuất hình qua card ở 1080p60.
- [ ] Cài Xcode, chạy `xcode-select --install`, cài `xcodegen` bằng Homebrew.
- [ ] Đăng nhập Apple ID trong Xcode (Settings > Accounts) để ký ứng dụng.
- [ ] Tạo API key Gemini, thử một request bằng `curl`.
- [ ] Kiểm tra Apple Translation có cặp Anh sang Việt (thử trong app Dịch của macOS).
- [ ] Chụp 30-50 ảnh hộp thoại từ 2-3 game làm bộ ảnh mẫu, ghi lại chữ đúng.

**Hoàn thành khi:** thấy hình game trong QuickTime và một request Gemini trả về tiếng Việt.

### Giai đoạn 1: Capture và phát (2 ngày)

- [ ] `project.yml`, `Info.plist`, entitlements, `CLAUDE.md`; build được bằng `xcodebuild`.
- [ ] Xin quyền camera và micro, hướng dẫn khi bị từ chối.
- [ ] Liệt kê và chọn capture card, chọn định dạng 1080p60.
- [ ] Hiển thị bằng preview layer, phát tiếng bằng audio preview output, chỉnh âm lượng.
- [ ] Toàn màn hình, xử lý rút/cắm lại card.
- [ ] Đo độ trễ (quay chậm bằng điện thoại cảnh TV và màn hình Mac cạnh nhau).

**Hoàn thành khi:** chơi game qua app 30 phút không giật, tiếng khớp hình.

### Giai đoạn 2: Chọn vùng và OCR (2 ngày)

- [ ] Frame sampler trên queue riêng, bỏ khung khi bận.
- [ ] Công cụ chọn vùng, lưu vào profile.
- [ ] Change detector bằng Accelerate.
- [ ] Tiền xử lý Core Image và Vision OCR, `customWords` từ từ điển.
- [ ] Hiện chữ gốc OCR lên lớp phụ đề (chưa dịch) để kiểm tra.
- [ ] Test đo độ chính xác OCR trên bộ ảnh mẫu, tinh chỉnh tiền xử lý.

**Hoàn thành khi:** mỗi câu thoại mới chỉ OCR 1-2 lần, chữ gốc đúng trên 95%.

### Giai đoạn 3: Dịch (2-3 ngày)

- [ ] Chuẩn hóa chuỗi và chống trùng.
- [ ] Cache GRDB và cache bộ nhớ.
- [ ] `GeminiTranslator` với prompt, ngữ cảnh, từ điển, gom câu.
- [ ] `RateLimiter` theo phút và theo ngày.
- [ ] `AppleTranslator` và nút tải gói ngôn ngữ offline.
- [ ] `FallbackTranslator` và các chế độ dịch.
- [ ] Lưu API key trong Keychain, màn hình nhập key lần đầu.

**Hoàn thành khi:** chơi 1 giờ, phụ đề hiện dưới 1,5 giây; tắt Wi-Fi thì tự chuyển offline, không crash.

### Giai đoạn 4: Phụ đề và trải nghiệm (2 ngày)

- [ ] Kiểu phụ đề: font, viền, nền mờ, xuống dòng, vị trí, hiệu ứng.
- [ ] Thanh trạng thái: engine đang dùng, quota còn lại, độ trễ OCR và dịch.
- [ ] Phím tắt (KeyboardShortcuts).
- [ ] Màn hình lịch sử: xem và sửa bản dịch.
- [ ] Màn hình cài đặt (SwiftUI `Settings` scene).
- [ ] Hỗ trợ chế độ sáng/tối của macOS cho các màn hình ngoài khung video.

**Hoàn thành khi:** người khác dùng thử được mà không cần đọc mã.

### Giai đoạn 5: Profile và đóng gói (1 ngày)

- [ ] Quản lý nhiều profile, chọn nhanh khi mở app, xuất/nhập file JSON.
- [ ] Log bằng `os.Logger`, không ghi API key.
- [ ] Build bản Release (`xcodebuild -configuration Release`), chép `.app` vào thư mục Applications.
- [ ] Hướng dẫn mở app lần đầu khi chưa notarize (chuột phải > Open, hoặc cho phép trong System Settings > Privacy & Security).
- [ ] Viết README hướng dẫn cài đặt và cấu hình.

**Hoàn thành khi:** mở `.app` từ Applications, chọn profile và chơi được ngay.

### Giai đoạn 6: Mở rộng (tùy chọn)

- [ ] Chế độ "Gemini đọc ảnh" cho game Vision đọc kém.
- [ ] Nhiều vùng chữ (hộp thoại, tên nhân vật, menu).
- [ ] Dịch lời thoại lồng tiếng: thu âm thanh game qua `AVCaptureAudioDataOutput`, nhận dạng giọng nói bằng `SpeechAnalyzer`/`SFSpeechRecognizer` (offline) hoặc Gemini audio, rồi dịch.
- [ ] Chụp ảnh nhanh kèm phụ đề.
- [ ] Chế độ chỉ phụ đề: cửa sổ nhỏ luôn nằm trên cùng, để dùng với splitter khi chơi trên TV.
- [ ] Phát hành ngoài App Store: tài khoản Apple Developer, Developer ID, notarize.

**Tổng thời gian ước tính cho bản dùng được (giai đoạn 0-5):** khoảng 9-11 ngày làm việc.

## 8. Kiểm thử

| Loại | Nội dung | Cách làm |
|---|---|---|
| Unit test | change detector, text normalizer, cache, rate limiter, prompt builder | Swift Testing, dữ liệu giả, không cần phần cứng |
| Độ chính xác OCR | so kết quả Vision với đáp án trên bộ ảnh mẫu | test tính tỉ lệ ký tự đúng (CER), chạy được không cần capture card |
| Translator | dùng bản giả (fake) qua protocol | không gọi API thật trong test tự động |
| Thủ công | chơi thật 30-60 phút mỗi game mẫu | ghi nhận độ trễ, câu dịch sai, lỗi |
| Hiệu năng | CPU, RAM, GPU, năng lượng | Instruments (Time Profiler, Energy Log) |

Lệnh chạy test: `xcodebuild test -scheme GameTranslator -destination 'platform=macOS'`.

## 9. Chi phí

Giả định: khoảng 300 câu thoại mỗi giờ chơi, mỗi request khoảng 300 token vào và 50 token ra.

| Kịch bản | Chi phí ước tính |
|---|---|
| Chỉ Apple Translation (offline) | 0đ, không giới hạn, chất lượng thấp hơn Gemini |
| Gemini gói miễn phí, dự phòng offline | 0đ, Gemini đủ khoảng 1,5-3 giờ chơi mỗi ngày, sau đó tự chuyển offline |
| Gemini trả phí, OCR + văn bản | vài trăm đồng mỗi giờ, 10.000đ đủ khoảng 20 giờ trở lên |
| Gemini trả phí, gửi ảnh | gấp vài lần |

Cần kiểm tra lại bảng giá và hạn mức Gemini hiện hành, và đặt giới hạn ngân sách trong Google Cloud Billing.

## 10. Rủi ro và cách xử lý

| Rủi ro | Mức độ | Cách xử lý |
|---|---|---|
| Font pixel làm OCR sai | Trung bình (Vision tốt hơn Windows OCR) | Tiền xử lý theo profile, `customWords`, chế độ Gemini đọc ảnh |
| Capture card cần driver riêng, không nhận trên macOS | Trung bình | Chọn card UVC; kiểm tra bằng QuickTime ở giai đoạn 0 |
| Hub USB làm rớt khung | Trung bình | Cắm thẳng vào Mac |
| Capture card gây trễ hình khi chơi | Trung bình | Splitter HDMI, chơi trên TV, app chỉ đọc chữ |
| Quên khai báo quyền camera/micro, app crash | Trung bình | Kiểm tra `Info.plist` và entitlements ở giai đoạn 1 |
| Apple Translation không có tiếng Việt hoặc API khác giữa các bản macOS | Trung bình | Kiểm tra ở giai đoạn 0; nếu không có thì chỉ dùng Gemini và hiện chữ gốc khi lỗi |
| Hết quota Gemini giữa chừng | Thấp (có offline) | Rate limiter, cache, gom câu, dự phòng Apple Translation |
| Google đổi hạn mức, giá, tên model | Trung bình | Tên model và hạn mức để trong cài đặt |
| macOS chặn mở app chưa notarize | Thấp | Hướng dẫn mở lần đầu; build bằng Xcode trên chính máy dùng |
| Lộ API key | Thấp | Keychain, không ghi key vào log hay profile |

## 11. Việc cần quyết định trước khi bắt đầu

1. Model capture card cụ thể và đã nhận trong QuickTime chưa.
2. Phiên bản macOS đang dùng (quyết định có dùng được Apple Translation không).
3. Hai hoặc ba game dùng làm mẫu để tinh chỉnh OCR.
4. Thư mục lưu mã nguồn dự án (nên tách khỏi các dự án khác).
5. Bật App Sandbox hay không (dùng cá nhân thì có thể tắt cho đơn giản).

## 12. Gợi ý khi giao việc cho Claude Code

- Đặt file này vào `docs/ke-hoach-macos.md` trong thư mục dự án, giao từng giai đoạn một.
- Lần đầu: "Đọc docs/ke-hoach-macos.md. Làm giai đoạn 1: tạo project.yml, Info.plist, entitlements, CLAUDE.md, hiển thị hình và phát tiếng từ capture card. Build bằng xcodebuild và báo lại."
- Cắm capture card khi Claude chạy thử, và cho phép quyền camera/micro khi macOS hỏi.
- Dùng `git`, commit sau mỗi giai đoạn.
- Không dán API key vào chat; nhập key vào màn hình cài đặt của app khi chạy.
