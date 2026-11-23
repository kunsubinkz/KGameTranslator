import XCTest
@testable import TranslatorCore

private struct FakeTranslator: Translator {
    let id: String
    let result: Result<String, TranslationError>
    func translate(_ request: TranslationRequest) async throws -> String { try result.get() }
}

final class TranslatorTests: XCTestCase {
    private let request = TranslationRequest(text: "Hello")

    private func make(mode: TranslateMode, claude: Result<String, TranslationError>,
                      offline: Result<String, TranslationError>, limiter: RateLimiter? = nil,
                      now: @escaping @Sendable () -> Date = { Date() }) -> FallbackTranslator {
        FallbackTranslator(mode: mode, claude: FakeTranslator(id: "g", result: claude), claudeModel: "m",
                           offline: FakeTranslator(id: "a", result: offline), limiter: limiter, now: now)
    }

    func testUsesClaudeWhenAvailable() async {
        let t = make(mode: .onlineWithOfflineFallback, claude: .success("Chào"), offline: .success("Xin chào"))
        let out = await t.translate(request)
        XCTAssertEqual(out, TranslationOutcome(text: "Chào", engine: .claude("m")))
    }

    func testFallsBackToOfflineOnNetworkError() async {
        let t = make(mode: .onlineWithOfflineFallback, claude: .failure(.network("x")), offline: .success("Xin chào"))
        let out = await t.translate(request)
        XCTAssertEqual(out.engine, .apple)
        XCTAssertEqual(out.text, "Xin chào")
    }

    func testFallsBackToOriginalWhenEverythingFails() async {
        let t = make(mode: .onlineWithOfflineFallback, claude: .failure(.timeout), offline: .failure(.unavailable("x")))
        let out = await t.translate(request)
        XCTAssertEqual(out, TranslationOutcome(text: "Hello", engine: .original))
    }

    func testClaudeOnlyNeverUsesOffline() async {
        let t = make(mode: .onlineOnly, claude: .failure(.emptyResponse), offline: .success("Xin chào"))
        let out = await t.translate(request)
        XCTAssertEqual(out.engine, .original)
    }

    func testOfflineOnlySkipsClaude() async {
        let t = make(mode: .offlineOnly, claude: .success("Chào"), offline: .success("Xin chào"))
        let out = await t.translate(request)
        XCTAssertEqual(out.engine, .apple)
    }

    func testQuotaErrorBacksOffClaude() async {
        final class Clock: @unchecked Sendable { var date = Date(timeIntervalSince1970: 1_800_000_000) }
        let clock = Clock()
        let t = make(mode: .onlineWithOfflineFallback, claude: .failure(.quotaExceeded), offline: .success("Xin chào"),
                     now: { clock.date })
        _ = await t.translate(request)
        clock.date.addTimeInterval(0.5)
        let during = await t.translate(request)
        XCTAssertEqual(during.engine, .apple)
    }

    func testExhaustedLimiterSkipsClaude() async {
        let limiter = RateLimiter(perMinute: 1, perDay: 1)
        let t = make(mode: .onlineWithOfflineFallback, claude: .success("Chào"), offline: .success("Xin chào"), limiter: limiter)
        let first = await t.translate(request)
        let second = await t.translate(request)
        XCTAssertEqual(first.engine, .claude("m"))
        XCTAssertEqual(second.engine, .apple)
    }

