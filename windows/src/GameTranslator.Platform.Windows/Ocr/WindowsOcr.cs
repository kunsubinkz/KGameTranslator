using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using GameTranslator.Core;
using GameTranslator.Core.Pipeline;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrResult = GameTranslator.Core.Pipeline.OcrResult;

namespace GameTranslator.Platform.Windows.Ocr;

/// <summary>OCR mặc định: Windows.Media.Ocr (có sẵn trong Windows, cần gói ngôn ngữ tiếng Anh).</summary>
public sealed class WindowsOcr : IOcrEngine
{
    private readonly OcrEngine? _engine;

    public string Name => "Windows OCR";

    public WindowsOcr()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public static bool IsEnglishAvailable =>
        OcrEngine.AvailableRecognizerLanguages.Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));

    public async Task<OcrResult> RecognizeAsync(BgraImage image, OcrSettings settings, IReadOnlyList<string> customWords,
        CancellationToken cancellationToken = default)
    {
        if (_engine is null) throw new InvalidOperationException("Thiếu gói OCR tiếng Anh. Thêm ngôn ngữ English trong Settings > Time & language.");
        var sw = Stopwatch.StartNew();

        int w = image.Width, h = image.Height;
        int max = (int)OcrEngine.MaxImageDimension;
        var pixels = image.Pixels;
        if (w > max || h > max)
        {
            // Quá lớn cho OCR: thu nhỏ bằng cách lấy mẫu.
            double k = Math.Min((double)max / w, (double)max / h);
            var small = BgraImage.Create(Math.Max((int)(w * k), 1), Math.Max((int)(h * k), 1));
            for (int y = 0; y < small.Height; y++)
                for (int x = 0; x < small.Width; x++)
                    Buffer.BlockCopy(pixels, (((int)(y / k)) * w + (int)(x / k)) * 4, small.Pixels, (y * small.Width + x) * 4, 4);
            pixels = small.Pixels; w = small.Width; h = small.Height;
        }

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
        var result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);

        return new OcrResult(JoinWithParagraphs(result.Lines.Select(l => (l.Text, Box(l))).ToList()), 1f, sw.Elapsed.TotalMilliseconds);
    }

    private static (double Top, double Bottom, double Left) Box(OcrLine line)
    {
        double top = double.MaxValue, bottom = 0, left = double.MaxValue;
        foreach (var w in line.Words)
        {
            var r = w.BoundingRect;
            top = Math.Min(top, r.Top); bottom = Math.Max(bottom, r.Bottom); left = Math.Min(left, r.Left);
        }
        return line.Words.Count == 0 ? (0, 0, 0) : (top, bottom, left);
    }

    /// <summary>Ghép các dòng thành văn bản có ranh giới đoạn: dòng cùng hàng nối bằng dấu cách ("Robbie ... Lookout Landing"),
    /// dòng sát nhau xuống dòng đơn (cùng một đoạn), khoảng trống lớn hơn ~0,6 chiều cao dòng thì chèn dòng trống (đoạn mới).</summary>
    internal static string JoinWithParagraphs(IReadOnlyList<(string Text, (double Top, double Bottom, double Left) Box)> lines)
    {
        if (lines.Count == 0) return "";
        // Sắp từ trên xuống, cùng hàng thì trái sang phải.
        var sorted = lines.OrderBy(l => Math.Round((l.Box.Top + l.Box.Bottom) / 2 / 6)).ThenBy(l => l.Box.Left).ToList();
        double avgHeight = Math.Max(sorted.Average(l => l.Box.Bottom - l.Box.Top), 1);
        var sb = new System.Text.StringBuilder(sorted[0].Text);
        var prev = sorted[0].Box;
        for (int i = 1; i < sorted.Count; i++)
        {
            var cur = sorted[i].Box;
            double h = Math.Max(Math.Max(prev.Bottom - prev.Top, cur.Bottom - cur.Top), 1);
            bool sameRow = Math.Abs((cur.Top + cur.Bottom) / 2 - (prev.Top + prev.Bottom) / 2) < h * 0.5;
            double gap = cur.Top - prev.Bottom;
            sb.Append(sameRow ? " " : gap > avgHeight * 0.6 ? "\n\n" : "\n").Append(sorted[i].Text);
            if (!sameRow || cur.Bottom > prev.Bottom) prev = cur;
        }
        return sb.ToString();
    }
}
