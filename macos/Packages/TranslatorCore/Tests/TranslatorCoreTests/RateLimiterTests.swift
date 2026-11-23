import XCTest
@testable import TranslatorCore

final class RateLimiterTests: XCTestCase {
    func testPerMinuteBucketRefills() async {
        let t0 = Date(timeIntervalSince1970: 1_800_000_000)
        let limiter = RateLimiter(perMinute: 2, perDay: 100, now: t0)
        let first = await limiter.tryAcquire(now: t0)
        let second = await limiter.tryAcquire(now: t0)
        let third = await limiter.tryAcquire(now: t0)
        XCTAssertTrue(first)
        XCTAssertTrue(second)
        XCTAssertFalse(third)
        let later = await limiter.tryAcquire(now: t0.addingTimeInterval(31))
        XCTAssertTrue(later)
    }

    func testDailyLimitAndReset() async {
        let t0 = Date(timeIntervalSince1970: 1_800_000_000)
        let limiter = RateLimiter(perMinute: 1000, perDay: 2, now: t0)
        _ = await limiter.tryAcquire(now: t0)
        _ = await limiter.tryAcquire(now: t0)
        let blocked = await limiter.tryAcquire(now: t0.addingTimeInterval(60))
        XCTAssertFalse(blocked)
        let remaining = await limiter.remainingToday(now: t0.addingTimeInterval(60))
        XCTAssertEqual(remaining, 0)
        let nextDay = await limiter.tryAcquire(now: t0.addingTimeInterval(2 * 86_400))
        XCTAssertTrue(nextDay)
    }

    func testRestoredCountIsUsedOnlyForSameDay() async {
        let t0 = Date(timeIntervalSince1970: 1_800_000_000)
        let same = RateLimiter(perMinute: 10, perDay: 5, now: t0, restored: (RateLimiter.dayKey(for: t0), 4))
        let remainingSame = await same.remainingToday(now: t0)
        XCTAssertEqual(remainingSame, 1)
        let stale = RateLimiter(perMinute: 10, perDay: 5, now: t0, restored: ("1999-01-01", 4))
        let remainingStale = await stale.remainingToday(now: t0)
        XCTAssertEqual(remainingStale, 5)
    }
}
