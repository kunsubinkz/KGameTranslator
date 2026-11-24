using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GameTranslator.Core;

public static partial class TextNormalizer
{
    private const string TrailingGlyphs = "▼▶►▾▸▹▷◀◁→↓⏷⏵»›>";

    // Swift Character.isNewline
    private static bool IsNewline(char c) =>
        c is (char)0x0A or (char)0x0B or (char)0x0C or (char)0x0D or (char)0x85 or (char)0x2028 or (char)0x2029;

    // Swift CharacterSet.whitespaces: tab và loại Zs (không gồm xuống dòng).
    private static bool IsInlineWhitespace(char c) =>
        c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>Gộp dòng, bỏ khoảng trắng thừa, sửa lỗi OCR phổ biến, bỏ biểu tượng "tiếp tục".
    /// Trả về null nếu chuỗi quá ngắn hoặc không có chữ cái.</summary>
    public static string? Normalize(string raw)
    {
        var lines = new List<string>();
        foreach (var line in SplitBy(raw.Replace('\r', '\n'), IsNewline))
        {
            var trimmed = TrimInline(line);
            if (trimmed.Length > 0) lines.Add(trimmed);
        }
        var text = FixCommonOcrErrors(string.Join(' ', lines));

        int end = text.Length;
        while (end > 0 && (TrailingGlyphs.Contains(text[end - 1]) || IsInlineWhitespace(text[end - 1]))) end--;
        text = text[..end];

        text = string.Join(' ', SplitBy(text, char.IsWhiteSpace).Where(s => s.Length > 0));

        if (LengthInGraphemes(text) < 3 || !text.Any(char.IsLetter)) return null;
        return text;
    }

    private static IEnumerable<string> SplitBy(string s, Func<char, bool> isSeparator)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (isSeparator(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string TrimInline(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsInlineWhitespace(s[start])) start++;
        while (end > start && IsInlineWhitespace(s[end - 1])) end--;
        return s[start..end];
    }

    [GeneratedRegex(@"(?<![\w])\|(?=\s|'|$)")]
    private static partial Regex LonePipe();

    [GeneratedRegex(@"(?<=[a-z])\|")]
    private static partial Regex PipeAfterLower();

    [GeneratedRegex(@"(?<![\w])l(?=\s|'(m|ll|ve|d))")]
    private static partial Regex LoneL();

    /// <summary>Như <see cref="Normalize"/> nhưng giữ ranh giới đoạn: OCR tách đoạn bằng dòng trống, mỗi đoạn được chuẩn hóa riêng
    /// (các dòng trong một đoạn vẫn nối bằng dấu cách) rồi nối bằng một dấu xuống dòng. Trả về null nếu không đoạn nào đọc được.</summary>
    public static string? NormalizeParagraphs(string raw)
    {
        const string nl = "\n";
        var paragraphs = raw.Replace((char)13, (char)10).Split(nl + nl)
            .Select(Normalize).Where(p => p is not null).ToList();
        return paragraphs.Count == 0 ? null : string.Join(nl, paragraphs);
    }

    /// <summary>`|` đứng một mình hoặc dính chữ thường là chữ I; `l` đứng một mình là I.</summary>
    internal static string FixCommonOcrErrors(string text)
    {
        var result = LonePipe().Replace(text, "I");
        result = PipeAfterLower().Replace(result, "l");
        result = LoneL().Replace(result, "I");
        return result;
    }

    private static string[] Graphemes(string s)
    {
        s = s.Normalize(NormalizationForm.FormC); // Swift so Character theo tương đương chuẩn tắc
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return [.. list];
    }

    private static int LengthInGraphemes(string s) => new StringInfo(s).LengthInTextElements;

    public static int Levenshtein(string a, string b)
    {
        var s = Graphemes(a);
        var t = Graphemes(b);
        if (s.Length == 0) return t.Length;
        if (t.Length == 0) return s.Length;
        var previous = new int[t.Length + 1];
        var current = new int[t.Length + 1];
        for (int j = 0; j <= t.Length; j++) previous[j] = j;
        for (int i = 1; i <= s.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= t.Length; j++)
            {
                int cost = string.Equals(s[i - 1], t[j - 1], StringComparison.Ordinal) ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[t.Length];
    }

    /// <summary>Tỉ lệ giống nhau 0...1.</summary>
    public static double Similarity(string a, string b)
    {
        int longest = Math.Max(Graphemes(a).Length, Graphemes(b).Length);
        if (longest == 0) return 1;
        return 1 - (double)Levenshtein(a, b) / longest;
    }

    public static bool IsSameSentence(string a, string b, double threshold = 0.92) =>
        Similarity(TranslationCache.SwiftLowercase(a), TranslationCache.SwiftLowercase(b)) >= threshold;

    /// <summary>Tỉ lệ lỗi ký tự (CER) giữa kết quả OCR và đáp án.</summary>
    public static double CharacterErrorRate(string expected, string actual)
    {
        if (expected.Length == 0) return actual.Length == 0 ? 0 : 1;
        return (double)Levenshtein(expected, actual) / Graphemes(expected).Length;
    }
}
