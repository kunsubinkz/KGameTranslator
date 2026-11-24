# Kế hoạch: dịch offline bằng NLLB-200 chạy local (host trong app)

Mục tiêu: bản Windows có **dịch offline thật** bằng model NLLB-200 chạy trên máy (GPU Intel Arc qua PyTorch XPU, hoặc CPU). App tự cài môi trường, tải model, khởi động và dừng một **host dịch** cục bộ, và dùng host đó làm bộ dịch offline trong pipeline hiện có. Ngoài ra cho phép trỏ tới một host chạy sẵn ở máy khác.

Tài liệu gốc: [../huong_dan_nllb.md](../huong_dan_nllb.md) (chạy `nllb_bench.py` bằng tay). Kế hoạch chính của bản Windows: [PLAN.md](PLAN.md) (mục 9 ghi "Dịch offline" là việc mở rộng; kế hoạch này thực hiện mục đó).

## 0. Trạng thái

| Giai đoạn | Trạng thái |
|---|---|
| N0. Đo thử bằng tay (quyết định đi tiếp hay không) | **Chưa làm** (cần tải vài GB; chờ bạn đồng ý). Máy dev chưa có Python/uv. Các phiên bản ghim trong `tools/nllb/requirements-*.txt` (torch 2.9.1, transformers 4.57.1...) là **giá trị tạm, chưa kiểm** |
| N1. Host Python `nllb_host.py` + giao thức HTTP | Đã viết (`tools/nllb/`). **Chưa chạy được lần nào** vì chưa có Python/torch trên máy; chỉ đã đọc lại logic |
| N2. Core: `LocalHostTranslator`, tách câu, danh mục model | Xong, 16 test mới pass (Core 55/55) |
| N3. Platform: môi trường Python, tải model, quản lý tiến trình host | Xong phần mã. Test pass: Job Object giết tiến trình con, chọn cổng trống, tải tiếp từ `.part` + kiểm SHA-256 với server giả (Windows 20/20). **Chưa thử thật**: `uv`, cài torch, tải từ Hugging Face, khởi động host |
| N4. Giao diện: màn hình "Model dịch local" + tab Dịch | Xong, đã xuất PNG bằng `--render-ui` (`artifacts/ui-nllb`); mới xem trạng thái "chưa cài", chưa xem trạng thái đã cài/đang tải. Chờ bạn duyệt |
| N5. Tích hợp pipeline, `--nllb-selftest`, kiểm thử thực tế | Đã nối vào `Session.ApplyProfile`, `--nllb-selftest` chạy đúng nhánh "chưa cài" (mã 3). **Ma trận thử với Switch chưa làm** |
| N6. Đóng gói, tài liệu | `build_release.ps1` tải `uv.exe` ghim 0.9.8 + kiểm SHA-256 (**chưa chạy script**), NSIS hỏi xóa `nllb\` khi gỡ, README đã cập nhật. Chưa thử trên máy sạch |

### Khác với kế hoạch ban đầu

- Bỏ tùy chọn "Dừng host khi thoát app": Job Object luôn giết host khi app thoát nên tùy chọn không có ý nghĩa.
- `nllb_host.py` và `requirements-*.txt` được chép cạnh exe bằng `Content` trong csproj (thư mục `nllb\`), không nhúng `EmbeddedResource`; app chép sang `nllb\host\` trong thư mục dữ liệu khi cài/khởi động.
- Nhập model có sẵn từ cache Hugging Face dùng tại chỗ (ghi đường dẫn vào `LocalTranslateSettings.ModelPaths`), không chép.
- Chưa có nút trên thanh công cụ; mở bằng Ctrl+Shift+M, thanh trạng thái (bấm vào chữ "NLLB: ...") hoặc Cài đặt > Dịch.
- "Host khác": đã hỗ trợ phía client (địa chỉ + token). Host Python vẫn chỉ nghe `127.0.0.1` mặc định (có `--host` để mở ra mạng, app chưa tự làm việc này).

## 1. Hiện trạng và chỗ cắm vào

Những gì đã có trong mã (không phải làm lại):

- `FallbackTranslator` (Core) đã có tham số `offline: ITranslator?` và 3 chế độ: Chỉ online, Online + dự phòng offline, Chỉ offline. Hiện `Session.ApplyProfile` truyền `null` nên chế độ offline chỉ hiện chữ gốc.
- Kết quả offline được gắn nhãn engine `"apple"` (`TranslationEngine.Apple`). `PipelineCoordinator` dựa vào `EngineKind.Apple` để **nâng cấp bản offline lên bản online** khi còn hạn mức. Bản macOS cũng hiện huy hiệu "Offline" theo đúng chuỗi `"apple"`.
- Tab Cài đặt > Dịch có dòng ghi chú "Bản Windows chưa có dịch offline...". Sẽ thay bằng phần cấu hình host.

Kết luận: chỉ cần viết một `ITranslator` mới gọi host, truyền vào chỗ `offline` của `FallbackTranslator`. Không đổi logic pipeline, không đổi schema hồ sơ, không phải xuất lại test vector.

> **Quyết định về nhãn engine:** bản dịch từ NLLB vẫn lưu cache với nhãn `"apple"` (nghĩa là "offline"). Lý do: cache dùng chung với bản Mac, và cả hai bản đều coi `"apple"` là offline để nâng cấp lên online. Thêm nhãn mới (ví dụ `nllb:600M`) thì phải sửa Swift và xuất lại vector trên Mac. Giao diện Windows hiện chữ "Offline (NLLB)" thay vì "apple".

Những điểm trong [../huong_dan_nllb.md](../huong_dan_nllb.md) cần chỉnh khi đưa vào app:

| Hướng dẫn ghi | Thực tế trên máy dev / khi tích hợp |
|---|---|
| Card Arc A580 | Máy dev là **Arc B580** 12 GB (+ UHD 770) |
| Cần Intel Extension for PyTorch (IPEX) để có `xpu` | PyTorch từ 2.5 hỗ trợ `xpu` sẵn (cài từ `https://download.pytorch.org/whl/xpu`). IPEX đang được Intel cho ngừng dần. Kiểm lại lúc làm N0 |
| `python3`, `export HF_HOME=...`, `rm -rf` | Lệnh kiểu macOS. App tự quản lý thư mục nên người dùng không cần gõ các lệnh này |
| Cần cài Python sẵn | Máy dev **chưa có Python** (lệnh `python` chỉ mở Microsoft Store). App phải tự cài (dùng `uv`, mục 3.1) |
| "Mô hình nghiên cứu" | Giấy phép NLLB-200 là **CC-BY-NC 4.0** (phi thương mại). App **không đóng gói model** trong bộ cài, người dùng tự tải từ Hugging Face trong app |

