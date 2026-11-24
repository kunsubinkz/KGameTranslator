namespace GameTranslator.Core.Pipeline;

/// <summary>Ảnh BGRA 8 bit, hàng trên cùng trước, không đệm cuối hàng.</summary>
public sealed record BgraImage(byte[] Pixels, int Width, int Height)
{
    public static BgraImage Create(int width, int height) => new(new byte[width * height * 4], width, height);
}

public sealed record OcrResult(string Text, float Confidence, double ElapsedMs);

public interface IOcrEngine
{
    string Name { get; }
    Task<OcrResult> RecognizeAsync(BgraImage image, OcrSettings settings, IReadOnlyList<string> customWords,
        CancellationToken cancellationToken = default);
}

/// <summary>Tiền xử lý ảnh vùng hộp thoại trên CPU (vùng nhỏ nên đủ nhanh): phóng to, đảo màu, tăng tương phản.</summary>
public static class Preprocessor
{
    public static BgraImage Prepare(BgraImage src, OcrSettings s, bool autoLevels = true)
    {
        var img = src;
        if (s.Scale > 1.01) img = ScaleBilinear(img, s.Scale);
        if (autoLevels) img = AutoLevels(img);
        if (s.Invert || Math.Abs(s.Contrast - 1) > 0.01) img = Adjust(img, s.Invert, s.Contrast);
        return img;
    }

    /// <summary>Tự cân sáng cho OCR khi vùng bị tối (Switch tự giảm sáng, cảnh đêm, chữ mờ): đo độ sáng theo kênh sáng nhất
    /// của mỗi điểm (chữ xanh/đỏ/vàng cũng tính là sáng), rồi kéo giãn dải sáng theo phân vị 1%–99.5%, giữ nguyên màu.
    /// Vùng đã đủ sáng thì để nguyên (kéo giãn ảnh sáng làm OCR đọc kém hơn).</summary>
    public static BgraImage AutoLevels(BgraImage src)
    {
        int n = src.Width * src.Height;
        if (n == 0) return src;
        var hist = new int[256];
        for (int o = 0; o < n * 4; o += 4)
            hist[Math.Max(src.Pixels[o], Math.Max(src.Pixels[o + 1], src.Pixels[o + 2]))]++;
        int lo = Percentile(hist, n, 0.01), hi = Percentile(hist, n, 0.995);
        if (hi >= 170 || hi - lo < 12) return src;
        double k = Math.Min(255.0 / (hi - lo), 4.0);
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = (byte)Math.Clamp((v - lo) * k + 0.5, 0, 255);
        var dst = BgraImage.Create(src.Width, src.Height);
        for (int o = 0; o < n * 4; o += 4)
        {
            dst.Pixels[o] = lut[src.Pixels[o]];
            dst.Pixels[o + 1] = lut[src.Pixels[o + 1]];
            dst.Pixels[o + 2] = lut[src.Pixels[o + 2]];
            dst.Pixels[o + 3] = 255;
        }
        return dst;
    }

    private static int Percentile(int[] hist, int total, double p)
    {
        long target = (long)(total * p), acc = 0;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc > target) return v;
        }
        return 255;
    }

    private static BgraImage ScaleBilinear(BgraImage src, double scale)
    {
        int w = Math.Max((int)Math.Round(src.Width * scale), 1), h = Math.Max((int)Math.Round(src.Height * scale), 1);
        var dst = BgraImage.Create(w, h);
        double sx = (double)src.Width / w, sy = (double)src.Height / h;
        for (int y = 0; y < h; y++)
        {
            double fy = Math.Max((y + 0.5) * sy - 0.5, 0);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, src.Height - 1);
            double wy = fy - y0;
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Max((x + 0.5) * sx - 0.5, 0);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, src.Width - 1);
                double wx = fx - x0;
                int o = (y * w + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    double top = src.Pixels[(y0 * src.Width + x0) * 4 + c] * (1 - wx) + src.Pixels[(y0 * src.Width + x1) * 4 + c] * wx;
                    double bot = src.Pixels[(y1 * src.Width + x0) * 4 + c] * (1 - wx) + src.Pixels[(y1 * src.Width + x1) * 4 + c] * wx;
                    dst.Pixels[o + c] = (byte)Math.Clamp(top * (1 - wy) + bot * wy + 0.5, 0, 255);
                }
            }
        }
        return dst;
    }

    private static BgraImage Adjust(BgraImage src, bool invert, double contrast)
    {
        var dst = BgraImage.Create(src.Width, src.Height);
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double v = invert ? 255 - i : i;
            lut[i] = (byte)Math.Clamp((v - 127.5) * contrast + 127.5 + 0.5, 0, 255);
        }
        for (int i = 0; i < src.Pixels.Length; i += 4)
        {
            dst.Pixels[i] = lut[src.Pixels[i]];
            dst.Pixels[i + 1] = lut[src.Pixels[i + 1]];
            dst.Pixels[i + 2] = lut[src.Pixels[i + 2]];
            dst.Pixels[i + 3] = 255;
        }
        return dst;
    }

    /// <summary>Ảnh xám thu nhỏ (~160 px rộng) của vùng, dùng cho ChangeDetector.</summary>
    public static byte[] Thumbnail(BgraImage src, int width = 160)
    {
        if (src.Width <= 0 || src.Height <= 0) return [];
        int height = Math.Max((int)Math.Round(src.Height * (double)width / src.Width), 1);
        var result = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int y0 = (int)((long)y * src.Height / height), y1 = Math.Max((int)((long)(y + 1) * src.Height / height), y0 + 1);
            for (int x = 0; x < width; x++)
            {
                int x0 = (int)((long)x * src.Width / width), x1 = Math.Max((int)((long)(x + 1) * src.Width / width), x0 + 1);
                long sum = 0; int n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int o = (yy * src.Width + xx) * 4;
                        sum += (src.Pixels[o] * 29 + src.Pixels[o + 1] * 150 + src.Pixels[o + 2] * 77) >> 8;
                        n++;
                    }
                result[y * width + x] = (byte)(sum / n);
            }
        }
        return result;
    }
}
