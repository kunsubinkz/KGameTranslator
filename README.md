# Game Translator

Chơi game console qua **capture card USB**, app hiện hình game kèm **phụ đề tiếng Việt** theo thời gian thực: đọc chữ tiếng Anh trong hộp thoại bằng OCR, dịch bằng AI online (**Claude** mặc định, hoặc **Gemini**) và vẽ phụ đề đè lên hình. Có hai bản, cùng định dạng hồ sơ game và cache:

| | macOS | Windows |
|---|---|---|
| Thư mục | [macos/](macos) | [windows/](windows) |
| Hệ điều hành | macOS 15+ (Apple Silicon, tùy chọn Intel) | Windows 10 (19041) trở lên, x64 và ARM64 |
| Công nghệ | Swift, SwiftUI/AppKit, Metal | C# .NET 10, Avalonia UI, Direct3D 11 + Direct2D |
| Nhận hình | AVFoundation | Media Foundation (giải mã MJPEG/H.264 bằng GPU) |
| OCR | Apple Vision | Windows OCR (`Windows.Media.Ocr`) |
| Dịch offline | Apple Translation | NLLB-200 chạy trên máy qua host Python do app tự cài (Intel Arc, NVIDIA hoặc CPU); chưa cài thì hiện chữ gốc |
| Lưu API key | Keychain | Windows Credential Manager |
| Cách phát hành | ZIP + DMG | Bộ cài NSIS (cài theo người dùng, không cần admin) + bản portable `.zip` |
| Tình trạng | 1.0.0, dùng ổn định | 1.0.0, đã chạy thử trên Windows 11 x64 với capture card UGREEN; ARM64 mới build, chưa chạy thử; chưa ký số |

Tài liệu từng bản: [windows/README.md](windows/README.md) (cài đặt, dòng lệnh, phát triển), [windows/PLAN.md](windows/PLAN.md) (kiến trúc, những gì đã làm và chưa làm), [windows/CLAUDE.md](windows/CLAUDE.md) (quy ước cho Claude Code khi làm bản Windows). Thiết kế giao diện dùng chung: [Game Translator macOS - Giao diện.html](Game%20Translator%20macOS%20-%20Giao%20di%E1%BB%87n.html) (5 màn hình: cửa sổ chính, chọn vùng, cài đặt dịch, lịch sử, hồ sơ game). Kế hoạch ban đầu của bản macOS: [ke-hoach-game-translator-macos.md](ke-hoach-game-translator-macos.md) (viết khi còn dùng Gemini và một vùng OCR; một số chi tiết đã đổi).

## Tính năng chung

- **Nhận hình/tiếng từ capture card UVC** (ví dụ UGREEN, Elgato), ưu tiên 1920x1080 ở 60 fps, **ưu tiên định dạng thô** vì MJPEG/H.264 phải giải mã nên trễ hơn. Chỉ chọn frame rate nằm trong danh sách thiết bị báo. Hình và tiếng chạy riêng nhau để giảm trễ. Thẻ góc video hiện FPS thực nhận (cập nhật mỗi 2 giây), định dạng hình và độ trễ.
- **Hiển thị hình bằng GPU** với 3 chế độ: *Mặc định*, *Mịn 4K*, *Điểm ảnh* (nearest neighbor, 1080p lên 4K là đúng 2x).
- **OCR chỉ chạy khi chữ đã ổn định** (bộ phát hiện thay đổi). Khi đang hiện phụ đề, app OCR kiểm tra mỗi 600 ms và ẩn khi hai lần liền không thấy chữ, nên vẫn tự ẩn được kể cả nền game chuyển động.
- **Nhiều vùng OCR**: vùng chính và các vùng phụ (ví dụ ô tên nhân vật), chữ các vùng được ghép rồi dịch chung.
- **Dịch bằng AI online**:
  - Nhà cung cấp **Claude** (mặc định) hoặc **Gemini**, mỗi bên một API key riêng; model chọn bằng dropdown hoặc nhập tay.
  - Chế độ dịch: *Chỉ online*, *Online, dự phòng offline*, *Chỉ offline*.
  - Ngữ cảnh các câu trước, từ điển riêng cho từng game, cache SQLite, giới hạn request theo phút và theo ngày.
