import Foundation

/// Phát hiện chữ mới và đã ổn định trong vùng hộp thoại. Thuần dữ liệu: thời gian được truyền từ ngoài vào để dễ test.
public struct ChangeDetector: Sendable {
    public enum Event: Equatable, Sendable {
        case none
        /// Chữ mới đã dừng thay đổi đủ lâu, nên OCR.
        case textStable
        /// Hộp thoại đã đóng (vùng trống đủ lâu), nên ẩn phụ đề.
        case cleared
    }

    public struct Config: Sendable, Equatable {
        public var changeThreshold: Double
        public var stableMs: Double
        public var hideDelayMs: Double
        /// Độ lệch chuẩn dưới ngưỡng này coi là vùng trống.
        public var blankStdDev: Double

        public init(changeThreshold: Double = 2.0, stableMs: Double = 350, hideDelayMs: Double = 800, blankStdDev: Double = 4.0) {
            self.changeThreshold = changeThreshold
            self.stableMs = stableMs
            self.hideDelayMs = hideDelayMs
            self.blankStdDev = blankStdDev
        }
    }

    public var config: Config
    private var previous: [UInt8]?
    private var lastChangeMs: Double = 0
    private var pendingStable = false
    private var hasText = false
    private var blankSinceMs: Double?

    public init(config: Config = Config()) {
        self.config = config
    }

    public mutating func reset() {
        previous = nil
        pendingStable = false
        hasText = false
        blankSinceMs = nil
    }

    /// `samples` là ảnh xám thu nhỏ của vùng hộp thoại.
    public mutating func process(samples: [UInt8], nowMs: Double) -> Event {
        guard !samples.isEmpty else { return .none }
        defer { previous = samples }

        if Self.standardDeviation(samples) < config.blankStdDev {
            pendingStable = false
            guard hasText else { return .none }
            let since = blankSinceMs ?? nowMs
            blankSinceMs = since
            if nowMs - since >= config.hideDelayMs {
                hasText = false
                blankSinceMs = nil
                return .cleared
            }
            return .none
        }
        blankSinceMs = nil

        let diff = previous.map { Self.meanAbsoluteDifference($0, samples) } ?? .infinity
        if diff > config.changeThreshold {
            lastChangeMs = nowMs
            pendingStable = true
            return .none
        }
        if pendingStable, nowMs - lastChangeMs >= config.stableMs {
            pendingStable = false
            hasText = true
            return .textStable
        }
        return .none
    }

    static func meanAbsoluteDifference(_ a: [UInt8], _ b: [UInt8]) -> Double {
        guard a.count == b.count, !a.isEmpty else { return .infinity }
        var total = 0
        for i in 0..<a.count {
            total += abs(Int(a[i]) - Int(b[i]))
        }
        return Double(total) / Double(a.count)
    }

    static func standardDeviation(_ values: [UInt8]) -> Double {
        let n = Double(values.count)
        var sum = 0.0, sumSquares = 0.0
        for v in values {
            let d = Double(v)
            sum += d
            sumSquares += d * d
        }
        let mean = sum / n
        return max(0, sumSquares / n - mean * mean).squareRoot()
    }
}
