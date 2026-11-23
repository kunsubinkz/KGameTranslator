import XCTest
@testable import TranslatorCore

final class TranslationCacheTests: XCTestCase {
    func testStoreLookupAndHits() throws {
        let cache = try TranslationCache(path: ":memory:")
        XCTAssertNil(cache.lookup(profileId: "p", source: "Hello"))
        cache.store(profileId: "p", source: "Hello", target: "Xin chào", engine: "apple")
        XCTAssertEqual(cache.lookup(profileId: "p", source: "hello")?.target, "Xin chào")
        XCTAssertNil(cache.lookup(profileId: "other", source: "Hello"))
        XCTAssertEqual(cache.history().first?.hits, 1)
    }

    func testManualEditReplacesTranslation() throws {
        let cache = try TranslationCache(path: ":memory:")
        cache.store(profileId: "p", source: "Hello", target: "Chào", engine: "apple")
        let key = TranslationCache.key(profileId: "p", source: "Hello")
        cache.updateTarget(key: key, target: "Xin chào")
        let hit = cache.lookup(profileId: "p", source: "Hello")
        XCTAssertEqual(hit?.target, "Xin chào")
        XCTAssertEqual(hit?.engine, "manual")
    }

    func testMemoryCacheIsBounded() throws {
        let cache = try TranslationCache(path: ":memory:")
        for i in 0..<(TranslationCache.memoryLimit + 50) {
            cache.store(profileId: "p", source: "sentence \(i)", target: "câu \(i)", engine: "apple")
        }
        XCTAssertEqual(cache.lookup(profileId: "p", source: "sentence 0")?.target, "câu 0")
    }

    func testMetaRoundTrip() throws {
        let cache = try TranslationCache(path: ":memory:")
        XCTAssertNil(cache.meta("k"))
        cache.setMeta("k", "v1")
        cache.setMeta("k", "v2")
        XCTAssertEqual(cache.meta("k"), "v2")
    }
}
