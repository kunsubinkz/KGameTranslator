import Foundation
import Observation
import SwiftUI
import Translation
import TranslatorCore

/// Cầu nối tới Translation framework: `TranslationSession` chỉ lấy được qua `.translationTask` của SwiftUI,
/// nên yêu cầu dịch được đưa vào một hàng đợi và xử lý trong view ẩn.
@MainActor
@Observable
final class AppleTranslationBridge {
    enum Availability: Equatable {
        case unknown, installed, needsDownload, unsupported
    }

    private final class Job {
        enum Kind { case translate(String), prepare }
        let kind: Kind
        var continuation: CheckedContinuation<String, Error>?
        init(kind: Kind, continuation: CheckedContinuation<String, Error>) {
            self.kind = kind
            self.continuation = continuation
        }
        func finish(_ result: Result<String, Error>) {
            continuation?.resume(with: result)
            continuation = nil
        }
    }

    static let source = Locale.Language(identifier: "en")
    static let target = Locale.Language(identifier: "vi")

    var configuration: TranslationSession.Configuration?
    private(set) var availability: Availability = .unknown
    private(set) var isPreparing = false

    @ObservationIgnored private var jobsContinuation: AsyncStream<Job>.Continuation?
    @ObservationIgnored private lazy var jobs: AsyncStream<Job> = {
        AsyncStream { self.jobsContinuation = $0 }
    }()

    func start() {
        _ = jobs
        configuration = .init(source: Self.source, target: Self.target)
        Task { await refreshAvailability() }
    }

    func refreshAvailability() async {
        let status = await LanguageAvailability().status(from: Self.source, to: Self.target)
        switch status {
        case .installed: availability = .installed
        case .supported: availability = .needsDownload
        case .unsupported: availability = .unsupported
        @unknown default: availability = .unknown
        }
    }

    /// Tải gói ngôn ngữ offline (macOS hiện hộp thoại tải xuống).
    func prepare() async {
        isPreparing = true
        defer { isPreparing = false }
        _ = try? await enqueue(.prepare, timeout: 600)
        await refreshAvailability()
    }

    func translate(_ text: String) async throws -> String {
        try await enqueue(.translate(text), timeout: 10)
    }

    private func enqueue(_ kind: Job.Kind, timeout: TimeInterval) async throws -> String {
        guard configuration != nil, jobsContinuation != nil else {
            throw TranslationError.unavailable("Dịch offline chưa sẵn sàng")
        }
        return try await withCheckedThrowingContinuation { continuation in
            let job = Job(kind: kind, continuation: continuation)
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                job.finish(.failure(TranslationError.timeout))
            }
            jobsContinuation?.yield(job)
        }
    }

    /// Chạy trong `.translationTask`; giữ session sống để xử lý hàng đợi.
    func run(session: TranslationSession) async {
        for await job in jobs {
            if Task.isCancelled { job.finish(.failure(CancellationError())); break }
            do {
                switch job.kind {
                case .prepare:
                    try await session.prepareTranslation()
                    job.finish(.success(""))
                case .translate(let text):
                    let response = try await session.translate(text)
                    job.finish(.success(response.targetText))
                }
            } catch {
                job.finish(.failure(TranslationError.unavailable(error.localizedDescription)))
            }
        }
    }
}

/// Dịch offline qua Apple Translation. Tên riêng trong thuật ngữ được thay bằng ký hiệu tạm trước khi dịch.
struct AppleTranslator: Translator {
    let bridge: AppleTranslationBridge
    var id: String { "apple" }

    func translate(_ request: TranslationRequest) async throws -> String {
        var text = request.text
        var placeholders: [(token: String, value: String)] = []
        for (index, entry) in request.glossary.sorted(by: { $0.key.count > $1.key.count }).enumerated()
        where text.contains(entry.key) {
            let token = "⟦\(index)⟧"
            text = text.replacingOccurrences(of: entry.key, with: token)
            placeholders.append((token, entry.value))
        }
        var translated = try await bridge.translate(text)
        for (token, value) in placeholders {
            translated = translated.replacingOccurrences(of: token, with: value)
        }
        return translated
    }
}