## 2. Kiến trúc

```
GameTranslator.exe (C#)                                 nllb_host.py (Python, tiến trình con)
┌──────────────────────────────────────┐   HTTP 127.0.0.1   ┌───────────────────────────────┐
│ PipelineCoordinator                  │   :47811 + token   │ ThreadingHTTPServer (stdlib)  │
│  └ FallbackTranslator                │ ─────────────────▶ │  /v1/health  /v1/translate    │
│      ├ online: Claude / Gemini       │                    │  /v1/load    /v1/shutdown     │
│      └ offline: LocalHostTranslator ─┤                    │ 1 hàng đợi → 1 luồng model    │
│                                      │                    │ transformers + torch (xpu/cpu)│
│ LocalHostManager (Platform.Windows)  │ ── start/stop ───▶ │                               │
│  ├ PythonEnvironment (uv, venv, pip) │   Job Object       └───────────────────────────────┘
│  ├ ModelDownloader (Hugging Face)    │
│  └ HostProcess (cổng, token, log)    │      Hoặc: host ở máy khác (cùng giao thức)
│ LocalModelWindow (Avalonia)          │ ─────────────────▶ http://192.168.1.x:47811
└──────────────────────────────────────┘
```

Vì sao dùng **host Python qua HTTP** thay vì chạy model ngay trong C#:

