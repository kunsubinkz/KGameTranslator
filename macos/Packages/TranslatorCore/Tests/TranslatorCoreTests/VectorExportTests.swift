import Foundation
import XCTest
@testable import TranslatorCore

/// Xuất test vector cho bản Windows (C#): chạy hàm Swift thật trên các đầu vào cố định rồi ghi JSON.
/// Chỉ chạy khi đặt biến môi trường, ví dụ:
///   GT_EXPORT_VECTORS=../../../windows/tests/vectors swift test --filter VectorExportTests
/// Không đặt biến thì bị bỏ qua, bộ test thường không đổi.
final class VectorExportTests: XCTestCase {
    private var outputDirectory: URL!

    override func setUpWithError() throws {
        guard let path = ProcessInfo.processInfo.environment["GT_EXPORT_VECTORS"], !path.isEmpty else {
            throw XCTSkip("Đặt GT_EXPORT_VECTORS=<thư mục> để xuất test vector")
        }
        outputDirectory = URL(fileURLWithPath: path, isDirectory: true)
        try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)
    }

    private func write(_ name: String, _ object: [String: Any]) throws {
        var root = object
        root["_generatedBy"] = "macos/Packages/TranslatorCore/Tests/TranslatorCoreTests/VectorExportTests.swift"
        let data = try JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        try data.write(to: outputDirectory.appendingPathComponent(name + ".json"))
    }

    private func jsonObject(_ data: Data) throws -> Any {
        try JSONSerialization.jsonObject(with: data)
    }

    private func errorName(_ error: Error) -> String {
        guard let e = error as? TranslationError else { return "other" }
        switch e {
        case .missingAPIKey: return "missingAPIKey"
        case .quotaExceeded: return "quotaExceeded"
        case .network(let message): return "network:" + message
        case .emptyResponse: return "emptyResponse"
        case .blocked: return "blocked"
        case .unavailable(let message): return "unavailable:" + message
        case .timeout: return "timeout"
        }
    }

    // MARK: - TextNormalizer

    func testExportTextNormalizer() throws {
        let inputs = [
            "Hello   there,\n  traveler. \n",
            "| think we should go",
            "Wel|, that was close",
            "Take this sword. ▼",
            "Hi", "1234 !!", "  \n ", "",
            "l'm not sure. l will go.",
            "l'll be there and l've seen it, l'd say",
            "The kingdom needs you now more than ever, Link.\nGo to the temple and find the sage. ►",
            "Press  A  to continue >",
            "Embry: Oh, I think it sounds\r\nfascinating!  »",
            "• Mighty Cucco\nSouth Akkala Stable\nPenn",
            "|t's a |ong way |",
            "Ünïcödé text: café, naïve, Æsir",
            "ABC", "ab1", "...a",
            "Line one\n\n\nLine two\t\twith tabs",
            "He said \"go!\" ▼▼",
        ]
        let normalize: [[String: Any]] = inputs.map { input in
            ["input": input, "expected": TextNormalizer.normalize(input) as Any? ?? NSNull()]
        }

        let pairs: [(String, String)] = [
            ("kitten", "sitting"), ("", ""), ("abc", ""), ("", "abc"),
            ("Welcome to Hyrule, traveler!", "Welcome to Hyrule, traveler!"),
            ("Welcome to Hyrule, traveler!", "welcome to hyrule, traveler!"),
            ("Welcome to Hyrule", "Goodbye and good luck"),
            ("The kingdom needs you now more than ever, Link.", "The kingdom needs you now more than ever, Llnk."),
            ("Take this sword.", "Take this sword!"),
            ("I'm not sure.", "l'm not sure."),
            ("café", "cafe"), ("Æsir", "æsir"),
        ]
        let similarity: [[String: Any]] = pairs.map { a, b in
            ["a": a, "b": b,
             "levenshtein": TextNormalizer.levenshtein(a, b),
             "similarity": TextNormalizer.similarity(a, b),
             "isSameSentence": TextNormalizer.isSameSentence(a, b)]
        }
        let cerPairs: [(String, String)] = [("abcd", "abcd"), ("abcd", "abxd"), ("", ""), ("", "x"), ("hello", "")]
        let cer: [[String: Any]] = cerPairs.map { e, a in
            ["expected": e, "actual": a, "cer": TextNormalizer.characterErrorRate(expected: e, actual: a)]
        }
        try write("text_normalizer", ["normalize": normalize, "similarity": similarity, "characterErrorRate": cer,
                                      "isSameSentenceDefaultThreshold": 0.92])
    }

    // MARK: - ChangeDetector

    func testExportChangeDetector() throws {
        let size = 64
        let blank = [UInt8](repeating: 20, count: size)
        func text(_ seed: Int) -> [UInt8] {
            (0..<size).map { UInt8(truncatingIfNeeded: (($0 * 37 + seed * 91) % 2 == 0) ? 240 : 30) }
        }
        func noisy(_ base: [UInt8], _ delta: Int) -> [UInt8] {
            base.enumerated().map { i, v in UInt8(clamping: Int(v) + (i % 4 == 0 ? delta : 0)) }
        }
        let config = ChangeDetector.Config(changeThreshold: 5, stableMs: 350, hideDelayMs: 800, blankStdDev: 4)

        enum Step { case frame([UInt8], Double), reset }
        let scenarios: [(String, [Step])] = [
            ("stable_once", [.frame(text(1), 0), .frame(text(1), 250), .frame(text(1), 500), .frame(text(1), 750), .frame(text(1), 1000)]),
            ("changing_delays", [.frame(text(1), 0), .frame(text(2), 250), .frame(text(2), 500), .frame(text(2), 750)]),
            ("new_sentence", [.frame(text(1), 0), .frame(text(1), 400), .frame(text(2), 600), .frame(text(2), 1000), .frame(text(2), 1200)]),
            ("blank_hides_after_delay", [.frame(text(1), 0), .frame(text(1), 400), .frame(blank, 500), .frame(blank, 900),
                                         .frame(blank, 1300), .frame(blank, 1400), .frame(blank, 2000)]),
            ("blank_flicker_keeps_text", [.frame(text(1), 0), .frame(text(1), 400), .frame(blank, 500), .frame(text(1), 700),
                                          .frame(blank, 900), .frame(blank, 1500), .frame(blank, 1800)]),
            ("blank_without_text", [.frame(blank, 0), .frame(blank, 1000), .frame(blank, 2000)]),
            ("small_noise_below_threshold", [.frame(text(3), 0), .frame(noisy(text(3), 8), 100), .frame(noisy(text(3), 8), 500),
                                             .frame(noisy(text(3), 40), 600), .frame(noisy(text(3), 40), 1000)]),
            ("reset_restarts", [.frame(text(1), 0), .frame(text(1), 400), .reset, .frame(text(1), 500), .frame(text(1), 900)]),
            ("empty_samples", [.frame([], 0), .frame(text(1), 100), .frame(text(1), 500)]),
        ]
        var out: [[String: Any]] = []
        for (name, steps) in scenarios {
            var detector = ChangeDetector(config: config)
            var recorded: [[String: Any]] = []
            for step in steps {
                switch step {
                case .reset:
                    detector.reset()
                    recorded.append(["reset": true])
                case let .frame(samples, ms):
                    let event = detector.process(samples: samples, nowMs: ms)
                    let label: String
                    switch event {
                    case .none: label = "none"
                    case .textStable: label = "textStable"
                    case .cleared: label = "cleared"
                    }
                    recorded.append(["samples": samples.map(Int.init), "nowMs": ms, "expected": label])
                }
            }
            out.append(["name": name, "steps": recorded])
        }
        let stats: [[String: Any]] = [text(1), blank, noisy(text(3), 8)].map { s in
            ["samples": s.map(Int.init), "standardDeviation": ChangeDetector.standardDeviation(s)]
        }
        let mad: [String: Any] = ["a": text(1).map(Int.init), "b": text(2).map(Int.init),
                                  "meanAbsoluteDifference": ChangeDetector.meanAbsoluteDifference(text(1), text(2))]
        try write("change_detector", [
            "config": ["changeThreshold": config.changeThreshold, "stableMs": config.stableMs,
                       "hideDelayMs": config.hideDelayMs, "blankStdDev": config.blankStdDev],
            "defaultConfig": ["changeThreshold": 2.0, "stableMs": 350.0, "hideDelayMs": 800.0, "blankStdDev": 4.0],
            "scenarios": out, "standardDeviation": stats, "meanAbsoluteDifference": [mad],
        ])
    }

    // MARK: - PromptBuilder

    func testExportPromptBuilder() throws {
        let glossaries: [[String: String]] = [
            [:],
            ["Link": "Link", "Hyrule": "Hyrule", "Rupee": "Rupee", "Zelda": "Zelda"],
            ["zelda": "Zelda", "Zelda": "Công chúa Zelda", "Ébène": "Gỗ mun", "apple": "táo", "Apple": "Táo", "_x": "y"],
        ]
        let contexts: [[ContextPair]] = [
            [],
            [ContextPair(source: "Hello there.", target: "Xin chào.")],
            [ContextPair(source: "Where is the temple?", target: "Ngôi đền ở đâu?"),
             ContextPair(source: "Go north.", target: "Đi về phía bắc."),
             ContextPair(source: "Thanks!", target: "Cảm ơn!")],
        ]
        var system: [[String: Any]] = []
        for g in glossaries {
            system.append(["glossary": g, "expected": PromptBuilder.systemInstruction(glossary: g)])
        }
        var user: [[String: Any]] = []
        for c in contexts {
            let request = TranslationRequest(text: "The kingdom needs you now more than ever, Link.", context: c)
            user.append(["text": request.text,
                         "context": c.map { ["source": $0.source, "target": $0.target] },
                         "expected": PromptBuilder.userContent(for: request)])
        }
        try write("prompt_builder", ["baseInstruction": PromptBuilder.baseInstruction,
                                     "systemInstruction": system, "userContent": user,
                                     "_note": "Thuật ngữ sắp theo phép so sánh String < của Swift (thứ tự Unicode scalar sau chuẩn hóa)"])
    }

    // MARK: - TranslationCache key

    func testExportCacheKeys() throws {
        let cases: [(String, String)] = [
            ("default", "Hello there"), ("default", "HELLO THERE"), ("zelda-totk", "Hello there"),
            ("zelda-totk", "The kingdom needs you now more than ever, Link."),
            ("p", "Café NAÏVE Ünïcödé"), ("p", "İstanbul"), ("p", "ß straße"), ("p", ""),
            ("vi", "Tiếng Việt CÓ DẤU"), ("p", "emoji 🎮 GAME"),
        ]
        let out: [[String: Any]] = cases.map { profile, source in
            ["profileId": profile, "source": source, "key": TranslationCache.key(profileId: profile, source: source)]
        }
        try write("cache_key", ["cases": out,
                                "_note": "key = SHA-256 hex chữ thường của UTF-8(profileId + \"\\n\" + source.lowercased()) theo Swift"])
    }

    // MARK: - RateLimiter

    func testExportRateLimiter() async throws {
        // 2026-11-23 10:00:00 UTC = 02:00 giờ Thái Bình Dương (PST).
        let base = Date(timeIntervalSince1970: 1_795_428_000)
        struct Op { let kind: String; let offset: Double }
        let scenarios: [(name: String, perMinute: Int, perDay: Int, restored: (String, Int)?, ops: [Op])] = [
            ("burst_then_refill", 3, 100, nil,
             [Op(kind: "acquire", offset: 0), Op(kind: "acquire", offset: 0), Op(kind: "acquire", offset: 0),
              Op(kind: "acquire", offset: 1), Op(kind: "acquire", offset: 20), Op(kind: "acquire", offset: 21),
              Op(kind: "remaining", offset: 21)]),
            ("daily_limit", 60, 3, nil,
             [Op(kind: "acquire", offset: 0), Op(kind: "acquire", offset: 1), Op(kind: "acquire", offset: 2),
              Op(kind: "acquire", offset: 3), Op(kind: "remaining", offset: 3)]),
            ("day_rolls_at_pacific_midnight", 60, 2, nil,
             [Op(kind: "acquire", offset: 0), Op(kind: "acquire", offset: 1), Op(kind: "acquire", offset: 2),
              Op(kind: "acquire", offset: 22 * 3600 - 1), Op(kind: "acquire", offset: 22 * 3600 + 1),
              Op(kind: "remaining", offset: 22 * 3600 + 2)]),
            ("restored_same_day", 60, 5, (RateLimiter.dayKey(for: base), 4),
             [Op(kind: "remaining", offset: 0), Op(kind: "acquire", offset: 0), Op(kind: "acquire", offset: 1)]),
            ("restored_other_day", 60, 5, ("2000-01-01", 4),
             [Op(kind: "remaining", offset: 0), Op(kind: "acquire", offset: 0)]),
            ("min_values_clamped", 0, 0, nil,
             [Op(kind: "acquire", offset: 0), Op(kind: "acquire", offset: 120), Op(kind: "remaining", offset: 120)]),
        ]
        var out: [[String: Any]] = []
        for s in scenarios {
            let limiter = RateLimiter(perMinute: s.perMinute, perDay: s.perDay, now: base,
                                      restored: s.restored.map { (dayKey: $0.0, count: $0.1) })
            var steps: [[String: Any]] = []
            for op in s.ops {
                let at = base.addingTimeInterval(op.offset)
                if op.kind == "acquire" {
                    steps.append(["op": "acquire", "offsetSeconds": op.offset, "expected": await limiter.tryAcquire(now: at)])
                } else {
                    steps.append(["op": "remaining", "offsetSeconds": op.offset, "expected": await limiter.remainingToday(now: at)])
                }
            }
            var entry: [String: Any] = ["name": s.name, "perMinute": s.perMinute, "perDay": s.perDay,
                                        "startUnixSeconds": base.timeIntervalSince1970, "steps": steps]
            if let r = s.restored { entry["restored"] = ["dayKey": r.0, "count": r.1] }
            out.append(entry)
        }
        // Mốc ngày quanh nửa đêm và đổi giờ mùa hè/đông ở Los Angeles.
        let instants: [Double] = [
            1_795_428_000, 1_795_507_199, 1_795_507_200, 1_795_510_800,
            1_772_956_799, 1_772_956_800, 1_773_039_599, 1_773_039_600, // nửa đêm quanh 2026-03-08 (bắt đầu giờ mùa hè)
            1_793_516_399, 1_793_516_400, 1_793_606_399, 1_793_606_400, // nửa đêm quanh 2026-11-01 (kết thúc giờ mùa hè)
            0, 1_704_067_200,
        ]
        let dayKeys: [[String: Any]] = instants.map { ["unixSeconds": $0, "dayKey": RateLimiter.dayKey(for: Date(timeIntervalSince1970: $0))] }
        try write("rate_limiter", ["scenarios": out, "dayKey": dayKeys, "timeZone": "America/Los_Angeles"])
    }

    // MARK: - Profile

    func testExportProfiles() throws {
        let inputs: [(String, String)] = [
            ("minimal", #"{"id":"x"}"#),
            ("legacy_gemini", #"{"id":"old","name":"Old","region":{"x":0.1,"y":0.7,"width":0.8,"height":0.2},"translate":{"mode":"geminiWithOfflineFallback","model":"gemini-flash-lite-latest","contextLines":2}}"#),
            ("legacy_gemini_only", #"{"id":"g","translate":{"mode":"geminiOnly","model":"gemini-2.5-flash"}}"#),
            ("full", #"{"id":"full","name":"Full","region":{"x":0.18,"y":0.74,"width":0.64,"height":0.2},"extraRegions":[{"x":0.2,"y":0.6,"width":0.1,"height":0.05}],"ocr":{"level":"fast","scale":3,"invert":true,"contrast":1.5,"minimumTextHeight":0.05},"detector":{"sampleEveryNFrames":10,"changeThreshold":6,"stableMs":400,"hideDelayMs":900},"translate":{"mode":"offlineOnly","provider":"gemini","model":"claude-sonnet-5-5","geminiModel":"gemini-pro-latest","contextLines":4,"requestsPerMinute":30,"requestsPerDay":1000,"upgradeOffline":false},"subtitle":{"position":"free","fontSize":32,"freeX":0.4,"freeY":0.8,"backgroundOpacity":0.3,"alignment":"justified","maxLines":40},"glossary":{"Link":"Link"}}"#),
            ("max_lines_low", #"{"id":"m","subtitle":{"maxLines":0}}"#),
            ("unknown_keys_ignored", #"{"id":"u","futureKey":123,"subtitle":{"position":"below","somethingNew":true}}"#),
        ]
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        var decode: [[String: Any]] = []
        for (name, json) in inputs {
            let profile = try JSONDecoder().decode(Profile.self, from: Data(json.utf8))
            decode.append(["name": name, "input": json, "expected": try jsonObject(encoder.encode(profile)),
                           "allRegions": try jsonObject(encoder.encode(profile.allRegions)),
                           "unionRegion": try jsonObject(encoder.encode(profile.unionRegion)),
                           "activeModel": profile.translate.activeModel])
        }
        let builtIns: [String: Any] = [
            "default": try jsonObject(encoder.encode(Profile.default)),
            "zeldaTOTK": try jsonObject(encoder.encode(Profile.zeldaTOTK)),
        ]
        let regions: [Region] = [
            Region(x: -0.2, y: 1.5, width: 2, height: -1), Region(x: 0.5, y: 0.5, width: 0.8, height: 0.8),
            Region(x: 0.99, y: 0.99, width: 0.5, height: 0.5), Region(x: 0.1, y: 0.2, width: 0.3, height: 0.4),
        ]
        let clamped: [[String: Any]] = try regions.map {
            ["input": try jsonObject(encoder.encode($0)), "expected": try jsonObject(encoder.encode($0.clamped()))]
        }
        var providers: [[String: Any]] = []
        for p in TranslateProvider.allCases {
            providers.append(["provider": p.rawValue, "title": p.title, "keyPlaceholder": p.keyPlaceholder,
                              "presets": p.presets.map { ["id": $0.id, "title": $0.title] }])
        }
        try write("profile", ["decode": decode, "builtIns": builtIns, "regionClamped": clamped, "providers": providers,
                              "_note": "expected = Profile sau khi đọc rồi ghi lại (khóa sắp xếp). So sánh theo cấu trúc JSON, số thực có sai số 1e-9"])
    }

    // MARK: - Claude / Gemini

    func testExportTranslators() async throws {
        let request = TranslationRequest(
            text: "Welcome to Hyrule, traveler!",
            context: [ContextPair(source: "Hello.", target: "Xin chào.")],
            glossary: ["Hyrule": "Hyrule", "Link": "Link"])

        var claudeRequests: [[String: Any]] = []
        for model in ["claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-sonnet-5", "claude-haiku-4-5", "my-custom-model"] {
            let urlRequest = try ClaudeTranslator(model: model) { "TEST_KEY" }.makeURLRequest(for: request, apiKey: "TEST_KEY")
            claudeRequests.append(["model": model, "method": urlRequest.httpMethod ?? "",
                                   "url": urlRequest.url?.absoluteString ?? "",
                                   "headers": urlRequest.allHTTPHeaderFields ?? [:],
                                   "body": try jsonObject(urlRequest.httpBody ?? Data())])
        }
        let geminiURLRequest = try GeminiTranslator(model: "gemini-flash-lite-latest") { "TEST_KEY" }
            .makeURLRequest(for: request, apiKey: "TEST_KEY")
        let geminiRequests: [[String: Any]] = [[
            "model": "gemini-flash-lite-latest", "method": geminiURLRequest.httpMethod ?? "",
            "url": geminiURLRequest.url?.absoluteString ?? "",
            "headers": geminiURLRequest.allHTTPHeaderFields ?? [:],
            "body": try jsonObject(geminiURLRequest.httpBody ?? Data()),
        ]]

        func parseCases(_ bodies: [String], _ parse: (Data) throws -> String) -> [[String: Any]] {
            bodies.map { body in
                do { return ["response": body, "result": try parse(Data(body.utf8))] }
                catch { return ["response": body, "error": errorName(error)] }
            }
        }
        let claudeParse = parseCases([
            #"{"content":[{"type":"text","text":" Xin chào \n"}],"stop_reason":"end_turn"}"#,
            #"{"content":[{"type":"thinking","thinking":""},{"type":"text","text":"Chào "},{"type":"text","text":"bạn"}],"stop_reason":"end_turn"}"#,
            #"{"content":[],"stop_reason":"refusal"}"#,
            #"{"content":[{"type":"text","text":"   "}],"stop_reason":"end_turn"}"#,
            #"{}"#, #"not json"#,
        ], ClaudeTranslator.parse)
        let geminiParse = parseCases([
            #"{"candidates":[{"content":{"parts":[{"text":" Xin chào \n"}]}}]}"#,
            #"{"candidates":[{"content":{"parts":[{"text":"Chào "},{"text":"bạn"}]}}]}"#,
            #"{"promptFeedback":{"blockReason":"SAFETY"}}"#,
            #"{"candidates":[{"finishReason":"SAFETY","content":{"parts":[]}}]}"#,
            #"{"candidates":[]}"#, #"{}"#,
        ], GeminiTranslator.parse)

        // Ánh xạ mã HTTP → kết quả/lỗi, chạy qua URLSession giả.
        let statuses: [(Int, String)] = [
            (200, #"{"content":[{"type":"text","text":"Xin chào"}],"stop_reason":"end_turn"}"#),
            (429, #"{"type":"error","error":{"type":"rate_limit_error"}}"#),
            (401, #"{"type":"error","error":{"type":"authentication_error"}}"#),
            (403, #"{"type":"error","error":{"type":"permission_error"}}"#),
            (400, #"{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API."}}"#),
            (400, #"{"type":"error","error":{"type":"invalid_request_error","message":"bad model"}}"#),
            (500, #"{}"#), (529, #"{"type":"error","error":{"type":"overloaded_error"}}"#),
        ]
        let geminiStatuses: [(Int, String)] = [
            (200, #"{"candidates":[{"content":{"parts":[{"text":"Xin chào"}]}}]}"#),
            (429, #"{}"#), (400, #"{}"#), (403, #"{}"#), (500, #"{}"#),
        ]
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubURLProtocol.self]
        let session = URLSession(configuration: config)

        func run(_ translator: Translator, _ cases: [(Int, String)]) async -> [[String: Any]] {
            var out: [[String: Any]] = []
            for (status, body) in cases {
                StubURLProtocol.response = (status, Data(body.utf8))
                do {
                    let text = try await translator.translate(request)
                    out.append(["status": status, "response": body, "result": text])
                } catch {
                    out.append(["status": status, "response": body, "error": errorName(error)])
                }
            }
            return out
        }
        let claudeStatus = await run(ClaudeTranslator(model: "claude-haiku-5-5", session: session) { "TEST_KEY" }, statuses)
        let geminiStatus = await run(GeminiTranslator(model: "gemini-flash-lite-latest", session: session) { "TEST_KEY" }, geminiStatuses)

        var missingKey: [String: Any] = [:]
        do { _ = try await ClaudeTranslator(model: "claude-haiku-5-5", session: session) { nil }.translate(request) }
        catch { missingKey["claude"] = errorName(error) }
        do { _ = try await GeminiTranslator(model: "gemini-flash-lite-latest", session: session) { "" }.translate(request) }
        catch { missingKey["gemini"] = errorName(error) }

        try write("translators", [
            "request": ["text": request.text, "context": request.context.map { ["source": $0.source, "target": $0.target] },
                        "glossary": request.glossary, "apiKey": "TEST_KEY"],
            "claude": ["requests": claudeRequests, "parse": claudeParse, "httpStatus": claudeStatus,
                       "id": ClaudeTranslator(model: "claude-haiku-5-5") { nil }.id],
            "gemini": ["requests": geminiRequests, "parse": geminiParse, "httpStatus": geminiStatus,
                       "id": GeminiTranslator(model: "gemini-flash-lite-latest") { nil }.id],
            "missingKey": missingKey,
            "engineLabels": [
                "claude": TranslationOutcome.Engine.claude("m").label, "gemini": TranslationOutcome.Engine.gemini("m").label,
                "apple": TranslationOutcome.Engine.apple.label, "original": TranslationOutcome.Engine.original.label,
            ],
            "_note": "body so sánh theo cấu trúc JSON (thứ tự khóa không quan trọng). Lỗi network kèm thông điệp sau dấu ':'",
        ])
    }
}

/// URLProtocol giả trả về mã HTTP và nội dung cho trước.
final class StubURLProtocol: URLProtocol {
    nonisolated(unsafe) static var response: (Int, Data) = (200, Data())

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let (status, data) = Self.response
        let http = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: nil)!
        client?.urlProtocol(self, didReceive: http, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
