import Foundation

/// Điều phối AI online (Claude hoặc Gemini), dịch offline và chữ gốc theo chế độ người dùng chọn.
public actor FallbackTranslator {
    public private(set) var mode: TranslateMode
    private let claude: Translator?
    private let claudeModel: String
    private let provider: TranslateProvider
    private let offline: Translator?
    private let limiter: RateLimiter?
    private let now: @Sendable () -> Date

    private var backoffUntil: Date = .distantPast
    private var backoffSeconds: Double = 0

    public init(mode: TranslateMode, claude: Translator?, claudeModel: String, provider: TranslateProvider = .claude, offline: Translator?,
                limiter: RateLimiter?, now: @escaping @Sendable () -> Date = { Date() }) {
        self.mode = mode
        self.claude = claude
        self.claudeModel = claudeModel
        self.provider = provider
        self.offline = offline
        self.limiter = limiter
        self.now = now
    }

    public func translate(_ request: TranslationRequest) async -> TranslationOutcome {
        if mode != .offlineOnly, let text = await tryClaude(request) {
            return TranslationOutcome(text: text, engine: onlineEngine)
        }
        if Task.isCancelled { return TranslationOutcome(text: request.text, engine: .original) }
        if mode != .onlineOnly, let offline, let text = try? await offline.translate(request),
           !text.isEmpty {
            return TranslationOutcome(text: text, engine: .apple)
        }
        return TranslationOutcome(text: request.text, engine: .original)
    }

    /// Dịch lại bằng Claude để thay bản offline. Trả về nil nếu hết hạn mức hoặc lỗi.
    public func upgrade(_ request: TranslationRequest) async -> TranslationOutcome? {
        guard mode != .offlineOnly, let text = await tryClaude(request) else { return nil }
        return TranslationOutcome(text: text, engine: onlineEngine)
    }

    private var onlineEngine: TranslationOutcome.Engine {
        provider == .claude ? .claude(claudeModel) : .gemini(claudeModel)
    }

    private func tryClaude(_ request: TranslationRequest) async -> String? {
        guard let claude, now() >= backoffUntil else { return nil }
        if let limiter, await !limiter.tryAcquire(now: now()) { return nil }
        do {
            let text = try await claude.translate(request)
            backoffSeconds = 0
            return text
        } catch TranslationError.quotaExceeded {
            backoffSeconds = min(max(backoffSeconds * 2, 1), 30)
            backoffUntil = now().addingTimeInterval(backoffSeconds)
            return nil
        } catch {
            // Lỗi mạng, timeout, phản hồi rỗng: chuyển offline ngay, câu sau thử Claude lại.
            return nil
        }
    }
}
