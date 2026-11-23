import Foundation

public enum PromptBuilder {
    public static let baseInstruction = """
    Bạn là người dịch hội thoại trò chơi điện tử từ tiếng Anh sang tiếng Việt.
    - Dịch tự nhiên, đúng giọng nhân vật, ngắn gọn để làm phụ đề.
    - Giữ nguyên tên riêng theo bảng thuật ngữ bên dưới.
    - Văn bản là kết quả OCR, có thể sai vài ký tự; hãy đoán nghĩa hợp lý.
    - Chỉ trả về bản dịch, không giải thích.
    """

    public static func systemInstruction(glossary: [String: String]) -> String {
        guard !glossary.isEmpty else { return baseInstruction }
        let lines = glossary.sorted { $0.key < $1.key }.map { "\($0.key) = \($0.value)" }
        return baseInstruction + "\n\nThuật ngữ:\n" + lines.joined(separator: "\n")
    }

    public static func userContent(for request: TranslationRequest) -> String {
        var parts: [String] = []
        if !request.context.isEmpty {
            let history = request.context.map { "EN: \($0.source)\nVI: \($0.target)" }.joined(separator: "\n")
            parts.append("Các câu trước (chỉ để tham khảo ngữ cảnh):\n" + history)
        }
        parts.append("Câu cần dịch:\n" + request.text)
        return parts.joined(separator: "\n\n")
    }
}
