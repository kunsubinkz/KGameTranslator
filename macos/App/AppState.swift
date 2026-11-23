import AppKit
import CoreImage
import AVFoundation
import Foundation
import Observation
import TranslatorCore

struct SubtitleState: Equatable {
    var source: String
    var target: String?
    var engine: String?
    var cached: Bool
}

@MainActor
@Observable
final class AppState {
    // MARK: - Thành phần
    let capture = CaptureController()
    let apple = AppleTranslationBridge()
    let profileStore = ProfileStore.standard()
    private(set) var cache: TranslationCache?
    @ObservationIgnored private var coordinator: PipelineCoordinator?
    @ObservationIgnored private var limiter: RateLimiter?
    @ObservationIgnored private var limiterKey = ""
    @ObservationIgnored private var started = false
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private let cpuMonitor = CPUMonitor()
    @ObservationIgnored private var cpuTimer: Timer?

    // MARK: - Profile
    var profiles: [Profile] = []
    var profile: Profile = .default {
        didSet { if profile != oldValue { profileDidChange(from: oldValue) } }
    }

    // MARK: - Thiết bị và quyền
    var videoDevices: [CaptureDevice] = []
    var audioDevices: [CaptureDevice] = []
    var audioOutputs: [CaptureDevice] = []
    var permission: PermissionState = .unknown
    var micPermission: PermissionState = .unknown
    var captureStatus: CaptureStatus = .idle
    var videoSize: CGSize = CGSize(width: 16, height: 9)

    var selectedVideoID: String? {
        didSet { Defaults.set(selectedVideoID, .videoID); if started, oldValue != selectedVideoID { restartCapture(matchAudio: true) } }
    }
    var selectedAudioID: String? {
        didSet { Defaults.set(selectedAudioID, .audioID); if started, oldValue != selectedAudioID { restartCapture(matchAudio: false) } }
    }
    var selectedOutputID: String? {
        didSet { Defaults.set(selectedOutputID, .outputID); capture.setAudioOutput(uniqueID: selectedOutputID) }
    }
    var volume: Double = Defaults.double(.volume, default: 1.0) {
        didSet { Defaults.set(volume, .volume); capture.setVolume(Float(volume)) }
    }

    // MARK: - Điều khiển
    var translationEnabled = true {
        didSet {
            coordinator?.setEnabled(translationEnabled)
            if !translationEnabled { subtitle = nil }
            Task { await coordinator?.resetState() }
        }
    }
    var showOriginal = Defaults.bool(.showOriginal) {
        didSet { Defaults.set(showOriginal, .showOriginal) }
    }
    var showRegionOutline = Defaults.bool(.showOutline, default: true) {
        didSet { Defaults.set(showRegionOutline, .showOutline) }
    }
    @ObservationIgnored let renderer = MetalVideoRenderer()
    var videoSmoothing = VideoSmoothing(rawValue: Defaults.string(.videoSmoothing) ?? "") ?? .system {
        didSet {
            Defaults.set(videoSmoothing.rawValue, .videoSmoothing)
            applySmoothing()
        }
    }
    var isSelectingRegion = false
    var isFullScreen = false
    /// Ảnh tĩnh dùng khi khoanh vùng.
    private(set) var snapshot: CGImage?

    // MARK: - Trạng thái hiển thị
    var subtitle: SubtitleState?
    var cpuPercent: Double?
    var ocrMs: Double?
    var translateMs: Double?
    var lastEngine: String?
    var quotaRemaining: Int?
    var quotaLimit: Int { profile.translate.requestsPerDay }
    var hasAPIKey = false

    // MARK: - Khởi động

