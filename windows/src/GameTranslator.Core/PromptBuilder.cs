using System.Text;

namespace GameTranslator.Core;

public static class PromptBuilder
{
    public const string BaseInstruction =
        "Bạn là người dịch hội thoại trò chơi điện tử từ tiếng Anh sang tiếng Việt.\n" +
        "- Dịch tự nhiên, đúng giọng nhân vật, ngắn gọn để làm phụ đề.\n" +
        "- Giữ nguyên tên riêng theo bảng thuật ngữ bên dưới.\n" +
        "- Văn bản là kết quả OCR, có thể sai vài ký tự; hãy đoán nghĩa hợp lý.\n" +
        "- Chỉ trả về bản dịch, không giải thích.";

    public static string SystemInstruction(IReadOnlyDictionary<string, string> glossary)
    {
        if (glossary.Count == 0) return BaseInstruction;
        // Swift sắp theo String `<` (thứ tự Unicode scalar sau chuẩn hóa).
        var keys = glossary.Keys.ToList();
        keys.Sort(CompareScalars);
        var lines = keys.Select(k => $"{k} = {glossary[k]}");
        return BaseInstruction + "\n\nThuật ngữ:\n" + string.Join("\n", lines);
    }

    private static int CompareScalars(string a, string b)
    {
        var ra = a.Normalize(NormalizationForm.FormC).EnumerateRunes().GetEnumerator();
        var rb = b.Normalize(NormalizationForm.FormC).EnumerateRunes().GetEnumerator();
        while (true)
        {
            bool ma = ra.MoveNext(), mb = rb.MoveNext();
            if (!ma || !mb) return ma == mb ? 0 : (ma ? 1 : -1);
            int c = ra.Current.Value.CompareTo(rb.Current.Value);
            if (c != 0) return c;
        }
    }

    /// <summary>Khi chữ gốc có nhiều đoạn (OCR tách theo khoảng trống giữa các dòng): dặn model giữ nguyên số đoạn.
    /// Câu một đoạn không thêm gì nên request vẫn khớp từng byte với bản macOS.</summary>
    public static string ParagraphNote(string text) => text.Contains((char)10)
        ? "\n- Văn bản có nhiều đoạn, mỗi đoạn một dòng: giữ nguyên số đoạn và thứ tự, dịch mỗi đoạn thành đúng một dòng."
        : "";

    public static string UserContent(TranslationRequest request)
    {
        var parts = new List<string>();
        if (request.Context.Count > 0)
        {
            var history = string.Join("\n", request.Context.Select(c => $"EN: {c.Source}\nVI: {c.Target}"));
            parts.Add("Các câu trước (chỉ để tham khảo ngữ cảnh):\n" + history);
        }
        parts.Add("Câu cần dịch:\n" + request.Text);
        return string.Join("\n\n", parts);
    }
}