    func testGeminiResponseParsing() throws {
        let ok = Data(#"{"candidates":[{"content":{"parts":[{"text":" Xin chào \n"}]}}]}"#.utf8)
        XCTAssertEqual(try GeminiTranslator.parse(ok), "Xin chào")
        let blocked = Data(#"{"promptFeedback":{"blockReason":"SAFETY"}}"#.utf8)
        XCTAssertThrowsError(try GeminiTranslator.parse(blocked))
    }

    func testGeminiProviderUsesGeminiEngine() async {
        let t = FallbackTranslator(mode: .onlineOnly, claude: FakeTranslator(id: "g", result: .success("Chào")),
                                   claudeModel: "m", provider: .gemini, offline: nil, limiter: nil)
        let out = await t.translate(TranslationRequest(text: "Hello"))
        XCTAssertEqual(out.engine, .gemini("m"))
    }

    func testClaudeResponseParsing() throws {
        let ok = Data(#"{"content":[{"type":"text","text":" Xin chào \n"}],"stop_reason":"end_turn"}"#.utf8)
        XCTAssertEqual(try ClaudeTranslator.parse(ok), "Xin chào")
        let blocked = Data(#"{"content":[],"stop_reason":"refusal"}"#.utf8)
        XCTAssertThrowsError(try ClaudeTranslator.parse(blocked))
        XCTAssertThrowsError(try ClaudeTranslator.parse(Data("{}".utf8)))
    }

    func testPromptIncludesGlossaryAndContext() {
        let system = PromptBuilder.systemInstruction(glossary: ["Link": "Link", "Rupee": "Rupee"])
        XCTAssertTrue(system.contains("Thuật ngữ:\nLink = Link\nRupee = Rupee"))
        let req = TranslationRequest(text: "Watch out!", context: [ContextPair(source: "Hi", target: "Chào")])
        let user = PromptBuilder.userContent(for: req)
        XCTAssertTrue(user.contains("EN: Hi\nVI: Chào"))
        XCTAssertTrue(user.hasSuffix("Watch out!"))
    }

    func testUpgradeUsesClaudeEvenInOfflineFirstFlow() async {
        let t = make(mode: .onlineWithOfflineFallback, claude: .success("Chào"), offline: .success("Xin chào"))
        let upgraded = await t.upgrade(request)
        XCTAssertEqual(upgraded, TranslationOutcome(text: "Chào", engine: .claude("m")))
        let offlineOnly = make(mode: .offlineOnly, claude: .success("Chào"), offline: .success("Xin chào"))
        let none = await offlineOnly.upgrade(request)
        XCTAssertNil(none)
    }

    func testOlderProfileJSONStillDecodes() throws {
        let json = #"{"id":"old","name":"Old","region":{"x":0.1,"y":0.2,"width":0.3,"height":0.4},"subtitle":{"position":"below","fontSize":30}}"#
        let profile = try JSONDecoder().decode(Profile.self, from: Data(json.utf8))
        XCTAssertEqual(profile.subtitle.position, .below)
        XCTAssertEqual(profile.subtitle.backgroundOpacity, 0.6)
        XCTAssertTrue(profile.translate.upgradeOffline)
        XCTAssertEqual(profile.detector, DetectorSettings())
    }

    func testCacheStatsAndEngineOnUpdate() throws {
        let cache = try TranslationCache(path: ":memory:")
        cache.store(profileId: "p", source: "A long sentence", target: "Câu dài", engine: "apple")
        _ = cache.lookup(profileId: "p", source: "A long sentence")
        _ = cache.lookup(profileId: "p", source: "A long sentence")
        XCTAssertEqual(cache.stats(profileId: "p").count, 1)
        XCTAssertEqual(cache.stats(profileId: "p").hits, 2)
        let key = TranslationCache.key(profileId: "p", source: "A long sentence")
        cache.updateTarget(key: key, target: "Một câu dài", engine: "claude:m")
        XCTAssertEqual(cache.history(profileId: "p").first?.engine, "claude:m")
    }

    func testProfileRoundTripAndStore() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("gt-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ProfileStore(directory: dir)
        try store.seedIfNeeded()
        XCTAssertEqual(try store.list().map(\.id).sorted(), ["default", "zelda-totk"])
        var p = Profile.zeldaTOTK
        p.subtitle.fontSize = 40
        try store.save(p)
        XCTAssertEqual(store.load(id: "zelda-totk")?.subtitle.fontSize, 40)
        XCTAssertEqual(ProfileStore.makeID(from: "Đường Về Nhà 2!"), "duong-ve-nha-2")
    }
}
