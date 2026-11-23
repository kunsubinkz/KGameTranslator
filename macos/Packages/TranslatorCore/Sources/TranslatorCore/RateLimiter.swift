import Foundation

/// Token bucket theo phút kèm bộ đếm theo ngày (đặt lại lúc nửa đêm giờ Thái Bình Dương).
public actor RateLimiter {
    public typealias Persist = @Sendable (_ dayKey: String, _ count: Int) -> Void

    private let perMinute: Double
    private let perDay: Int
    private var tokens: Double
    private var lastRefill: Date
    private var dayKey: String
    private var dayCount: Int
    private let persist: Persist?

    public init(perMinute: Int, perDay: Int, now: Date = Date(),
                restored: (dayKey: String, count: Int)? = nil, persist: Persist? = nil) {
        self.perMinute = Double(max(perMinute, 1))
        self.perDay = max(perDay, 1)
        self.tokens = Double(max(perMinute, 1))
        self.lastRefill = now
        self.persist = persist
        let today = Self.dayKey(for: now)
        if let restored, restored.dayKey == today {
            self.dayKey = today
            self.dayCount = restored.count
        } else {
            self.dayKey = today
            self.dayCount = 0
        }
    }

    public static func dayKey(for date: Date) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Los_Angeles") ?? .gmt
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }

    /// Thử lấy một lượt gọi API. Trả về false nếu hết hạn mức phút hoặc ngày.
    public func tryAcquire(now: Date = Date()) -> Bool {
        rollDayIfNeeded(now)
        refill(now)
        guard dayCount < perDay, tokens >= 1 else { return false }
        tokens -= 1
        dayCount += 1
        persist?(dayKey, dayCount)
        return true
    }

    public func remainingToday(now: Date = Date()) -> Int {
        rollDayIfNeeded(now)
        return max(perDay - dayCount, 0)
    }

    public var dailyLimit: Int { perDay }

    private func refill(_ now: Date) {
        let elapsed = max(now.timeIntervalSince(lastRefill), 0)
        tokens = min(perMinute, tokens + elapsed * perMinute / 60)
        lastRefill = now
    }

    private func rollDayIfNeeded(_ now: Date) {
        let key = Self.dayKey(for: now)
        if key != dayKey {
            dayKey = key
            dayCount = 0
            persist?(dayKey, dayCount)
        }
    }
}
