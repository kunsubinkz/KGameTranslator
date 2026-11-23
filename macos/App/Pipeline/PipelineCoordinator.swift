import CoreImage
import CoreVideo
import Foundation
import TranslatorCore

enum PipelineEvent: Sendable {
    case recognized(text: String, ocrMs: Double)
    case translating(source: String)
    case subtitle(source: String, target: String, engine: String, cached: Bool, translateMs: Double)
    case cleared
}

struct FrameBox: @unchecked Sendable {
    let buffer: CVPixelBuffer
}

/// Cho phép tối đa một khung được xử lý cùng lúc; khung đến khi đang bận bị bỏ.
final class BusyGate: @unchecked Sendable {
    private let lock = NSLock()
    private var busy = false
    private var enabled = false

    func setEnabled(_ value: Bool) {
        lock.lock(); enabled = value; lock.unlock()
    }

    func tryEnter() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard enabled, !busy else { return false }
        busy = true
        return true
    }

    func leave() {
        lock.lock(); busy = false; lock.unlock()
    }
}

/// Điều phối: cắt vùng, phát hiện thay đổi, OCR, chuẩn hóa, cache, dịch. Dịch chạy ở Task riêng nên không chặn khung kế tiếp.
actor PipelineCoordinator {
    nonisolated let gate = BusyGate()

    private let ocr: OCREngine
    private let preprocessor = Preprocessor()
    private let cache: TranslationCache
    private let emit: @Sendable (PipelineEvent) -> Void

    private var profile: Profile = .default
    private var detector = ChangeDetector()
    private var translator: FallbackTranslator?
    private var recentPairs: [ContextPair] = []
    private var lastSource: String?
    private var generation = 0
    private var translateTask: Task<Void, Never>?
    private var lastPollMs: Double = 0
    private var missCount = 0
    /// Khi đang hiện phụ đề, cứ mỗi chừng này ms lại OCR kiểm tra chữ còn đó không (nền game chuyển động thì ChangeDetector không thấy "trống").
    private static let presencePollMs: Double = 600
    private static let missesToHide = 2

    init(ocr: OCREngine = VisionOCR(), cache: TranslationCache, emit: @escaping @Sendable (PipelineEvent) -> Void) {
        self.ocr = ocr
        self.cache = cache
        self.emit = emit
    }

    nonisolated func setEnabled(_ enabled: Bool) {
        gate.setEnabled(enabled)
    }

    /// Gọi từ queue lấy khung. Không chờ: nếu pipeline bận thì bỏ khung.
    nonisolated func submit(_ frame: FrameBox) {
        guard gate.tryEnter() else { return }
        Task {
            await process(frame)
            gate.leave()
        }
    }

    func configure(profile: Profile, translator: FallbackTranslator) {
        let profileChanged = self.profile.id != profile.id || self.profile.allRegions != profile.allRegions
        self.profile = profile
        self.translator = translator
        detector.config = ChangeDetector.Config(
            changeThreshold: profile.detector.changeThreshold,
            stableMs: profile.detector.stableMs,
            hideDelayMs: profile.detector.hideDelayMs)
        if profileChanged { resetState() }
    }

    func resetState() {
        detector.reset()
        lastSource = nil
        missCount = 0
        recentPairs.removeAll()
        translateTask?.cancel()
        generation += 1
    }

    /// Dịch lại câu hiện tại, bỏ qua cache.
    func retranslate() {
        guard let source = lastSource else { return }
        startTranslation(source, bypassCache: true)
    }

    // MARK: - Xử lý khung

    private func process(_ frame: FrameBox) async {
        let image = CIImage(cvPixelBuffer: frame.buffer)
        let regions = profile.allRegions.map { image.cropped(to: Self.cropRect(for: $0, in: image.extent)) }

        var samples: [UInt8] = []
        for region in regions {
            guard let part = preprocessor.thumbnail(region) else { return }
            samples += part
        }
        let nowMs = ProcessInfo.processInfo.systemUptime * 1000

        switch detector.process(samples: samples, nowMs: nowMs) {
        case .none:
            if lastSource != nil, nowMs - lastPollMs >= Self.presencePollMs {
                lastPollMs = nowMs
                checkPresence(regions)
            }
        case .cleared:
            hide()
        case .textStable:
            lastPollMs = nowMs
            recognize(regions)
        }
    }

    private func hide() {
        lastSource = nil
        missCount = 0
        detector.reset()
        emit(.cleared)
    }

    /// OCR mọi vùng rồi ghép chữ lại. nil nếu không đọc được chữ nào.
    private func readText(_ regions: [CIImage]) -> (text: String, ms: Double)? {
        let words = Array(profile.glossary.keys)
        var parts: [String] = []
        var totalMs = 0.0
        for region in regions {
            guard let prepared = preprocessor.prepare(region, settings: profile.ocr) else { continue }
            do {
                let result = try ocr.recognize(prepared, settings: profile.ocr, customWords: words)
                totalMs += result.elapsedMs
                if let text = TextNormalizer.normalize(result.text) { parts.append(text) }
            } catch {
                Log.pipeline.error("OCR lỗi: \(error.localizedDescription, privacy: .public)")
            }
        }
        guard !parts.isEmpty else { return nil }
        return (parts.joined(separator: " "), totalMs)
    }

    /// Đang hiện phụ đề mà OCR liên tiếp không thấy chữ thì ẩn đi.
    private func checkPresence(_ regions: [CIImage]) {
        if readText(regions) == nil {
            missCount += 1
            if missCount >= Self.missesToHide { hide() }
        } else {
            missCount = 0
        }
    }

    private func recognize(_ regions: [CIImage]) {
        guard let (text, ms) = readText(regions) else {
            if lastSource != nil { hide() }
            return
        }
        missCount = 0
        if let last = lastSource, TextNormalizer.isSameSentence(last, text) { return }

        lastSource = text
        emit(.recognized(text: text, ocrMs: ms))
        startTranslation(text, bypassCache: false)
    }

    private func startTranslation(_ source: String, bypassCache: Bool) {
        let profileId = profile.id
        if !bypassCache, let hit = cache.lookup(profileId: profileId, source: source) {
            translateTask?.cancel()
            generation += 1
            remember(source: source, target: hit.target)
            emit(.subtitle(source: source, target: hit.target, engine: hit.engine, cached: true, translateMs: 0))
            return
        }
        guard let translator else { return }

        emit(.translating(source: source))
        translateTask?.cancel()
        generation += 1
        let current = generation
        let request = TranslationRequest(
            text: source,
            context: Array(recentPairs.suffix(profile.translate.contextLines)),
            glossary: profile.glossary)
        translateTask = Task { [weak self] in
            let start = DispatchTime.now()
            let outcome = await translator.translate(request)
            guard !Task.isCancelled else { return }
            let ms = Double(DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds) / 1_000_000
            await self?.finish(generation: current, profileId: profileId, request: request,
                               outcome: outcome, ms: ms, translator: translator)
        }
    }

    private func finish(generation: Int, profileId: String, request: TranslationRequest,
                        outcome: TranslationOutcome, ms: Double, translator: FallbackTranslator) {
        guard generation == self.generation else { return }
        let source = request.text
        if outcome.engine != .original {
            cache.store(profileId: profileId, source: source, target: outcome.text, engine: outcome.engine.label)
            remember(source: source, target: outcome.text)
        }
        emit(.subtitle(source: source, target: outcome.text, engine: outcome.engine.label, cached: false, translateMs: ms))

        if outcome.engine == .apple, profile.translate.upgradeOffline {
            Task { [weak self] in
                let start = DispatchTime.now()
                guard let better = await translator.upgrade(request) else { return }
                let upgradeMs = Double(DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds) / 1_000_000
                await self?.applyUpgrade(profileId: profileId, source: source, outcome: better, ms: upgradeMs)
            }
        }
    }

    /// Thay bản dịch offline bằng bản Claude; chỉ cập nhật phụ đề nếu câu đó vẫn đang hiện.
    private func applyUpgrade(profileId: String, source: String, outcome: TranslationOutcome, ms: Double) {
        cache.store(profileId: profileId, source: source, target: outcome.text, engine: outcome.engine.label)
        guard lastSource == source else { return }
        emit(.subtitle(source: source, target: outcome.text, engine: outcome.engine.label, cached: false, translateMs: ms))
    }

    private func remember(source: String, target: String) {
        recentPairs.append(ContextPair(source: source, target: target))
        if recentPairs.count > 6 { recentPairs.removeFirst(recentPairs.count - 6) }
    }

    /// Vùng tương đối (gốc trên trái) sang pixel (CIImage có gốc dưới trái).
    static func cropRect(for region: Region, in extent: CGRect) -> CGRect {
        let r = region.clamped()
        return CGRect(
            x: extent.minX + r.x * extent.width,
            y: extent.minY + (1 - r.y - r.height) * extent.height,
            width: r.width * extent.width,
            height: r.height * extent.height).integral
    }
}
