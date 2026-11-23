import Foundation

public struct ContextPair: Sendable, Equatable {
    public let source: String
    public let target: String

    public init(source: String, target: String) {
        self.source = source
        self.target = target
    }
}

public struct TranslationRequest: Sendable {
    public let text: String
    public let context: [ContextPair]
    public let glossary: [String: String]

    public init(text: String, context: [ContextPair] = [], glossary: [String: String] = [:]) {
        self.text = text
        self.context = context
        self.glossary = glossary
    }
}

public enum TranslationError: Error, Equatable, Sendable {
    case missingAPIKey
    /// HTTP 429 hoặc hết hạn mức nội bộ.
    case quotaExceeded
    case network(String)
    case emptyResponse
    case blocked
    case unavailable(String)
    case timeout
}

public protocol Translator: Sendable {
    var id: String { get }
    func translate(_ request: TranslationRequest) async throws -> String
}

public struct TranslationOutcome: Sendable, Equatable {
    public enum Engine: Sendable, Equatable {
        case claude(String)
        case gemini(String)
        case apple
        /// Không dịch được, hiện chữ gốc.
        case original

        public var label: String {
            switch self {
            case .claude(let model): return "claude:\(model)"
            case .gemini(let model): return "gemini:\(model)"
            case .apple: return "apple"
            case .original: return "original"
            }
        }
    }

    public let text: String
    public let engine: Engine

    public init(text: String, engine: Engine) {
        self.text = text
        self.engine = engine
    }
}