- Đúng như hướng dẫn đã chạy được (transformers + PyTorch), ít rủi ro nhất để có bản đầu.
- Tiến trình riêng: model lỗi, hết VRAM hay driver treo thì app (hình, tiếng) **không văng**. Hợp với quy tắc "lỗi thành trạng thái trên thanh trạng thái".
- Cùng giao thức dùng được cho host ở máy khác.
- Sau này muốn bỏ Python (ONNX Runtime + DirectML trong C#, xem mục 9) thì chỉ thay phía sau giao thức, không đụng pipeline.

Đánh đổi: môi trường Python + PyTorch XPU nặng vài GB và cài lâu. Bù lại bằng màn hình quản lý có tiến trình rõ ràng, cài một lần.

## 3. Thành phần

### 3.0 Đóng gói: cài một lần bấm, không cài gì riêng

Người dùng **không phải cài Python, pip hay model bằng tay**. Mọi thứ cần cho NLLB đều do app quản lý và nằm trong thư mục dữ liệu của app:

| Phần | Nằm ở đâu | Kích thước |
|---|---|---|
| `uv.exe`, `nllb_host.py`, `requirements-*.txt` | **Kèm trong bộ cài và bản zip** (cạnh `GameTranslator.exe`, thư mục `nllb\`) | ~20 MB |
| Python 3.12 + torch + transformers | Tải lần đầu, khi bấm "Cài đặt dịch offline" | vài GB (đo ở N0) |
| Model NLLB | Tải lần đầu, cùng nút trên | ~2,5 GB (600M) |

Lý do không nhét torch và model vào bộ cài:

- Torch có bản riêng cho từng loại GPU (Intel XPU, NVIDIA CUDA, CPU). Nhét vào thì phải làm 3 bộ cài, mỗi bộ vài GB, trong khi bộ cài hiện chỉ ~50 MB và người không dùng dịch offline cũng phải tải.
- Model NLLB theo giấy phép CC-BY-NC, không nên phát hành kèm app.

Luồng cài **một nút** (`LocalHostManager.InstallAllAsync`):

1. Lần đầu mở màn hình "Model dịch local", hoặc chọn chế độ "Chỉ offline" / "Online, dự phòng offline" trong Cài đặt mà chưa cài, app hiện:
   > Dịch offline chưa được cài. Cần tải khoảng **X GB** (Python + PyTorch cho Intel Arc B580 + model 600M) vào `C:\Users\...\GameTranslator\nllb`. Ổ C: còn trống Y GB. **[Cài đặt dịch offline]** [Đổi thư mục...]
2. Bấm một lần, app làm liền mạch: cài Python → cài gói theo GPU → tải model mặc định → khởi động host → dịch thử "Hello, traveler!". Một thanh tiến trình chung, ghi bước đang làm và số MB đã tải.
3. Xong thì host chạy luôn, phụ đề offline dùng được ngay; từ lần sau app tự khởi động host khi mở.
4. Mạng đứt hoặc tắt app giữa chừng: lần sau bấm lại thì làm tiếp từ bước còn thiếu, file đang tải dở được tải tiếp.
5. Gỡ app: bộ gỡ hỏi có xóa luôn `nllb\` không (lấy lại vài GB).

Các nút lẻ (cài lại môi trường, tải model khác, đổi thiết bị) vẫn có trong màn hình quản lý cho ai cần chỉnh, nhưng người dùng bình thường chỉ cần một nút trên.

### 3.1 Môi trường Python (`PythonEnvironment`)

Dùng [uv](https://github.com/astral-sh/uv) (một file exe, tự tải được Python, cài gói nhanh). Không đụng Python hệ thống, không cần quyền admin, không ghi PATH.

Các bước (bước 1 và 2 của luồng một nút ở mục 3.0):

1. `uv.exe` đã có sẵn cạnh exe (`build_release.ps1` tải bản **ghim phiên bản**, kiểm SHA-256 lúc build, NSIS đóng vào bộ cài). Bản ARM64 kèm `uv` bản ARM64.
2. `uv python install 3.12` với `UV_PYTHON_INSTALL_DIR=nllb\python` (Python nằm trong thư mục app).
3. `uv venv nllb\venv --python 3.12`.
4. Chọn bộ gói theo GPU (lấy từ `GpuCatalog` đã có):
   - Intel Arc → `requirements-xpu.txt` (torch từ index `whl/xpu`)
   - NVIDIA → `requirements-cuda.txt` (torch `whl/cu12x`)
   - AMD, Qualcomm, ARM64, không rõ → `requirements-cpu.txt`
   - Người dùng chọn lại được ("Thiết bị: Tự động / GPU Intel / GPU NVIDIA / CPU").
5. `uv pip install -r requirements-<backend>.txt`. Các file requirements **ghim phiên bản** (torch, transformers, sentencepiece, safetensors) và nằm cùng script host.
6. Chạy `python -I nllb_host.py check` để in JSON: phiên bản torch, có `xpu`/`cuda` không, tên thiết bị. Lưu kết quả vào `nllb\env.json`.

Mỗi bước in một dòng tiến trình lên màn hình (đọc stdout/stderr của uv). Bấm **Hủy** thì dừng tiến trình con. Cài dở thì lần sau làm lại từ bước còn thiếu. Có nút "Cài lại" (xóa `venv`) và "Gỡ môi trường".

### 3.2 Tải model (`ModelDownloader`)

C# tự tải file từ Hugging Face bằng `HttpClient` (không dùng cache `~/.cache/huggingface`), để có tiến trình, tạm dừng, tải tiếp và chọn ổ lưu:

- Lấy danh sách file: `GET https://huggingface.co/api/models/{repo}/tree/main`. Chỉ tải các file cần: `config.json`, `generation_config.json`, `tokenizer.json`, `tokenizer_config.json`, `special_tokens_map.json`, `sentencepiece.bpe.model`, và trọng số (`model.safetensors` nếu có, không thì `pytorch_model.bin`).
- Tải `https://huggingface.co/{repo}/resolve/main/{file}` vào file `.part`, tải tiếp bằng header `Range`, xong thì kiểm SHA-256 với `lfs.oid` trong danh sách file rồi mới đổi tên.
- Lưu dạng thư mục thường: `<thư mục model>\facebook--nllb-200-distilled-600M\`. Host nạp bằng `from_pretrained(<đường dẫn>)`.
- **Nhập model có sẵn**: nếu thấy `%USERPROFILE%\.cache\huggingface\hub\models--facebook--nllb-200-*` (đã tải theo hướng dẫn cũ) thì đề nghị dùng luôn thư mục snapshot đó, không tải lại.
- Thư mục lưu model chọn được (ví dụ ổ D:). Đổi thư mục thì hỏi chuyển model cũ hay không.

Danh mục model (`LocalModelCatalog`, Core):

| Model | Dung lượng tải | VRAM ước tính (fp16) | Ghi chú |
|---|---|---|---|
| `facebook/nllb-200-distilled-600M` | ~2,5 GB | ~1,5 GB | **Mặc định**, nhanh nhất |
| `facebook/nllb-200-distilled-1.3B` | ~5,5 GB | ~3 GB | Dịch tốt hơn, chậm hơn |
| `facebook/nllb-200-1.3B` | ~5,5 GB | ~3 GB | Bản không chưng cất |
| `facebook/nllb-200-3.3B` | ~17 GB | ~7 GB | Chỉ nên dùng nếu N0 cho thấy tốc độ chấp nhận được |

Con số trên là ước tính, cập nhật bằng số đo thật ở N0. Có ô nhập tên repo khác (bản fine-tune NLLB).

### 3.3 Host Python (`nllb_host.py`)

Một file, chỉ dùng thư viện chuẩn cho phần HTTP (`http.server.ThreadingHTTPServer`), không thêm FastAPI.

Lệnh:

```
python -I nllb_host.py check                         # in JSON thông tin torch/thiết bị
python -I nllb_host.py serve --port 47811 --model-dir <thư mục> --device auto --dtype auto
python -I nllb_host.py bench --model-dir <thư mục> --input cau.txt   # thay cho nllb_bench.py
```

Token đọc từ biến môi trường `GT_HOST_TOKEN` (không đưa vào dòng lệnh để không lộ trong danh sách tiến trình).

Giao thức (JSON, UTF-8, mọi request phải có `Authorization: Bearer <token>`; request có header `Origin` bị từ chối để trang web trong trình duyệt không gọi được vào host):

| Endpoint | Vào | Ra |
|---|---|---|
| `GET /v1/health` | | `{"version":1,"state":"idle\|loading\|ready\|error","model":"...","device":"xpu:0 Intel(R) Arc(TM) B580","dtype":"float16","error":null}` |
| `POST /v1/load` | `{"modelDir":"...","device":"auto","dtype":"auto"}` | `202`, theo dõi bằng `/v1/health` |
| `POST /v1/translate` | `{"texts":["..."],"src":"eng_Latn","tgt":"vie_Latn","beams":1,"maxNewTokens":256}` | `{"translations":["..."],"ms":123.4}`; `503` khi chưa sẵn sàng |
| `POST /v1/shutdown` | | `200` rồi thoát |

Chi tiết:

- Một luồng duy nhất chạy model, request xếp hàng. Pipeline chỉ dịch một câu mỗi lúc nên không cần song song.
- `dtype=auto`: GPU dùng `float16`, CPU dùng `float32`.
- Sau khi nạp model, dịch thử một câu để khởi động kernel GPU (lần đầu trên XPU thường chậm), rồi mới báo `ready`.
- `maxNewTokens` mặc định theo độ dài câu vào (khoảng 2 lần số token + 16, tối đa 256) để tránh model lặp từ vô hạn.
- Log ra stdout dạng JSON từng dòng; app ghi vào `logs\nllb-host.log`.

### 3.4 Quản lý tiến trình host (`HostProcess`, `LocalHostManager`)

- Chạy `venv\Scripts\python.exe -I nllb_host.py serve ...`, thư mục làm việc là `nllb\host\`.
- Gắn tiến trình vào **Job Object** với `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`: app văng hay bị tắt cưỡng bức thì host cũng tắt, không để lại tiến trình chiếm VRAM.
- Đặt độ ưu tiên tiến trình host là `BelowNormal` để không tranh CPU với luồng capture/vẽ (đã dùng MMCSS).
- Cổng mặc định `47811`, chỉ nghe `127.0.0.1`. Cổng bận thì chọn cổng trống và lưu lại.
- Token ngẫu nhiên tạo lúc cài, lưu trong Credential Manager (`KGameTranslator/nllb-host`), không ghi log.
- Trạng thái (sự kiện cho UI và thanh trạng thái): Chưa cài → Chưa có model → Đã dừng → Đang khởi động → Đang nạp model → Sẵn sàng → Lỗi (kèm 20 dòng log cuối).
- Tùy chọn "Tự khởi động host khi mở app" và "Dừng host khi thoát app" (mặc định bật cả hai). Host tự thoát thì thử lại tối đa 3 lần, quá thì báo lỗi.
- Dừng: gọi `/v1/shutdown`, chờ 5 giây, rồi mới kill.

### 3.5 Bộ dịch (`LocalHostTranslator`, Core)

- Thuộc `GameTranslator.Core` (chỉ dùng `HttpClient`, không phụ thuộc Windows) nên test được bằng `HttpMessageHandler` giả, giống cách test Claude/Gemini.
- Tách câu trước khi gửi (`SentenceSplitter`): đoạn thoại nhiều câu dịch theo từng câu trong một request (`texts` là mảng), ghép lại bằng dấu cách. NLLB dịch câu ngắn ổn định hơn dịch cả đoạn.
- Không dùng ngữ cảnh câu trước và từ điển game (NLLB không nhận prompt). Giao diện ghi rõ điều này.
- Ánh xạ lỗi sang `TranslationException` có sẵn: không kết nối được → `Unavailable("host chưa chạy")`, `503` → `Unavailable("đang nạp model")`, quá giờ (mặc định 10 giây) → `Timeout`, `401` → `Unavailable("sai token")`.
- `FallbackTranslator` đã bắt mọi lỗi của bộ offline và hiện chữ gốc, nên host chưa chạy không làm hỏng pipeline.

### 3.6 Cài đặt

Cấu hình host là **cài đặt của máy** (`AppSettings`), không theo hồ sơ game. Chế độ dịch (online/offline) vẫn theo hồ sơ như hiện nay.

```csharp
public sealed class LocalTranslateSettings
{
    public bool Enabled { get; set; } = true;            // dùng NLLB làm bộ dịch offline
    public LocalHostKind Host { get; set; } = LocalHostKind.Managed;  // Managed | External
    public string ExternalUrl { get; set; } = "";         // khi Host = External
    public int Port { get; set; } = 47811;
    public string ModelId { get; set; } = "facebook/nllb-200-distilled-600M";
    public string? ModelsDir { get; set; }                 // null = %LOCALAPPDATA%\GameTranslator\nllb\models
    public string Device { get; set; } = "auto";           // auto | xpu | cuda | cpu
    public string Backend { get; set; } = "auto";          // bộ gói đã cài: xpu | cuda | cpu
    public int Beams { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 10;
    public bool AutoStart { get; set; } = true;
    public bool StopOnExit { get; set; } = true;
}
```

Token của host ngoài lưu ở Credential Manager (`KGameTranslator/nllb-external`), giống API key.

Thư mục dữ liệu (bản portable thì nằm trong `data\` cạnh exe như hiện nay):

```
%LOCALAPPDATA%\GameTranslator\nllb\
  uv\uv.exe
  python\            Python do uv quản lý
  venv\              torch, transformers...
  host\              nllb_host.py + requirements-*.txt (app chép ra từ resource nhúng)
  models\            facebook--nllb-200-distilled-600M\ ... (đổi được sang ổ khác)
  env.json           backend, phiên bản torch, thiết bị đo được
```

## 4. Giao diện

### 4.1 Màn hình "Model dịch local" (cửa sổ mới `LocalModelWindow`)

Mở từ: nút trên thanh công cụ, Cài đặt > Dịch > "Quản lý model local...", phím tắt `Ctrl+Shift+M` (kiểm lại không trùng phím đã có).

```
┌ Model dịch local (NLLB-200) ──────────────────────────────────────────────┐
│ HOST                                                                      │
│  ● Sẵn sàng · nllb-200-distilled-600M · xpu:0 Arc B580 · fp16 · :47811    │
│  [ Dừng ]  [ Khởi động lại ]   ☑ Tự khởi động khi mở app  ☑ Dừng khi thoát│
│                                                                           │
│ MÔI TRƯỜNG                                                                │
│  Đã cài · Python 3.12 · torch 2.x+xpu · 4,1 GB      Thiết bị: [Tự động ▾] │
│  [ Cài lại ]  [ Gỡ ]                                                      │
│                                                                           │
│ MODEL                               Thư mục: D:\Models  [ Đổi... ]        │
│  ◉ nllb-200-distilled-600M   2,5 GB  ~1,5 GB VRAM   Đã tải    [ Xóa ]     │
│  ○ nllb-200-distilled-1.3B   5,5 GB  ~3 GB VRAM     ████░░ 62% [Tạm dừng] │
│  ○ nllb-200-3.3B             17 GB   ~7 GB VRAM     Chưa tải  [ Tải ]     │
│  [ Nhập model có sẵn... ]  [ Thêm repo khác... ]                          │
│                                                                           │
│ CHẠY THỬ                                                                  │
│  [Hello, traveler! Where are you headed?                ] [ Dịch ]        │
│  → Xin chào, lữ khách! Bạn đang đi đâu vậy?     (142 ms)                  │
│  [ Đo tốc độ (20 câu mẫu) ]  [ Đo bằng file... ]                          │
│  TB 138 ms · trung vị 131 · nhanh nhất 88 · chậm nhất 260                 │
│                                                                           │
│ ▸ Log host (20 dòng cuối)                       [ Mở thư mục log ]        │
└───────────────────────────────────────────────────────────────────────────┘
```

Quy tắc:

- Chưa cài thì màn hình chỉ hiện một khung lớn "Dịch offline chưa được cài" với nút **[Cài đặt dịch offline]** (mục 3.0); các mục trên chỉ hiện sau khi cài xong (hoặc khi bấm "Tùy chỉnh nâng cao").
- Nút chỉ bật khi hợp lệ (chưa cài môi trường thì "Khởi động" mờ, kèm dòng "Cần cài môi trường trước").
- Cài môi trường và tải model chạy nền; đóng cửa sổ không hủy việc đang chạy, mở lại vẫn thấy tiến trình.
- Mọi lỗi hiện thành dòng chữ đỏ trong mục tương ứng, không hộp thoại chặn.
- Trước khi tải: hiện dung lượng cần và dung lượng trống của ổ; thiếu chỗ thì không cho tải.
- Trước khi xóa model hoặc gỡ môi trường: hỏi lại, ghi rõ dung lượng sẽ lấy lại.
- Ô "Đo tốc độ" dùng 20 câu thoại mẫu nhúng sẵn (giống mục đích của `nllb_bench.py`), hoặc file người dùng chọn (mỗi dòng một câu). Kết quả có thể lưu ra file.
- Chuỗi giao diện bằng tiếng Việt, làm bằng mã C# như các cửa sổ khác (`Ui.Row`, `Ui.Combo`...).

### 4.2 Cài đặt > Dịch

Thay dòng "Bản Windows chưa có dịch offline..." bằng mục mới:

```
Dịch offline (model local)
  Dùng NLLB làm bộ dịch offline   ☑
  Host                [ Tự chạy trên máy này ▾ ]   (hoặc: Host khác)
  Địa chỉ host        [ http://192.168.1.20:47811 ]  (chỉ khi Host khác)
  Token               [ •••••• ]                     (chỉ khi Host khác)
  Model               [ nllb-200-distilled-600M ▾ ]  (các model đã tải)
  Số beam             [ 1 ]   1 nhanh nhất; 4 dịch tốt hơn nhưng chậm hơn
  Trạng thái          ● Sẵn sàng (xpu, 138 ms/câu)    [ Thử kết nối ]  [ Quản lý model local... ]
  Ghi chú: NLLB không dùng ngữ cảnh câu trước và từ điển game.
```

Gợi ý chế độ dịch hiển thị lại cho đúng: "Online, dự phòng offline" = Claude/Gemini trước, lỗi hoặc hết hạn mức thì NLLB; "Chỉ offline" = chỉ NLLB, không tốn tiền API.

### 4.3 Thanh trạng thái và phụ đề

- Thanh trạng thái thêm mục `NLLB: sẵn sàng` / `đang nạp` / `chưa chạy` / `lỗi` (chỉ hiện khi bật dịch offline).
- Khi chế độ "Chỉ offline": ô nhà cung cấp hiện `Offline (NLLB)` thay cho `Offline`.
- Biểu tượng nguồn bản dịch: nhãn engine `"apple"` hiện là `offline (NLLB)`.

## 5. Thay đổi theo file

| File | Việc |
|---|---|
| `tools/nllb/nllb_host.py` (mới) | Host HTTP, lệnh `check` / `serve` / `bench` |
| `tools/nllb/requirements-{xpu,cuda,cpu}.txt` (mới) | Gói ghim phiên bản |
| `src/GameTranslator.Core/LocalHost/LocalHostTranslator.cs` (mới) | `ITranslator` gọi `/v1/translate` |
| `src/GameTranslator.Core/LocalHost/LocalHostProtocol.cs` (mới) | Record JSON của giao thức |
| `src/GameTranslator.Core/LocalHost/SentenceSplitter.cs` (mới) | Tách câu (giữ "Mr.", "...", "?!") |
| `src/GameTranslator.Core/LocalHost/LocalModelCatalog.cs` (mới) | Danh mục model |
| `src/GameTranslator.Platform.Windows/LocalModel/PythonEnvironment.cs` (mới) | uv, Python, venv, pip, `check` |
| `src/GameTranslator.Platform.Windows/LocalModel/ModelDownloader.cs` (mới) | Tải Hugging Face, tải tiếp, kiểm SHA-256 |
| `src/GameTranslator.Platform.Windows/LocalModel/HostProcess.cs` (mới) | Chạy/dừng, cổng, token, log, trạng thái |
| `src/GameTranslator.Platform.Windows/Support/JobObject.cs` (mới) | `CreateJobObjectW`, `AssignProcessToJobObject` (`LibraryImport`) |
| `src/GameTranslator.App/LocalModelWindow.cs` (mới) | Màn hình quản lý (mục 4.1) |
| `src/GameTranslator.App/AppSettings.cs` | Thêm `LocalTranslateSettings`, đường dẫn `AppPaths.Nllb` |
| `src/GameTranslator.App/SettingsWindow.cs` | Mục "Dịch offline (model local)" ở tab Dịch |
| `src/GameTranslator.App/Session.cs` | Giữ `LocalHostManager`; `ApplyProfile` truyền `LocalHostTranslator` vào `offline`; tự khởi động host; trạng thái NLLB trong `Snapshot()` |
| `src/GameTranslator.App/MainWindow.cs` | Nút mở màn hình, phím tắt, trạng thái NLLB |
| `src/GameTranslator.App/Program.cs` | Cờ `--nllb-selftest` |
| `src/GameTranslator.App/GameTranslator.App.csproj` | Nhúng `tools/nllb/*` làm `EmbeddedResource` |
| `tests/GameTranslator.Core.Tests/LocalHostTests.cs` (mới) | Test bộ dịch + tách câu với handler giả |
| `tests/GameTranslator.Windows.Tests/WindowsTests.cs` | Test Job Object, chọn cổng trống |
| `build_release.ps1` | Tải `uv.exe` bản ghim (x64/ARM64), kiểm SHA-256, chép cùng `tools/nllb/*` vào thư mục `nllb\` cạnh exe trước khi chạy NSIS/zip |
| `installer/gametranslator.nsi` | Đóng thư mục `nllb\` vào bộ cài; khi gỡ cài đặt: hỏi có xóa dữ liệu `nllb\` (môi trường + model, vài GB) không |
| `PLAN.md` mục 0 và 9, `README.md` | Cập nhật trạng thái và cách dùng |

Không đổi: `FallbackTranslator`, `PipelineCoordinator`, `Profile`, `TranslationCache`, `tests/vectors/*`.

## 6. Các giai đoạn

### N0 — Đo thử bằng tay (0,5 ngày) — cổng quyết định

Trước khi viết mã trong app, chạy tay trên máy dev để biết có đáng làm không:

1. Tải `uv.exe` bản standalone vào một thư mục thử riêng (không cài vào hệ thống, giống cách app sẽ làm), dùng nó cài Python 3.12 và tạo venv trong thư mục đó, cài torch bản `xpu` + transformers + sentencepiece. Thử xong xóa thư mục là sạch.
2. `python -c "import torch; print(torch.xpu.is_available(), torch.xpu.get_device_name(0))"` phải ra Arc B580.
3. Chạy `nllb_bench.py` (người dùng cung cấp file, hoặc viết lại theo hướng dẫn) với 600M và 1.3B, mỗi cái trên `xpu` fp16 và `cpu` fp32, beam 1 và 4.
4. Chạy app `--demo` (hoặc với capture card) cùng lúc với bench, xem FPS thực và "Trễ hình" trên thẻ góc video có tụt không khi GPU vừa vẽ vừa dịch.
5. Người dùng chấm chất lượng 20 câu thoại thật của game (so với Claude Haiku).
6. Ghi số đo vào mục 0 của file này: dung lượng venv, thời gian nạp model, ms/câu, VRAM.

Tiêu chí đi tiếp: 600M trên B580 **dưới khoảng 300 ms/câu** và không làm hình giật. Nếu XPU không chạy được hoặc chậm: thử thêm CTranslate2 bản CPU int8 (xem mục 9) trước khi bỏ.

### N1 — Host Python (1 ngày)

- Viết `nllb_host.py` theo mục 3.3, các file requirements ghim phiên bản theo bản đã chạy ở N0.
- Kiểm bằng tay: `check`, `serve` + gọi `curl` vào cả 4 endpoint, sai token bị `401`, có `Origin` bị `403`, `bench` in đúng 4 số như `nllb_bench.py`.

### N2 — Core (1 ngày)

- `LocalHostTranslator`, `LocalHostProtocol`, `SentenceSplitter`, `LocalModelCatalog`.
- Test (handler giả, không cần Python): dịch nhiều câu ghép đúng thứ tự; `503`/không kết nối/quá giờ/`401` ra đúng loại lỗi; `FallbackTranslator` chế độ dự phòng: online lỗi → dùng bản NLLB, nhãn `"apple"`; tách câu với `"Mr. Smith?! Wait..."`.
- `dotnet test tests/GameTranslator.Core.Tests`: toàn bộ test cũ (39) vẫn pass.

### N3 — Platform (2 ngày)

- `PythonEnvironment` (mục 3.1), `ModelDownloader` (mục 3.2), `HostProcess` + `JobObject` (mục 3.4).
- Test Windows: Job Object giết tiến trình con khi đóng handle; chọn cổng trống khi 47811 bận; tải tiếp từ file `.part` (dùng server HTTP cục bộ giả, không tải thật từ Hugging Face trong test).
- Thử thật một lần trên máy dev: cài môi trường từ đầu, tải 600M, khởi động host, `/v1/health` ra `ready` trên `xpu`.

### N4 — Giao diện (2 ngày)

- `LocalModelWindow` (mục 4.1), mục mới trong tab Dịch (mục 4.2), trạng thái NLLB trên thanh trạng thái.
- `--render-ui` xuất thêm PNG màn hình mới để xem bố cục. Người dùng duyệt giao diện.

### N5 — Tích hợp và kiểm thử thực tế (1 ngày)

- `Session.ApplyProfile` truyền `LocalHostTranslator` vào `FallbackTranslator`.
- `--nllb-selftest`: khởi động host (nếu đã cài), dịch 5 câu mẫu, in thiết bị và thời gian, thoát với mã lỗi khác 0 nếu hỏng.
- Ma trận thử (cần người dùng bật Switch):
  - Chỉ offline: phụ đề tiếng Việt từ NLLB, không gọi API.
  - Online + dự phòng: rút mạng hoặc dùng API key sai → phụ đề chuyển sang NLLB; cắm lại mạng → bản offline được nâng cấp lên online (đã có sẵn trong pipeline).
  - Tắt host khi đang chơi → hiện chữ gốc, thanh trạng thái báo, app không văng; bật lại → dịch tiếp.
  - Tắt app bằng Task Manager → không còn `python.exe` của host.
  - So FPS / trễ hình khi có và không có NLLB chạy trên cùng GPU.

### N6 — Đóng gói và tài liệu (0,5 ngày)

- `uv.exe` + `tools/nllb/*` nằm trong thư mục `nllb\` cạnh exe (mục 3.0). Script host được chép ra `nllb\host\` trong thư mục dữ liệu khi cài (so hash, khác thì chép đè, để cập nhật app là cập nhật script).
- Thử trên máy sạch (chưa có Python): cài bộ cài → mở app → bấm một nút → có phụ đề offline, không cần làm gì khác.
- NSIS: gỡ cài đặt hỏi có xóa `nllb\` không. Bản portable: `nllb\` nằm trong `data\`.
- ARM64: torch XPU/CUDA không có bản ARM64 → chỉ cho backend CPU (hoặc ẩn tính năng nếu N0 cho thấy không chạy được).
- Cập nhật `README.md` (cách dùng, giấy phép CC-BY-NC của model), `PLAN.md` mục 0 và 9, mục 0 của file này.

Tổng: khoảng **8 ngày** làm việc, chưa tính thời gian chờ cài/tải.

## 7. Rủi ro và cách xử lý

| Rủi ro | Cách xử lý |
|---|---|
| Torch XPU trên Windows cần driver Intel mới, có thể lỗi | N0 kiểm trước; `check` báo rõ; luôn có đường lùi CPU |
| Môi trường nặng (torch XPU vài GB), cài lâu, mạng chập chờn | Tiến trình từng bước, làm tiếp được; hiện dung lượng trước khi cài |
| Dịch trên cùng GPU làm hình giật | Đo ở N0/N5; host ưu tiên thấp; cho chọn CPU; hàng đợi 1 request nên tải GPU ngắn và rời rạc |
| Lần dịch đầu trên XPU rất chậm (biên dịch kernel) | Dịch khởi động trước khi báo `ready` |
| NLLB dịch câu thoại ngắn kém, lặp từ, không giữ tên riêng | Giới hạn `maxNewTokens`; tách câu; chế độ dự phòng vẫn ưu tiên AI online; nâng cấp offline → online đã có |
| Trang web trong trình duyệt gọi vào host `localhost` | Bắt buộc token, từ chối request có `Origin`, chỉ nghe `127.0.0.1` |
| Phần mềm diệt virus chặn `python.exe`/`uv.exe` mới tải | Báo lỗi kèm đường dẫn để người dùng cho phép; không tự tắt bảo vệ |
| Cổng 47811 bị chiếm | Tự chọn cổng trống |
| Giấy phép CC-BY-NC | Không đóng gói model; ghi rõ trong app và README |
| `uv`/Python/torch đổi phiên bản làm hỏng cài đặt | Ghim phiên bản và SHA-256 trong mã/requirements |

## 8. Cần bạn quyết định

1. ~~Đi theo host Python + PyTorch XPU hay hướng không cần Python?~~ **Đã chốt (2026-11-24): host Python, cài bằng một nút** (mục 3.0). Hướng ONNX không cần Python để sau (mục 9).
2. **Model mặc định**: 600M (nhanh) hay 1.3B (dịch tốt hơn)? Nên chốt sau số đo N0.
3. **Thư mục lưu model mặc định**: `%LOCALAPPDATA%` (ổ C:) hay hỏi người dùng chọn ngay lần đầu?
4. **Host khác (máy khác trong mạng LAN)**: làm luôn ở bản này, hay để sau? Nếu làm thì host phải nghe `0.0.0.0` và người dùng tự mở tường lửa.
5. Có file `nllb_bench.py` gốc không? Hướng dẫn nhắc tới nhưng không có trong repo. Có thì đưa vào `tools/nllb/` để N0 dùng đúng script đó.

## 9. Ngoài phạm vi (để sau)

- **Không cần Python**: chạy NLLB bản ONNX (encoder/decoder) bằng ONNX Runtime + DirectML ngay trong C#, tokenizer bằng `Microsoft.ML.Tokenizers` (SentencePiece). Nhẹ hơn nhiều (không có torch) và chạy trên mọi GPU, nhưng phải tự viết vòng giải mã có KV cache. Làm khi bản host đã chứng minh chất lượng NLLB đủ dùng.
- **CTranslate2** bản CPU int8: nhẹ, nhanh trên CPU, nhưng không chạy GPU Intel. Có thể thêm làm backend thứ hai của host.
- Giữ tên riêng theo từ điển game (che tên bằng ký hiệu trước khi dịch rồi thay lại).
- Nhãn engine riêng cho NLLB trong cache (cần sửa Swift và xuất lại vector trên Mac).
- Host dạng tương thích OpenAI (LM Studio, Ollama) để dùng model LLM local.
