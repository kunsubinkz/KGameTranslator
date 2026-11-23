# Game Translator (macOS)

Ứng dụng macOS (Apple Silicon) nhận hình và tiếng từ capture card, đọc chữ tiếng Anh trong hộp thoại game bằng Apple Vision, dịch sang tiếng Việt bằng AI online (**Claude** mặc định, hoặc **Gemini**; dự phòng Apple Translation offline) và hiện phụ đề đè lên hình.

Tài liệu liên quan:
- Thiết kế giao diện: [Game Translator macOS - Giao diện.html](Game%20Translator%20macOS%20-%20Giao%20di%E1%BB%87n.html) (5 màn hình: cửa sổ chính, chọn vùng, cài đặt dịch, lịch sử, hồ sơ game).
- Bản Windows (đang lên kế hoạch, làm trên máy Windows bằng Claude Code): [windows/PLAN.md](windows/PLAN.md), quy ước cho Claude ở [windows/CLAUDE.md](windows/CLAUDE.md), test vector dùng chung ở `windows/tests/vectors/`.
- Kế hoạch ban đầu: [ke-hoach-game-translator-macos.md](ke-hoach-game-translator-macos.md) (viết khi còn dùng Gemini, một vùng OCR; một số chi tiết đã đổi theo README này).

## Tính năng chính

- **Nhận hình/tiếng từ capture card UVC** (ví dụ UGREEN, Elgato), ưu tiên 1920x1080 ở 60 fps. Thẻ góc phải video hiện **FPS thực nhận được** (cập nhật mỗi 2 giây) và định dạng hình (thô hay MJPEG/H.264). App ưu tiên định dạng thô vì MJPEG phải giải mã nên trễ hơn. Hình và tiếng chạy ở hai session riêng để giảm trễ.
- **Hiển thị hình** (Cài đặt > Chung): *Mặc định (hệ thống)*, *Mịn 4K* (Lanczos + làm nét, vẽ bằng GPU) hoặc *Điểm ảnh* (nearest neighbor, 1080p lên 4K là đúng 2x). Hai chế độ sau vẽ từng khung bằng Metal ngay khi khung đến.
- **OCR bằng Apple Vision**, chỉ chạy khi chữ trong vùng đã ổn định.
- **Dịch bằng AI online hoặc offline**:
  - Nhà cung cấp: **Claude** (mặc định) hoặc **Gemini**, mỗi bên một API key riêng trong Keychain.
  - Model chọn bằng dropdown, hoặc nhập tên khác bằng "Khác…".
  - Chế độ dịch: *Chỉ online*, *Online, dự phòng offline*, *Chỉ offline (Apple)*.
  - Có ngữ cảnh các câu trước, từ điển riêng cho từng game, cache SQLite, giới hạn request theo phút và theo ngày.
- **Nhiều vùng OCR**: ngoài vùng chính có thể thêm vùng phụ (ví dụ ô tên nhân vật). Chữ của các vùng được ghép lại rồi dịch chung.
- **Phụ đề tiếng Việt**:
  - Rộng đúng bằng vùng OCR (với nhiều vùng thì bằng hình chữ nhật bao quanh), tự xuống dòng trong chiều rộng đó.
  - Vị trí: trên, dưới, đè lên hộp thoại, hoặc tự do (kéo thả).
  - Căn lề chữ: trái, giữa, phải hoặc đều hai mép; số dòng tối đa chỉnh được (mặc định 12). Chỉnh trong Hồ sơ game > Phụ đề.
  - Không hiện chữ gốc kèm vòng xoay khi đang chờ dịch: phụ đề cũ ẩn đi, có bản dịch mới hiện (câu có trong cache hiện ngay).
  - **Tự ẩn khi hết thoại**, kể cả khi nền game chuyển động: lúc đang hiện phụ đề, app kiểm tra OCR định kỳ và ẩn khi liên tiếp không thấy chữ.
- **Lịch sử bản dịch**, sửa tay, dịch lại bằng AI online, xuất CSV.
- **Hồ sơ theo từng game**: vùng, OCR, phát hiện chữ mới, phụ đề, từ điển.

## Build

Yêu cầu: macOS 15+, Xcode (hoặc Command Line Tools) có Swift 5.9+.

```bash
cd macos
./build_release.sh                 # test, build Release, ký ad-hoc, ra ZIP + DMG trong build/release/
./build_release.sh --install       # thêm bước chép vào /Applications
./build_release.sh --universal     # arm64 + x86_64
./build_release.sh --skip-tests
```

Chạy khi phát triển: `cd macos && swift run GameTranslator`. Test: `cd macos/Packages/TranslatorCore && swift test`. Smoke test: `GameTranslator --selftest`.

Biến môi trường cho phát hành: `SIGN_IDENTITY` (Developer ID), `NOTARY_PROFILE` (bật notarize).

## Cách dùng

