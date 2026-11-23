import Darwin
import Foundation

/// Đo CPU của chính tiến trình (tính theo một nhân, giống Activity Monitor) bằng `getrusage`.
final class CPUMonitor: @unchecked Sendable {
    private var lastCPU: Double = 0
    private var lastWall: Double = 0

    private static func cpuSeconds() -> Double {
        var usage = rusage()
        getrusage(RUSAGE_SELF, &usage)
        func seconds(_ t: timeval) -> Double { Double(t.tv_sec) + Double(t.tv_usec) / 1_000_000 }
        return seconds(usage.ru_utime) + seconds(usage.ru_stime)
    }

    /// Phần trăm CPU từ lần gọi trước.
    func sample() -> Double {
        let cpu = Self.cpuSeconds()
        let wall = ProcessInfo.processInfo.systemUptime
        defer { lastCPU = cpu; lastWall = wall }
        guard lastWall > 0, wall > lastWall else { return 0 }
        return max(0, (cpu - lastCPU) / (wall - lastWall) * 100)
    }
}
