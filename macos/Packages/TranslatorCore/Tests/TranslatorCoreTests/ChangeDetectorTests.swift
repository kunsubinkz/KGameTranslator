import XCTest
@testable import TranslatorCore

final class ChangeDetectorTests: XCTestCase {
    private let size = 64
    private var blank: [UInt8] { [UInt8](repeating: 20, count: size) }

    private func text(_ seed: Int) -> [UInt8] {
        (0..<size).map { UInt8(truncatingIfNeeded: (($0 * 37 + seed * 91) % 2 == 0) ? 240 : 30) }
    }

    private func makeDetector() -> ChangeDetector {
        ChangeDetector(config: .init(changeThreshold: 5, stableMs: 350, hideDelayMs: 800, blankStdDev: 4))
    }

    func testEmitsOnceWhenTextBecomesStable() {
        var d = makeDetector()
        XCTAssertEqual(d.process(samples: text(1), nowMs: 0), .none)
        XCTAssertEqual(d.process(samples: text(1), nowMs: 250), .none)
        XCTAssertEqual(d.process(samples: text(1), nowMs: 500), .textStable)
        XCTAssertEqual(d.process(samples: text(1), nowMs: 750), .none)
        XCTAssertEqual(d.process(samples: text(1), nowMs: 1000), .none)
    }

    func testChangingTextDelaysEvent() {
        var d = makeDetector()
        _ = d.process(samples: text(1), nowMs: 0)
        _ = d.process(samples: text(2), nowMs: 250)
        XCTAssertEqual(d.process(samples: text(2), nowMs: 500), .none)
        XCTAssertEqual(d.process(samples: text(2), nowMs: 750), .textStable)
    }

    func testNewSentenceTriggersAnotherEvent() {
        var d = makeDetector()
        _ = d.process(samples: text(1), nowMs: 0)
        XCTAssertEqual(d.process(samples: text(1), nowMs: 400), .textStable)
        _ = d.process(samples: text(2), nowMs: 600)
        XCTAssertEqual(d.process(samples: text(2), nowMs: 1000), .textStable)
    }

    func testClearsAfterHideDelay() {
        var d = makeDetector()
        _ = d.process(samples: text(1), nowMs: 0)
        XCTAssertEqual(d.process(samples: text(1), nowMs: 400), .textStable)
        XCTAssertEqual(d.process(samples: blank, nowMs: 1000), .none)
        XCTAssertEqual(d.process(samples: blank, nowMs: 1500), .none)
        XCTAssertEqual(d.process(samples: blank, nowMs: 1900), .cleared)
        XCTAssertEqual(d.process(samples: blank, nowMs: 2500), .none)
    }

    func testBlankRegionNeverEmitsText() {
        var d = makeDetector()
        for t in stride(from: 0.0, to: 3000, by: 250) {
            XCTAssertEqual(d.process(samples: blank, nowMs: t), .none)
        }
    }
}