    func start() async {
        guard !started else { return }

        try? profileStore.seedIfNeeded()
        profiles = (try? profileStore.list()) ?? [.default]
        let lastID = Defaults.string(.profileID)
        profile = profiles.first(where: { $0.id == lastID }) ?? profiles.first ?? .default

        let cachePath = ProfileStore.applicationSupport.appendingPathComponent("cache.sqlite").path
        let cache = (try? TranslationCache(path: cachePath)) ?? (try? TranslationCache(path: ":memory:"))
        self.cache = cache

        if let cache {
            let coordinator = PipelineCoordinator(cache: cache) { [weak self] event in
                Task { @MainActor in self?.handle(event) }
            }
            coordinator.setEnabled(translationEnabled)
            self.coordinator = coordinator
            capture.frameHandler = { buffer in coordinator.submit(FrameBox(buffer: buffer)) }
        }
        capture.statusHandler = { [weak self] status in
            Task { @MainActor in self?.handleCaptureStatus(status) }
        }
        apple.start()
        applySmoothing()
        warmUpOCR()
        refreshHasKey()
        rebuildTranslator()
        capture.setSampleEvery(profile.detector.sampleEveryNFrames)

        let granted = await Permissions.requestCapture()
        permission = granted.video
        micPermission = granted.audio

        refreshDevices()
        selectedVideoID = Defaults.string(.videoID).flatMap { id in videoDevices.contains { $0.id == id } ? id : nil }
            ?? videoDevices.first?.id
        selectedAudioID = Defaults.string(.audioID).flatMap { id in audioDevices.contains { $0.id == id } ? id : nil }
            ?? DeviceDiscovery.bestAudioMatch(forVideo: videoDevices.first { $0.id == selectedVideoID }, in: audioDevices)?.id
        selectedOutputID = Defaults.string(.outputID)

        started = true
        cpuTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.cpuPercent = self?.cpuMonitor.sample() }
        }
        observeDevices()
        startCapture()
        await refreshQuota()
    }

    /// Lần OCR đầu tiên phải nạp model Vision (có thể vài chục giây), nên chạy thử ngầm khi mở app.
    private func warmUpOCR() {
        Task.detached(priority: .utility) {
            guard let image = SelfTest.renderText("Warm up text recognition") else { return }
            _ = try? VisionOCR().recognize(image, settings: OCRSettings(), customWords: [])
        }
    }

    /// Bật/tắt đường vẽ Metal: chỉ tốn tài nguyên khi chọn chế độ mịn hoặc điểm ảnh.
    private func applySmoothing() {
        renderer.setSmoothing(videoSmoothing)
        if videoSmoothing == .system {
            capture.displayHandler = nil
        } else {
            let renderer = renderer
            capture.displayHandler = { @Sendable (buffer: CVPixelBuffer) in renderer.render(buffer) }
        }
    }

    func refreshDevices() {
        videoDevices = DeviceDiscovery.videoDevices()
        audioDevices = DeviceDiscovery.audioDevices()
        audioOutputs = DeviceDiscovery.audioOutputs()
    }

    private func observeDevices() {
        let center = NotificationCenter.default
        for name in [AVCaptureDevice.wasConnectedNotification, AVCaptureDevice.wasDisconnectedNotification] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.refreshDevices() }
            })
        }
    }

    // MARK: - Capture

    func startCapture() {
        guard permission == .granted, let videoID = selectedVideoID else {
            if permission == .granted { captureStatus = .disconnected("Chưa chọn capture card") }
            return
        }
        capture.start(videoID: videoID, audioID: micPermission == .granted ? selectedAudioID : nil,
                      volume: Float(volume), outputUID: selectedOutputID)
    }

    private func restartCapture(matchAudio: Bool) {
        if matchAudio, Defaults.string(.audioID) == nil || !audioDevices.contains(where: { $0.id == selectedAudioID }) {
            selectedAudioID = DeviceDiscovery.bestAudioMatch(
                forVideo: videoDevices.first { $0.id == selectedVideoID }, in: audioDevices)?.id
        }
        startCapture()
        Task { await coordinator?.resetState() }
    }

    private func handleCaptureStatus(_ status: CaptureStatus) {
        captureStatus = status
        if case .running(let w, let h, _, _) = status, w > 0, h > 0 {
            videoSize = CGSize(width: w, height: h)
        }
    }

    // MARK: - Pipeline

    private func handle(_ event: PipelineEvent) {
        guard translationEnabled else { return }
        switch event {
        case .recognized(_, let ms):
            ocrMs = ms
        case .translating:
            // Không hiện chữ gốc tiếng Anh kèm vòng xoay: ẩn phụ đề cũ, chờ bản dịch rồi mới hiện.
            subtitle = nil
        case .subtitle(let source, let target, let engine, let cached, let ms):
            subtitle = SubtitleState(source: source, target: target, engine: engine, cached: cached)
            lastEngine = engine
            if !cached { translateMs = ms }
            Task { await refreshQuota() }
        case .cleared:
            subtitle = nil
        }
    }

    func retranslate() {
        Task { await coordinator?.retranslate() }
    }

    func refreshQuota() async {
        quotaRemaining = await limiter?.remainingToday()
    }

    /// Dựng lại bộ dịch khi đổi profile, chế độ, model hoặc API key.
    func rebuildTranslator() {
        guard let cache else { return }
        let settings = profile.translate

        let key = "\(settings.requestsPerMinute)/\(settings.requestsPerDay)"
        if limiter == nil || key != limiterKey {
            limiterKey = key
            let restored = cache.meta("rl.day").flatMap { day in
                cache.meta("rl.count").flatMap(Int.init).map { (dayKey: day, count: $0) }
            }
            limiter = RateLimiter(perMinute: settings.requestsPerMinute, perDay: settings.requestsPerDay,
                                  restored: restored) { day, count in
                cache.setMeta("rl.day", day)
                cache.setMeta("rl.count", String(count))
            }
        }

        let online = Self.makeOnlineTranslator(settings)
        let translator = FallbackTranslator(
            mode: settings.mode, claude: online, claudeModel: settings.activeModel, provider: settings.provider,
            offline: AppleTranslator(bridge: apple), limiter: limiter)
        let profile = profile
        Task {
            await coordinator?.configure(profile: profile, translator: translator)
            await refreshQuota()
        }
    }

    /// Bộ dịch online theo nhà cung cấp và model đang chọn trong hồ sơ.
    nonisolated static func makeOnlineTranslator(_ settings: TranslateSettings) -> Translator {
        let account = Keychain.account(for: settings.provider)
        switch settings.provider {
        case .claude: return ClaudeTranslator(model: settings.model) { Keychain.read(account: account) }
        case .gemini: return GeminiTranslator(model: settings.geminiModel) { Keychain.read(account: account) }
        }
    }

    private func refreshHasKey() {
        hasAPIKey = Keychain.read(account: Keychain.account(for: profile.translate.provider)) != nil
    }

    private func profileDidChange(from old: Profile) {
        refreshHasKey()
        Defaults.set(profile.id, .profileID)
        try? profileStore.save(profile)
        if let index = profiles.firstIndex(where: { $0.id == profile.id }) {
            profiles[index] = profile
        }
        capture.setSampleEvery(profile.detector.sampleEveryNFrames)
        if old.id != profile.id { subtitle = nil }
        guard started || cache != nil else { return }
        rebuildTranslator()
    }

    // MARK: - Hành động

    func setAPIKey(_ key: String) {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        Keychain.write(trimmed, account: Keychain.account(for: profile.translate.provider))
        hasAPIKey = !trimmed.isEmpty
        rebuildTranslator()
    }

    func testTranslator() async -> String {
        let online = Self.makeOnlineTranslator(profile.translate)
        do {
            return "OK: " + (try await online.translate(TranslationRequest(text: "Welcome to Hyrule, traveler!")))
        } catch TranslationError.missingAPIKey {
            return "Chưa có API key."
        } catch TranslationError.quotaExceeded {
            return "Lỗi: hết quota (429)."
        } catch {
            return "Lỗi: \(error)"
        }
    }

    func beginRegionSelection() {
        snapshot = capture.snapshot()
        isSelectingRegion = true
    }

    /// Chọn lại vùng cho một hồ sơ: chuyển sang hồ sơ đó rồi mở màn hình chọn vùng.
    func beginRegionSelection(forProfile id: String) {
        selectProfile(id: id)
        beginRegionSelection()
        NSApp.windows.first { $0.identifier?.rawValue.contains("main") == true }?.makeKeyAndOrderFront(nil)
    }

    struct OCRTestResult {
        let text: String
        let ocrMs: Double
        let confidence: Float
    }

    /// Thử OCR trên ảnh tĩnh với các vùng đang khoanh (ghép chữ của mọi vùng), dùng cài đặt OCR của hồ sơ hiện tại.
    func testOCR(regions: [Region]) async -> OCRTestResult? {
        guard let snapshot else { return nil }
        let crops: [CGImage] = regions.compactMap { region in
            let r = region.clamped()
            let crop = CGRect(x: r.x * Double(snapshot.width), y: r.y * Double(snapshot.height),
                              width: r.width * Double(snapshot.width), height: r.height * Double(snapshot.height)).integral
            return snapshot.cropping(to: crop)
        }
        guard !crops.isEmpty else { return nil }
        let settings = profile.ocr
        let words = Array(profile.glossary.keys)
        return await Task.detached(priority: .userInitiated) {
            var texts: [String] = []
            var totalMs = 0.0
            var confidences: [Float] = []
            for cropped in crops {
                guard let prepared = Preprocessor().prepare(CIImage(cgImage: cropped), settings: settings),
                      let result = try? VisionOCR().recognize(prepared, settings: settings, customWords: words) else { continue }
                texts.append(TextNormalizer.normalize(result.text) ?? result.text)
                totalMs += result.elapsedMs
                confidences.append(result.confidence)
            }
            guard !confidences.isEmpty else { return nil }
            return OCRTestResult(text: texts.joined(separator: "\n"), ocrMs: totalMs,
                                 confidence: confidences.reduce(0, +) / Float(confidences.count))
        }.value
    }

    /// Lưu danh sách vùng: vùng đầu là vùng chính, còn lại là vùng phụ.
    func commitRegions(_ regions: [Region]) {
        let clamped = regions.map { $0.clamped() }
        if let first = clamped.first {
            profile.region = first
            profile.extraRegions = Array(clamped.dropFirst())
        }
        isSelectingRegion = false
        Task { await coordinator?.resetState() }
    }

    func toggleFullScreen() {
        (NSApp.keyWindow ?? NSApp.windows.first)?.toggleFullScreen(nil)
    }

    // MARK: - Quản lý profile

    /// Lưu thay đổi của một hồ sơ (đang dùng hoặc không).
    func updateProfile(_ updated: Profile) {
        if updated.id == profile.id {
            profile = updated
        } else if let index = profiles.firstIndex(where: { $0.id == updated.id }), profiles[index] != updated {
            profiles[index] = updated
            try? profileStore.save(updated)
        }
    }

    /// Dịch lại một câu trong lịch sử bằng AI online đang chọn (người dùng chủ động nên không tính vào bộ giới hạn).
    func regenerateOnline(_ item: CachedTranslation) async -> Bool {
        let settings = profile.translate
        let online = Self.makeOnlineTranslator(settings)
        guard let text = try? await online.translate(
            TranslationRequest(text: item.source, glossary: profile.glossary)) else { return false }
        cache?.updateTarget(key: item.key, target: text, engine: "\(settings.provider.rawValue):\(settings.activeModel)")
        return true
    }

    func selectProfile(id: String) {
        if let found = profiles.first(where: { $0.id == id }) { profile = found }
    }

    func createProfile(named name: String, basedOn base: Profile? = nil) {
        var new = base ?? .default
        new.name = name
        new.id = uniqueID(for: name)
        try? profileStore.save(new)
        profiles.append(new)
        profile = new
    }

    func deleteProfile(id: String) {
        guard profiles.count > 1, profiles.contains(where: { $0.id == id }) else { return }
        try? profileStore.delete(id: id)
        profiles.removeAll { $0.id == id }
        cache?.clear(profileId: id)
        if profile.id == id { profile = profiles[0] }
    }

    func exportProfile(_ profile: Profile, to url: URL) throws {
        try profileStore.export(profile, to: url)
    }

    func importProfile(from url: URL) throws {
        let imported = try profileStore.importProfile(from: url)
        profiles = (try? profileStore.list()) ?? profiles
        profile = imported
    }

    private func uniqueID(for name: String) -> String {
        let base = ProfileStore.makeID(from: name)
        var candidate = base
        var n = 2
        while profiles.contains(where: { $0.id == candidate }) {
            candidate = "\(base)-\(n)"
            n += 1
        }
        return candidate
    }
}

/// Lưu lựa chọn người dùng bằng UserDefaults.
enum Defaults {
    enum Key: String {
        case videoID, audioID, outputID, volume, showOriginal, profileID, showOutline, videoSmoothing
    }

    private static let store = UserDefaults.standard

    static func set(_ value: Any?, _ key: Key) { store.set(value, forKey: key.rawValue) }
    static func string(_ key: Key) -> String? { store.string(forKey: key.rawValue) }
    static func bool(_ key: Key, default value: Bool = false) -> Bool {
        store.object(forKey: key.rawValue) == nil ? value : store.bool(forKey: key.rawValue)
    }
    static func double(_ key: Key, default value: Double) -> Double {
        store.object(forKey: key.rawValue) == nil ? value : store.double(forKey: key.rawValue)
    }
}
