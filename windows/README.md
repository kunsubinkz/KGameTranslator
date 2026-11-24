# Game Translator cho Windows

Hiện hình game từ capture card UVC kèm phụ đề tiếng Việt (OCR + dịch bằng Claude hoặc Gemini). Cùng định dạng hồ sơ game và cache với bản macOS ([../macos](../macos)). Mô tả chung cả hai bản và so sánh khác biệt: [../README.md](../README.md).

## Cài đặt

Lấy file trong `artifacts/` (sau khi chạy `build_release.ps1`):

| File | Dùng khi |
|---|---|
| `GameTranslator-<ver>-x64-setup.exe` | Windows thường (Intel/AMD) |
| `GameTranslator-<ver>-arm64-setup.exe` | Windows ARM (Snapdragon X) |
| `GameTranslator-<ver>-<arch>-portable.zip` | Không cài: giải nén chạy luôn, dữ liệu nằm trong thư mục `data\` cạnh exe |

Bộ cài chạy theo từng người dùng (không cần quyền admin), cài vào `%LOCALAPPDATA%\Programs\GameTranslator`. Gỡ cài đặt giữ lại hồ sơ game, cache và API key.

> Exe chưa ký số nên SmartScreen có thể cảnh báo ("Windows protected your PC"): chọn **More info → Run anyway**. Muốn ký: đặt `SIGN_CERT` (đường dẫn .pfx) và `SIGN_PASSWORD` trước khi chạy `build_release.ps1`.

## Yêu cầu

- Windows 10 (19041) trở lên, x64 hoặc ARM64.
- Gói OCR tiếng Anh: Settings > Time & language > Language & region > thêm English (United States).
- Camera: bật Settings > Privacy & security > Camera > "Let desktop apps access your camera".
- API key Claude hoặc Gemini: nhập trong **Cài đặt > Dịch** (lưu trong Windows Credential Manager, không ghi ra file).

## Dùng nhanh

1. Cắm capture card, bật máy chơi game, mở app. Card hiện trong ô chọn nguồn hình (ưu tiên 1080p 60 fps, định dạng thô).
2. **Chọn vùng** (Ctrl+R): kéo khung quanh hộp thoại; có thể thêm vùng phụ (ô tên nhân vật).
3. Nhập API key, bật **Dịch** (Ctrl+T).
4. Chưa có capture card? Chạy `GameTranslator.exe --demo` để thử với nguồn hình giả.

Phím tắt: Ctrl+T dịch, Ctrl+R chọn vùng, Ctrl+Shift+R dịch lại, Ctrl+O hiện chữ gốc, F11/Alt+Enter toàn màn hình, Ctrl+H lịch sử, Ctrl+L chỉnh nhanh phụ đề, Ctrl+Shift+P hồ sơ game, Ctrl+, cài đặt.

## Dịch offline (NLLB-200 chạy trên máy)

Mở **Ctrl+Shift+M** (hoặc Cài đặt > Dịch > Quản lý model local) rồi bấm **Cài đặt dịch offline**: app tự tải Python + PyTorch (theo card màn hình: Intel Arc, NVIDIA hoặc CPU) và model NLLB-200 về thư mục dữ liệu của app, rồi khởi động một host dịch cục bộ. Không cần cài Python, pip hay model bằng tay; tắt app giữa chừng thì lần sau bấm lại sẽ làm tiếp. Sau đó chọn chế độ dịch "Online, dự phòng offline" hoặc "Chỉ offline" trong Cài đặt > Dịch. Có thể trỏ tới host chạy ở máy khác (cùng giao thức) trong cùng mục đó.

Lưu ý: NLLB không dùng ngữ cảnh câu trước và từ điển game; bản dịch offline hiện nhãn "offline (NLLB)" và được nâng cấp lên bản online khi còn hạn mức. Model NLLB-200 theo giấy phép **CC-BY-NC 4.0 (phi thương mại)**; app không đóng gói model, người dùng tự tải từ Hugging Face. Chi tiết kỹ thuật: [PLAN_NLLB_LOCAL.md](PLAN_NLLB_LOCAL.md).

## Dòng lệnh

```powershell
GameTranslator.exe --selftest         # kiểm tra GPU, vẽ thử 3 chế độ phóng, OCR, Credential Manager; kết quả ở %LOCALAPPDATA%\GameTranslator\logs\selftest.txt
GameTranslator.exe --probe-capture    # mở thử từng định dạng của capture card và đo FPS thực
GameTranslator.exe --probe-live       # thô / MJPEG GPU giải mã / MJPEG CPU giải mã: FPS, độ trễ, CPU, OCR vùng hồ sơ; rồi chạy pipeline 6 giây (không gọi API dịch)
GameTranslator.exe --ocr-image <ảnh> [x y w h]   # OCR một ảnh chụp để dò lỗi đọc chữ
GameTranslator.exe --upscale-test <dir>   # phóng 2x một ảnh mẫu qua từng chế độ hiển thị, xuất PNG để so độ mịn
GameTranslator.exe --upscale-perf         # đo thời gian GPU khi phóng 1080p lên 4K trên từng card
GameTranslator.exe --render-ui <dir>  # xuất ảnh PNG các cửa sổ (không cần màn hình)
GameTranslator.exe --demo             # nguồn hình giả + bật dịch
GameTranslator.exe --nllb-selftest    # khởi động host NLLB (nếu đã cài), dịch 5 câu mẫu, in thiết bị và ms/câu; mã thoát 0 nếu ổn
```

## Phát triển

```powershell
dotnet build GameTranslator.slnx
dotnet test tests/GameTranslator.Core.Tests      # logic dùng chung, đối chiếu test vector xuất từ Swift
dotnet test tests/GameTranslator.Windows.Tests   # cần máy Windows thật có GPU
./build_release.ps1                              # test -> publish x64/arm64 -> NSIS -> zip -> SHA256SUMS (vào artifacts/)
```

Công cụ cần có: .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`), NSIS (`winget install NSIS.NSIS`). Xem [PLAN.md](PLAN.md) để biết kiến trúc và những gì chưa làm.

Log ở `%LOCALAPPDATA%\GameTranslator\logs\app.log`.

## Giấy phép bên thứ ba

Chế độ "Mịn 4K" dùng thuật toán AMD FidelityFX Super Resolution 1.0 (EASU, RCAS), Copyright (c) 2021 Advanced Micro Devices, Inc., giấy phép MIT (toàn văn trong `src/GameTranslator.Platform.Windows/Render/Shaders.cs`). Khử răng cưa dựa trên thuật toán FXAA của Timothy Lottes.