- **Phụ đề tiếng Việt**: rộng bằng vùng OCR, tự xuống dòng, căn trái/giữa/phải/đều, số dòng tối đa chỉnh được (mặc định 12), vị trí trên/dưới/đè lên hộp thoại hoặc tự do (kéo thả), chỉnh cỡ chữ và độ đậm nền. Lúc chờ dịch không hiện chữ gốc: phụ đề cũ ẩn đi, có bản dịch mới hiện (câu có trong cache hiện ngay).
- **Lịch sử bản dịch**: xem, sửa tay, dịch lại bằng AI online, xuất CSV.
- **Hồ sơ theo từng game**: vùng, OCR, phát hiện chữ mới, phụ đề, từ điển. Nhập/xuất JSON, dùng chung giữa Mac và Windows.

### Khác nhau giữa hai bản

| | macOS | Windows |
|---|---|---|
| Chế độ hiển thị | Mặc định (hệ thống), Mịn 4K (Lanczos + làm nét), Điểm ảnh | Mặc định (song tuyến), **Mịn 4K = AMD FSR 1 + khử răng cưa FXAA** (tự lùi về nhẹ hơn nếu GPU không kịp), Điểm ảnh |
| Chọn card màn hình | Hệ thống tự chọn | Tự động theo màn hình chứa cửa sổ, hiệu năng cao, tiết kiệm điện, hoặc chọn từng GPU; đổi khi đang chạy; tự dựng lại khi driver reset |
| Đồng bộ dọc | Theo hệ thống | Bật/tắt trong Cài đặt |
| OCR | Apple Vision | Windows OCR, có thêm tự cân sáng cho vùng bị tối (Switch tự giảm sáng) |
| Xuống dòng theo đoạn | Nối mọi dòng OCR thành một đoạn | Giữ ranh giới đoạn (tiêu đề, tên nhân vật, nội dung) và dặn model dịch mỗi đoạn một dòng |
| Chỉnh phụ đề nhanh | Hồ sơ game > Phụ đề | Nút "Phụ đề" (Ctrl+L) cạnh nút Dịch, chỉnh là thấy ngay trên video |
| Dịch offline | Có (Apple Translation, có sẵn trong hệ thống) | Có (NLLB-200, cần cài Python + PyTorch + model vài GB trong app, Ctrl+Shift+M; license model phi thương mại; chưa đo thực tế trên máy thật) |
| Ngôn ngữ giao diện | Tiếng Việt | Tiếng Việt |

## Cài đặt và build

### macOS

Yêu cầu: macOS 15+, Xcode (hoặc Command Line Tools) có Swift 5.9+.

```bash
cd macos
./build_release.sh                 # test, build Release, ký ad-hoc, ra ZIP + DMG trong build/release/
./build_release.sh --install       # thêm bước chép vào /Applications
./build_release.sh --universal     # arm64 + x86_64
./build_release.sh --skip-tests
```

Chạy khi phát triển: `cd macos && swift run GameTranslator`. Test: `cd macos/Packages/TranslatorCore && swift test`. Smoke test: `GameTranslator --selftest`. Biến môi trường phát hành: `SIGN_IDENTITY` (Developer ID), `NOTARY_PROFILE` (bật notarize). Mở app lần đầu bằng chuột phải > Open, cấp quyền Camera và Micro.

### Windows

