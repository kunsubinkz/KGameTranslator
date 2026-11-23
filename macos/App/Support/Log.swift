import Foundation
import os

enum Log {
    private static let subsystem = "vn.gametranslator.app"
    static let capture = Logger(subsystem: subsystem, category: "capture")
    static let pipeline = Logger(subsystem: subsystem, category: "pipeline")
    static let translate = Logger(subsystem: subsystem, category: "translate")
    static let app = Logger(subsystem: subsystem, category: "app")
}
