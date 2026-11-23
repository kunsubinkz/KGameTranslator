import Foundation

/// Gọi Gemini qua REST, không cần SDK. API key lấy qua closure để đọc từ Keychain mỗi lần gọi.
public struct GeminiTranslator: Translator {
    public let model: String
    public let temperature: Double
    public let maxOutputTokens: Int
    private let apiKey: @Sendable () -> String?
    private let session: URLSession

    public var id: String { "gemini:\(model)" }

    public init(model: String, temperature: Double = 0.3, maxOutputTokens: Int = 300,
                timeout: TimeInterval = 8, session: URLSession? = nil,
                apiKey: @escaping @Sendable () -> String?) {
        self.model = model
        self.temperature = temperature
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
            default: throw TranslationError.network("HTTP \(http.statusCode)")
            }
        }
        return try Self.parse(data)
    }

    func makeURLRequest(for request: TranslationRequest, apiKey: String) throws -> URLRequest {
        guard let url = URL(string: "https://generativelanguage.googleapis.com/v1beta/models/\(model):generateContent") else {
            throw TranslationError.unavailable("Tên model không hợp lệ")
        }
        var urlRequest = URLRequest(url: url)
        urlRequest.httpMethod = "POST"
        urlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        urlRequest.setValue(apiKey, forHTTPHeaderField: "x-goog-api-key")
        let body: [String: Any] = [
            "systemInstruction": ["parts": [["text": PromptBuilder.systemInstruction(glossary: request.glossary)]]],
            "contents": [["role": "user", "parts": [["text": PromptBuilder.userContent(for: request)]]]],
            "generationConfig": ["temperature": temperature, "maxOutputTokens": maxOutputTokens],
        ]
        urlRequest.httpBody = try JSONSerialization.data(withJSONObject: body)
        return urlRequest
    }

    /// Lấy văn bản từ phản hồi `generateContent`.
    public static func parse(_ data: Data) throws -> String {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw TranslationError.emptyResponse
        }
        if let feedback = root["promptFeedback"] as? [String: Any], feedback["blockReason"] != nil {
            throw TranslationError.blocked
        }
        guard let candidate = (root["candidates"] as? [[String: Any]])?.first else {
            throw TranslationError.emptyResponse
        }
        if (candidate["finishReason"] as? String) == "SAFETY" { throw TranslationError.blocked }
        let parts = (candidate["content"] as? [String: Any])?["parts"] as? [[String: Any]] ?? []
        let text = parts.compactMap { $0["text"] as? String }.joined()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { throw TranslationError.emptyResponse }
        return text
    }
}