Yêu cầu: .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`) và NSIS (`winget install NSIS.NSIS`), chạy trên máy Windows.

```powershell
cd windows
dotnet build GameTranslator.slnx
dotnet test tests/GameTranslator.Core.Tests      # logic dùng chung, đối chiếu test vector xuất từ Swift
dotnet test tests/GameTranslator.Windows.Tests   # cần máy Windows thật có GPU
./build_release.ps1                              # test -> publish x64/arm64 -> NSIS -> zip -> SHA256SUMS (vào artifacts/)
```

Ra `artifacts/`: `GameTranslator-<ver>-x64-setup.exe`, `...-arm64-setup.exe`, hai bản `-portable.zip` (dữ liệu nằm trong thư mục `data\` cạnh exe) và `SHA256SUMS`. Exe chưa ký số nên SmartScreen hoặc Smart App Control có thể cảnh báo/chặn; muốn ký đặt `SIGN_CERT` (.pfx) và `SIGN_PASSWORD`. Cần gói OCR tiếng Anh (Settings > Time & language > Language & region) và bật quyền camera cho ứng dụng desktop (Settings > Privacy & security > Camera). Dòng lệnh chẩn đoán (`--selftest`, `--probe-live`, `--ocr-image`, `--upscale-test`, `--render-ui`, `--demo`...) xem [windows/README.md](windows/README.md).

## Cách dùng (cả hai bản)

1. Mở app, chọn capture card, nguồn tiếng và thiết bị phát tiếng ở thanh trên. Chưa có capture card thì thử với nguồn hình giả (Windows: `GameTranslator.exe --demo`).
2. Cài đặt (⌘, hoặc Ctrl+,) > Dịch: chọn **nhà cung cấp AI** và **model**, dán **API key** (lưu trong Keychain / Credential Manager, không ghi ra file), bấm "Thử" để kiểm tra.
3. Chọn vùng hộp thoại (⌘R hoặc Ctrl+R): kéo khoanh vùng, có thể thêm vùng phụ. Mỗi game một hồ sơ riêng.
4. Bật Dịch (⌘T hoặc Ctrl+T).

### Chọn nhiều vùng

- Kéo trên chỗ trống để vẽ vùng mới, kéo bên trong để di chuyển, kéo tám chấm tròn để đổi kích thước.
- **Thêm vùng** để thêm vùng phụ, bấm vào một vùng để chọn nó, **Xóa vùng** để bỏ.
- **Thử OCR các vùng** đọc thử và ghép chữ của mọi vùng. Lưu vào hồ sơ khi xong.

### Phím tắt

| Chức năng | macOS | Windows |
|---|---|---|
| Bật/tắt dịch | ⌘T | Ctrl+T |
| Chọn vùng hộp thoại | ⌘R | Ctrl+R |
| Dịch lại (bỏ qua cache) | ⌘⇧R | Ctrl+Shift+R |
| Hiện/ẩn chữ gốc | ⌘O | Ctrl+O |
| Toàn màn hình | ⌃⌘F | F11 hoặc Alt+Enter (Esc để thoát) |
| Lịch sử bản dịch | ⌘Y | Ctrl+H |
| Hồ sơ game | ⌘⇧P | Ctrl+Shift+P |
| Cài đặt | ⌘, | Ctrl+, |
| Chỉnh nhanh phụ đề | (Hồ sơ game > Phụ đề) | Ctrl+L |

### API key và chi phí

| Nhà cung cấp | Lấy key ở | Ghi chú |
|---|---|---|
| Claude | console.anthropic.com | Trả trước bằng credit. Hết credit thì API dừng (không tự trừ thẻ nếu tắt Auto-reload), app tự lùi về dịch offline (macOS) hoặc hiện chữ gốc (Windows). Credit mua ở nơi khác (ví dụ Google) không dùng được. |
| Gemini | aistudio.google.com | Có gói miễn phí nhưng giới hạn rate thấp. |

| Nhà cung cấp | Model gợi ý |
|---|---|
| Claude | `claude-haiku-5-5` (mặc định, nhanh và rẻ nhất), `claude-sonnet-5-5`, `claude-opus-5-5` |
| Gemini | `gemini-flash-lite-latest` (mặc định), `gemini-flash-latest`, `gemini-pro-latest` |

App tự gửi tham số phù hợp từng model Claude (Haiku tắt suy nghĩ, Sonnet dùng `between_tools`, Opus hạ effort), không gửi `temperature`. Hồ sơ cũ lưu model Gemini trong trường `model` sẽ tự chuyển sang trường riêng và nhà cung cấp mặc định là Claude.

### Dữ liệu

| | macOS | Windows |
|---|---|---|
| Hồ sơ game (JSON) | `~/Library/Application Support/GameTranslator/Profiles/` | `%APPDATA%\GameTranslator\Profiles\` |
| Cache SQLite, log | `~/Library/Application Support/GameTranslator/` | `%LOCALAPPDATA%\GameTranslator\` |
| API key | Keychain | Credential Manager (`KGameTranslator/claude`, `KGameTranslator/gemini`) |

File hồ sơ JSON chép qua lại giữa hai bản được (cùng schema). Bản portable Windows dùng thư mục `data\` cạnh exe.

## Xử lý sự cố

- **Hình trễ so với OBS**: xem thẻ góc video. Nếu ghi MJPEG thì thẻ không có định dạng thô ở 1080p60: thử cổng USB 3 cắm thẳng (tránh hub) hoặc hạ xuống 1080p30 thô. Trên Windows, MJPEG vẫn mượt vì GPU giải mã; trên cả hai bản có thể đổi chế độ hiển thị để so độ trễ.
- **Hình giật / không đủ 60 fps**: xem thẻ FPS. Thẻ báo 30 thì thẻ hoặc cổng USB chỉ cho 30 fps. Báo 60 mà vẫn giật thì lỗi nằm ở phần hiển thị.
- **Không thấy phụ đề**: kiểm tra nút Dịch đã bật, đã chọn vùng đúng hộp thoại, đã nhập API key (nếu thiếu key và không có bản dịch offline thì app hiện chữ gốc). Dùng "Thử OCR các vùng" để xem OCR có đọc được không.
- **Windows tối hình thì OCR không đọc được**: app tự cân sáng; nếu vẫn lỗi, kiểm tra độ sáng màn hình Switch (Switch tự giảm sáng sau một lúc không bấm).
- **Lỗi hết credit / 429**: Claude hết credit hoặc Gemini hết hạn mức; app tự lùi về dịch offline (macOS) hoặc hiện chữ gốc (Windows).
- **Phụ đề không ẩn**: nền game chuyển động liên tục có thể làm app chậm nhận ra hết thoại (khoảng 1 đến 2 giây).
- **App văng khi cắm capture card (macOS)**: đã sửa lỗi đặt frame rate không nằm trong dải thiết bị hỗ trợ. Nếu vẫn văng, gửi crash report trong `~/Library/Logs/DiagnosticReports/`.
- **Windows báo "Windows chặn camera"**: bật Settings > Privacy & security > Camera > "Let desktop apps access your camera".
- **Windows chặn chạy file ("An Application Control policy has blocked this file")**: Smart App Control chặn file chưa ký số. Cách xử lý: ký số exe, hoặc tự cân nhắc tắt Smart App Control (không thể bật lại sau khi tắt).

## Cấu trúc

```
README.md                          # tài liệu tổng (file này)
VERSION                            # phiên bản dùng chung cho cả hai bản
Game Translator macOS - Giao diện.html
ke-hoach-game-translator-macos.md
macos/
├── Package.swift                  # target GameTranslator (App/)
├── App/                           # SwiftUI/AppKit: Capture, Pipeline, UI, Support
├── Packages/TranslatorCore/       # logic thuần + test: ChangeDetector, TextNormalizer, cache, ClaudeTranslator, GeminiTranslator, Fallback
├── Resources/                     # Info.plist, entitlements, AppIcon.icns
├── tools/make_icon.swift
└── build_release.sh
windows/
├── GameTranslator.slnx
├── src/
│   ├── GameTranslator.Core/              # logic thuần (port của TranslatorCore) + PipelineCoordinator, không phụ thuộc Windows
│   ├── GameTranslator.Platform.Windows/  # GPU (DXGI), capture (Media Foundation), vẽ (D3D11 + Direct2D), tiếng (WASAPI), OCR, Credential Manager
│   └── GameTranslator.App/               # giao diện Avalonia: cửa sổ chính, chọn vùng, cài đặt, lịch sử, hồ sơ game
├── tests/
│   ├── vectors/                          # test vector xuất từ Swift (không sửa tay)
│   ├── GameTranslator.Core.Tests/        # chạy được cả trên Mac
│   └── GameTranslator.Windows.Tests/     # cần máy Windows thật có GPU
├── installer/gametranslator.nsi
├── build_release.ps1
├── PLAN.md
└── CLAUDE.md
```

## Giữ hai bản đồng bộ

- Bản macOS là **chuẩn hành vi** của logic dùng chung: chuẩn hóa chuỗi OCR, bộ phát hiện thay đổi, prompt, khóa cache, giới hạn request, tham số request Claude/Gemini, schema hồ sơ.
- `windows/tests/vectors/*.json` được xuất từ mã Swift bằng `VectorExportTests.swift` (chỉ chạy được trên Mac): `cd macos/Packages/TranslatorCore && GT_EXPORT_VECTORS=$(pwd)/../../../windows/tests/vectors swift test --filter VectorExportTests`. Bản C# phải pass 100% các vector này.
- Muốn đổi hành vi dùng chung: sửa Swift trước, xuất lại vector, rồi mới port sang C#. Chỉ phần riêng của từng nền tảng (hiển thị, OCR, GPU, giao diện) mới được khác nhau; những chỗ Windows đã chủ động khác macOS được ghi ở mục 0 của [windows/PLAN.md](windows/PLAN.md) (ví dụ giữ ranh giới đoạn, tự cân sáng OCR, FSR + FXAA).

## Giấy phép bên thứ ba

Chế độ "Mịn 4K" trên Windows dùng thuật toán AMD FidelityFX Super Resolution 1.0 (EASU, RCAS), Copyright (c) 2021 Advanced Micro Devices, Inc., giấy phép MIT (toàn văn trong `windows/src/GameTranslator.Platform.Windows/Render/Shaders.cs`). Khử răng cưa dựa trên thuật toán FXAA của Timothy Lottes.
