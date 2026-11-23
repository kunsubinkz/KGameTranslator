# CLAUDE.md — Game Translator cho Windows

Hướng dẫn cho Claude Code khi làm việc trong thư mục `windows/`. Đọc file này và [PLAN.md](PLAN.md) trước khi bắt đầu.

## Bối cảnh

- Đây là bản Windows của Game Translator. Bản macOS đã chạy ổn ở [../macos](../macos) và là **chuẩn hành vi**: khi phân vân, đọc mã Swift tương ứng (bảng ánh xạ ở PLAN.md mục 4).
- Người dùng chơi Nintendo Switch 2 qua capture card USB, app hiện hình game kèm phụ đề tiếng Việt. Độ trễ hình và tiếng phải ngang OBS: đây là yêu cầu quan trọng nhất.
- Người dùng giao tiếp bằng tiếng Việt. Trả lời, chú thích trong mã và chuỗi giao diện đều bằng tiếng Việt (giống mã macOS).

## Cách làm việc

- Làm **từng giai đoạn** theo PLAN.md mục 6. Hết mỗi giai đoạn: build, chạy test, cập nhật bảng trạng thái ở PLAN.md mục 0 (ghi rõ cái gì đã kiểm chứng được, cái gì chưa), rồi báo lại.
- Khác với kế hoạch thì ghi vào PLAN.md mục 0, phần "Khác với kế hoạch ban đầu", kèm lý do.
- Chỉ commit khi người dùng đồng ý. Gợi ý commit sau mỗi giai đoạn.
- Không thêm tính năng ngoài kế hoạch khi chưa hỏi.

## Lệnh

```powershell
dotnet build GameTranslator.slnx
dotnet test tests/GameTranslator.Core.Tests
dotnet test tests/GameTranslator.Windows.Tests          # cần máy Windows thật có GPU
dotnet run --project src/GameTranslator.App
dotnet run --project src/GameTranslator.App -- --selftest
./build_release.ps1                                      # test → publish x64/arm64 → NSIS → zip vào artifacts/
```

Khi chưa có các file trên (giai đoạn 0), tạo theo PLAN.md mục 5.

## Test vector (nguồn sự thật cho logic dùng chung)

- `tests/vectors/*.json` được xuất từ mã Swift trên máy Mac bằng `VectorExportTests.swift`. **Không sửa tay các file này.**
- `GameTranslator.Core.Tests` phải đọc và pass 100% vector:
  - So JSON theo cấu trúc (thứ tự khóa không quan trọng), số thực sai số 1e-9.
  - So chuỗi bằng `StringComparison.Ordinal`; chuẩn hóa NFC khi băm khóa cache.
  - Các trường bắt đầu bằng `_` (như `_note`, `_generatedBy`) là chú thích, bỏ qua khi so.
  - Lỗi dạng `network:<thông điệp>` thì phải khớp cả thông điệp.
- Vector ghi đúng hành vi hiện tại, kể cả chỗ kỳ quặc (ví dụ `"|t's a |ong way |"` → `"|t's a |ong way I"`). Port đúng như vậy, đừng "sửa giúp".
- Muốn đổi hành vi dùng chung thì phải sửa Swift trước rồi xuất lại vector trên Mac. Máy Windows không làm được việc này: hãy báo người dùng.

## Phần cứng và những gì Claude không tự kiểm được

- Claude không nhìn thấy màn hình game. Hình có mịn không, tay cầm có trễ không, phụ đề có đẹp không: **hỏi người dùng** và nhờ so với OBS.
- Bằng chứng tự thu được thì dùng: `--selftest`, log, số liệu trên thẻ góc video (FPS thực, định dạng, GPU, trễ trong app), PresentMon nếu đã cài.
- Trước khi chạy thử phần capture (giai đoạn 3 trở đi), nhắc người dùng cắm capture card và bật Switch.
- Nếu Windows chặn camera (`E_ACCESSDENIED`), hướng dẫn người dùng bật Settings > Privacy & security > Camera > "Let desktop apps access your camera". Đừng tự sửa cài đặt bảo mật của hệ thống.

## An toàn và chi phí

- API key nằm trong Windows Credential Manager. Không in, không ghi log, không commit, không hỏi người dùng dán key vào chat. Người dùng tự nhập key trong màn hình Cài đặt của app.
- Test tự động dùng translator giả, không gọi Claude/Gemini thật. Chỉ gọi API thật khi người dùng yêu cầu (tốn tiền).
- Cài công cụ bằng `winget` thì nêu tên gói trước khi chạy. Không cài driver, không đổi cài đặt hệ thống ngoài `HKCU` của app.
- Ghi `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` chỉ khi người dùng bấm nút trong app, không tự ghi lúc khởi động.

## Quy ước mã

- C# .NET 10, `Nullable` + `TreatWarningsAsErrors`, file-scoped namespace, `LibraryImport` cho P/Invoke.
- `GameTranslator.Core` không phụ thuộc Windows (chạy test được cả trên macOS). Mọi thứ Windows nằm trong `GameTranslator.Platform.Windows` sau interface.
- Không chặn luồng capture/vẽ: OCR và dịch chạy ở luồng khác, khung đến khi đang bận thì bỏ.
- Không tự tạo frame rate hay media type: chỉ chọn trong danh sách thiết bị liệt kê (bản Mac từng văng vì việc này).
- Mỗi lỗi `HRESULT` ở phần capture/GPU phải thành trạng thái hiện trên thanh trạng thái, không làm app văng.
- Dữ liệu: hồ sơ ở `%APPDATA%\GameTranslator\Profiles`, cache và log ở `%LOCALAPPDATA%\GameTranslator`. JSON hồ sơ cùng schema với bản Mac (xem `tests/vectors/profile.json`).