1. Mở app lần đầu bằng chuột phải > Open, cấp quyền Camera và Micro.
2. Chọn capture card, nguồn tiếng và thiết bị phát tiếng ở thanh trên.
3. Cài đặt (⌘,) > Dịch:
   - Chọn **Nhà cung cấp AI** (Claude hoặc Gemini) và **Model**.
   - Dán **API key** của nhà cung cấp đó (lưu trong Keychain), bấm "Thử" để kiểm tra. Hoặc chọn chế độ "Chỉ offline" và tải gói dịch Anh → Việt.
4. Nhấn ⌘R, kéo chuột khoanh vùng hộp thoại, Return để lưu. Mỗi game có một profile riêng.

### API key và chi phí

| Nhà cung cấp | Lấy key ở | Ghi chú |
|---|---|---|
| Claude | console.anthropic.com | Trả trước bằng credit. Hết credit thì API dừng (không tự trừ thẻ nếu tắt Auto-reload), app tự lùi về dịch offline. Credit mua ở nơi khác (ví dụ Google) không dùng được. |
| Gemini | aistudio.google.com | Có gói miễn phí nhưng giới hạn rate thấp. |

Model gợi ý trong dropdown:

| Nhà cung cấp | Model |
|---|---|
| Claude | `claude-haiku-5-5` (mặc định, nhanh và rẻ nhất), `claude-sonnet-5-5`, `claude-opus-5-5` |
| Gemini | `gemini-flash-lite-latest` (mặc định), `gemini-flash-latest`, `gemini-pro-latest` |

App tự gửi tham số phù hợp từng model Claude (Haiku tắt suy nghĩ, Sonnet dùng `between_tools`, Opus hạ effort), không gửi `temperature`.

Hồ sơ cũ lưu model Gemini trong trường `model` sẽ tự chuyển sang trường riêng và nhà cung cấp mặc định là Claude.

### Chọn nhiều vùng

Ở màn hình chọn vùng (⌘R):

- Kéo trên chỗ trống để vẽ vùng mới, kéo bên trong (hoặc giữ Space) để di chuyển, kéo tám chấm tròn để đổi kích thước.
- Bấm **Thêm vùng** để thêm vùng phụ, bấm vào một vùng để chọn nó, bấm **Xóa vùng N** để bỏ.
- **Thử OCR các vùng** đọc thử và ghép chữ của mọi vùng. Return để lưu vào hồ sơ.

### Phím tắt

| Phím | Chức năng |
|---|---|
| ⌘T | Bật/tắt dịch |
| ⌘R | Chọn vùng hộp thoại |
| ⌘⇧R | Dịch lại (bỏ qua cache) |
| ⌘O | Hiện/ẩn chữ gốc |
| ⌃⌘F | Toàn màn hình |
| ⌘Y | Lịch sử bản dịch |
| ⌘⇧P | Hồ sơ game (vùng, OCR, phụ đề, từ điển) |

Dữ liệu lưu tại `~/Library/Application Support/GameTranslator/` (profile JSON, cache SQLite).

## Xử lý sự cố

- **App văng khi cắm capture card**: đã sửa lỗi đặt frame rate không nằm trong dải thiết bị hỗ trợ (đã dùng đúng dải của định dạng được chọn). Nếu vẫn văng, gửi crash report trong `~/Library/Logs/DiagnosticReports/`.
- **Hình trễ so với OBS**: xem thẻ góc video, nếu ghi MJPEG thì thẻ không có định dạng thô ở 1080p60 (thử đổi cổng USB 3 hoặc hạ xuống 1080p30 thô). Thử thêm Cài đặt > Chung > Hiển thị hình = Mịn 4K để so độ trễ với chế độ hệ thống.
- **Hình giật / không đủ 60 fps**: xem thẻ FPS ở góc phải video. Nếu thẻ báo 30 thì thẻ hoặc cổng USB chỉ cho 30 fps (thử cổng USB 3 cắm thẳng, tránh hub). Nếu báo 60 mà vẫn giật thì lỗi nằm ở phần hiển thị.
- **Lỗi hết credit / 429**: Claude hết credit hoặc Gemini hết hạn mức; app tự dùng bản dịch offline nếu chế độ có dự phòng.
- **Phụ đề không ẩn**: nền game chuyển động liên tục có thể làm app chậm nhận ra hết thoại (khoảng 1 đến 2 giây).

## Cấu trúc

```
macos/
├── Package.swift                  # target GameTranslator (App/)
├── App/                           # SwiftUI/AppKit: Capture, Pipeline, UI, Support
├── Packages/TranslatorCore/       # logic thuần + test: ChangeDetector, TextNormalizer, cache, ClaudeTranslator, GeminiTranslator, Fallback
├── Resources/                     # Info.plist, entitlements, AppIcon.icns
├── tools/make_icon.swift
└── build_release.sh
```
