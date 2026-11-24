# Hướng dẫn chạy NLLB-200 để dịch và đo tốc độ

## 1. Mục đích

Chạy model dịch NLLB-200 cục bộ để dịch câu thoại game từ tiếng Anh sang tiếng Việt, đồng thời đo thời gian dịch mỗi câu. Script `nllb_bench.py` dùng GPU Intel Arc nếu PyTorch nhận được, nếu không sẽ chạy bằng CPU.

## 2. Yêu cầu

- Python 3.9 trở lên (khuyến nghị 3.11 hoặc 3.12 để tránh lỗi tương thích về sau)
- Kết nối Internet cho lần chạy đầu tiên (để tải model)
- Dung lượng trống khoảng 2,5 GB cho model 600M

## 3. Cài thư viện

```bash
python3 -m pip install torch transformers sentencepiece
```

Trên máy có card Intel Arc, cần cài thêm Intel Extension for PyTorch đúng phiên bản với PyTorch để có thiết bị `xpu`.

## 4. Chạy script

```bash
cd ~/Downloads
python3 nllb_bench.py
```

Các tùy chọn thường dùng:

| Tùy chọn | Ý nghĩa |
|---|---|
| `--input cau.txt` | Dịch các câu trong file, mỗi dòng một câu |
| `--output ket_qua.txt` | Ghi bản dịch ra file |
| `--model facebook/nllb-200-distilled-1.3B` | Dùng model lớn hơn |
| `--device cpu` | Ép chạy bằng CPU |
| `--beams 4` | Tăng chất lượng, nhưng chậm hơn |

Script sẽ in ra thiết bị đang dùng (`xpu` hoặc `cpu`), 5 câu dịch mẫu, và thời gian trung bình, trung vị, nhanh nhất, chậm nhất mỗi câu.

## 5. Nơi lưu model

Mặc định model được lưu tại:

- macOS / Linux: `~/.cache/huggingface/hub`
- Windows: `C:\Users\<tên>\.cache\huggingface\hub`

### Đổi sang ổ khác (ví dụ ổ ngoài trên Mac)

```bash
export HF_HOME=/Volumes/TEN_O_NGOAI/hf_cache
python3 nllb_bench.py
```

Để cố định cài đặt này, thêm dòng `export HF_HOME=...` vào `~/.zshrc`. Ổ ngoài cần được kết nối mỗi lần chạy.

### Xóa model để lấy lại dung lượng

```bash
rm -rf ~/.cache/huggingface/hub/models--facebook--nllb-200-distilled-600M
```

## 6. Lưu ý

- **Mac Apple Silicon không có Intel Arc.** Trên Mac, script chạy bằng CPU, nên số đo không phản ánh tốc độ của Arc A580.
- **Lần chạy đầu chậm hơn** do phải tải model và khởi động. Các lần sau nhanh hơn.
- **Model NLLB-200 là mô hình nghiên cứu**, không được khuyến nghị dùng trong sản phẩm thương mại mà không kiểm tra kỹ.
- **Nên kiểm tra chất lượng** bằng khoảng 20 câu thoại thật của game trước khi quyết định dùng lâu dài.
