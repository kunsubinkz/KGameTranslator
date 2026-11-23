import Foundation

public enum TextNormalizer {
    private static let trailingGlyphs = CharacterSet(charactersIn: "▼▶►▾▸▹▷◀◁→↓⏷⏵»›>")

    /// Gộp dòng, bỏ khoảng trắng thừa, sửa lỗi OCR phổ biến, bỏ biểu tượng "tiếp tục".
    /// Trả về nil nếu chuỗi quá ngắn hoặc không có chữ cái.
    public static func normalize(_ raw: String) -> String? {
        var text = raw.replacingOccurrences(of: "\r", with: "\n")
            .split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
            .joined(separator: " ")

        text = fixCommonOCRErrors(text)

        while let last = text.unicodeScalars.last,
              trailingGlyphs.contains(last) || CharacterSet.whitespaces.contains(last) {
            text.unicodeScalars.removeLast()
        }

        text = text.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")

        guard text.count >= 3, text.contains(where: \.isLetter) else { return nil }
        return text
    }

    /// `|` đứng một mình hoặc dính chữ thường là chữ I; `l` đứng một mình là I.
    static func fixCommonOCRErrors(_ text: String) -> String {
        var result = text
        result = result.replacingOccurrences(of: #"(?<![\w])\|(?=\s|'|$)"#, with: "I", options: .regularExpression)
        result = result.replacingOccurrences(of: #"(?<=[a-z])\|"#, with: "l", options: .regularExpression)
        result = result.replacingOccurrences(of: #"(?<![\w])l(?=\s|'(m|ll|ve|d))"#, with: "I", options: .regularExpression)
        return result
    }

    public static func levenshtein(_ a: String, _ b: String) -> Int {
        let s = Array(a), t = Array(b)
        if s.isEmpty { return t.count }
        if t.isEmpty { return s.count }
        var previous = Array(0...t.count)
        var current = [Int](repeating: 0, count: t.count + 1)
        for i in 1...s.count {
            current[0] = i
            for j in 1...t.count {
                let cost = s[i - 1] == t[j - 1] ? 0 : 1
                current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + cost)
            }
            swap(&previous, &current)
        }
        return previous[t.count]
    }

    /// Tỉ lệ giống nhau 0...1.
    public static func similarity(_ a: String, _ b: String) -> Double {
        let longest = max(a.count, b.count)
        if longest == 0 { return 1 }
        return 1 - Double(levenshtein(a, b)) / Double(longest)
    }

    public static func isSameSentence(_ a: String, _ b: String, threshold: Double = 0.92) -> Bool {
        similarity(a.lowercased(), b.lowercased()) >= threshold
    }

    /// Tỉ lệ lỗi ký tự (CER) giữa kết quả OCR và đáp án; dùng để đo độ chính xác OCR.
    public static func characterErrorRate(expected: String, actual: String) -> Double {
        guard !expected.isEmpty else { return actual.isEmpty ? 0 : 1 }
        return Double(levenshtein(expected, actual)) / Double(expected.count)
    }
}
