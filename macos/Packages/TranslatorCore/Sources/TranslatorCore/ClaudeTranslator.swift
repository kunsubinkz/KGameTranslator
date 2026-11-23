import Foundation

/// Gọi Claude (Anthropic Messages API) qua REST, không cần SDK. API key lấy qua closure để đọc từ Keychain mỗi lần gọi.
public struct ClaudeTranslator: Translator {
    public let model: String
    public let maxOutputTokens: Int
    private let apiKey: @Sendable () -> String?
    private let session: URLSession

    public var id: String { "claude:\(model)" }

    public init(model: String, maxOutputTokens: Int = 1024,
                timeout: TimeInterval = 8, session: URLSession? = nil,
                apiKey: @escaping @Sendable () -> String?) {
        self.model = model
        self.maxOutputTokens = maxOutputTokens
        self.apiKey = apiKey
        if let session {
            self.session = session
        } else {
            let config = URLSessionConfiguration.ephemeral
            config.timeoutIntervalForRequest = timeout
            config.timeoutIntervalForResource = timeout * 2
            self.session = URLSession(configuration: config)
        }
    }

    public func translate(_ request: TranslationRequest) async throws -> String {
        guard let key = apiKey(), !key.isEmpty else { throw TranslationError.missingAPIKey }
        let urlRequest = try makeURLRequest(for: request, apiKey: key)

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: urlRequest)
        } catch let error as URLError where error.code == .timedOut {
            throw TranslationError.timeout
        } catch is CancellationError {
            throw CancellationError()
        } catch let error as URLError where error.code == .cancelled {
            throw CancellationError()
        } catch {
            throw TranslationError.network(error.localizedDescription)
        }

        if let http = response as? HTTPURLResponse {
            switch http.statusCode {
            case 200..<300: break
            case 429: throw TranslationError.quotaExceeded
            case 401, 403: throw TranslationError.network("API key Claude không hợp lệ (HTTP \(http.statusCode))")
            default:
                // Hết credit trả về 400 kèm "credit balance"; coi như hết hạn mức để app tự lùi về offline.
                if http.statusCode == 400, String(data: data, encoding: .utf8)?.contains("credit balance") == true {
                    throw TranslationError.quotaExceeded
                }
                throw TranslationError.network("HTTP \(http.statusCode)")
            }
        }
        return try Self.parse(data)
    }

    func makeURLRequest(for request: TranslationRequest, apiKey: String) throws -> URLRequest {
        guard let url = URL(string: "https://api.anthropic.com/v1/messages") else {
            throw TranslationError.unavailable("URL không hợp lệ")
        }
        var urlRequest = URLRequest(url: url)
        urlRequest.httpMethod = "POST"
        urlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        urlRequest.setValue(apiKey, forHTTPHeaderField: "x-api-key")
        urlRequest.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        // Không gửi temperature/top_p (các model 5.x từ chối giá trị khác mặc định).
        var body: [String: Any] = [
            "model": model,
            "max_tokens": maxOutputTokens,
            "system": PromptBuilder.systemInstruction(glossary: request.glossary),
            "messages": [["role": "user", "content": PromptBuilder.userContent(for: request)]],
        ]
        // Giảm suy nghĩ để dịch nhanh: Haiku 5.5 tắt hẳn được, Sonnet 5.5 chỉ có "between_tools",
        // Opus 5.5 không tắt được nên hạ effort xuống thấp.
        let name = model.lowercased()
        if name.contains("haiku") {
            body["thinking"] = ["type": "disabled"]
        } else if name.contains("sonnet-5-5") {
            body["thinking"] = ["type": "between_tools"]
        } else if name.contains("opus-5-5") {
            body["output_config"] = ["effort": "low"]
        }
        urlRequest.httpBody = try JSONSerialization.data(withJSONObject: body)
        return urlRequest
    }

    /// Lấy văn bản từ phản hồi Messages API.
    public static func parse(_ data: Data) throws -> String {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw TranslationError.emptyResponse
        }
        if (root["stop_reason"] as? String) == "refusal" { throw TranslationError.blocked }
        let blocks = root["content"] as? [[String: Any]] ?? []
        let text = blocks.filter { ($0["type"] as? String) == "text" }
            .compactMap { $0["text"] as? String }.joined()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { throw TranslationError.emptyResponse }
        return text
    }
}
