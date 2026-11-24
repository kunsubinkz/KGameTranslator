namespace GameTranslator.Core.LocalHost;

/// <summary>Tách đoạn thoại thành từng câu để NLLB dịch ổn định hơn. Giữ "Mr.", "...", "?!".</summary>
public static class SentenceSplitter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "no", "lt", "capt", "gen", "col", "sgt",
    };

    public static IReadOnlyList<string> Split(string text)
    {
        var result = new List<string>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is not ('.' or '!' or '?' or '…' or '。'))
            {
                i++;
                continue;
            }
            // Gom cả cụm dấu kết thúc: "...", "?!", "!!".
            var end = i;
            while (end + 1 < text.Length && text[end + 1] is '.' or '!' or '?' or '…')
                end++;
            // Đóng ngoặc/nháy ngay sau dấu kết thúc thì đi cùng câu.
            while (end + 1 < text.Length && text[end + 1] is '"' or '\'' or ')' or '”' or '’')
                end++;

            var atEnd = end + 1 >= text.Length;
            var spaceAfter = !atEnd && char.IsWhiteSpace(text[end + 1]);
            if (!atEnd && !spaceAfter)
            {
                i = end + 1; // "3.5", "a.b": không phải hết câu.
                continue;
            }
            if (c == '.' && end == i && IsAbbreviation(text, start, i))
            {
                i = end + 1;
                continue;
            }
            // "Wait... what?": dấu chấm lửng theo sau bởi chữ thường thì chưa hết câu.
            if (!atEnd && c == '.' && end > i && NextLetterIsLower(text, end + 1))
            {
                i = end + 1;
                continue;
            }
            Add(result, text[start..(end + 1)]);
            start = end + 1;
            i = start;
        }
        if (start < text.Length) Add(result, text[start..]);
        return result;
    }

    private static void Add(List<string> list, string s)
    {
        s = s.Trim();
        if (s.Length > 0) list.Add(s);
    }

    private static bool IsAbbreviation(string text, int sentenceStart, int dotIndex)
    {
        var w = dotIndex;
        while (w > sentenceStart && char.IsLetter(text[w - 1])) w--;
        var word = text[w..dotIndex];
        if (word.Length == 1 && char.IsUpper(word[0])) return true; // chữ cái đầu tên: "J. Smith"
        return word.Length > 0 && Abbreviations.Contains(word);
    }

    private static bool NextLetterIsLower(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            return char.IsLetter(text[i]) && char.IsLower(text[i]);
        }
        return false;
    }
}
