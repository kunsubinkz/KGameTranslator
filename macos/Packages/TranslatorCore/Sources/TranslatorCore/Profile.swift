import Foundation

/// Vùng hộp thoại theo tọa độ tương đối 0...1, gốc ở góc trên trái.
public struct Region: Codable, Equatable, Sendable {
    public var x: Double
    public var y: Double
    public var width: Double
    public var height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x; self.y = y; self.width = width; self.height = height
    }

    public func clamped() -> Region {
        let nx = min(max(x, 0), 0.99)
        let ny = min(max(y, 0), 0.99)
        let nw = min(max(width, 0.01), 1 - nx)
        let nh = min(max(height, 0.01), 1 - ny)
        return Region(x: nx, y: ny, width: nw, height: nh)
    }
}

public enum OCRLevel: String, Codable, Sendable, CaseIterable {
    case accurate, fast
}

public struct OCRSettings: Codable, Equatable, Sendable {
    public var level: OCRLevel = .accurate
    public var scale: Double = 2.0
    public var invert: Bool = false
    public var contrast: Double = 1.2
    public var minimumTextHeight: Double = 0.03

    public init() {}

    enum CodingKeys: String, CodingKey { case level, scale, invert, contrast, minimumTextHeight }

    public init(from decoder: Decoder) throws {
        self.init()
        let c = try decoder.container(keyedBy: CodingKeys.self)
        level = try c.decodeIfPresent(OCRLevel.self, forKey: .level) ?? level
        scale = try c.decodeIfPresent(Double.self, forKey: .scale) ?? scale
        invert = try c.decodeIfPresent(Bool.self, forKey: .invert) ?? invert
        contrast = try c.decodeIfPresent(Double.self, forKey: .contrast) ?? contrast
        minimumTextHeight = try c.decodeIfPresent(Double.self, forKey: .minimumTextHeight) ?? minimumTextHeight
    }
}

public struct DetectorSettings: Codable, Equatable, Sendable {
    public var sampleEveryNFrames: Int = 15
    public var changeThreshold: Double = 2.0
    public var stableMs: Double = 350
    public var hideDelayMs: Double = 800

    public init() {}

    enum CodingKeys: String, CodingKey { case sampleEveryNFrames, changeThreshold, stableMs, hideDelayMs }

    public init(from decoder: Decoder) throws {
        self.init()
        let c = try decoder.container(keyedBy: CodingKeys.self)
        sampleEveryNFrames = try c.decodeIfPresent(Int.self, forKey: .sampleEveryNFrames) ?? sampleEveryNFrames
        changeThreshold = try c.decodeIfPresent(Double.self, forKey: .changeThreshold) ?? changeThreshold
        stableMs = try c.decodeIfPresent(Double.self, forKey: .stableMs) ?? stableMs
        hideDelayMs = try c.decodeIfPresent(Double.self, forKey: .hideDelayMs) ?? hideDelayMs
    }
}

public enum TranslateProvider: String, Codable, Sendable, CaseIterable {
    case claude, gemini

    public var title: String { self == .claude ? "Claude (Anthropic)" : "Gemini (Google)" }
    public var keyPlaceholder: String { self == .claude ? "Dán API key từ console.anthropic.com" : "Dán API key từ Google AI Studio" }

    /// Model gợi ý cho dropdown; người dùng vẫn nhập tên khác được.
    public var presets: [(id: String, title: String)] {
        switch self {
        case .claude:
            return [("claude-haiku-5-5", "Claude Haiku 5.5 · nhanh, rẻ nhất"),
                    ("claude-sonnet-5-5", "Claude Sonnet 5.5 · cân bằng"),
                    ("claude-opus-5-5", "Claude Opus 5.5 · chất lượng cao nhất")]
        case .gemini:
            return [("gemini-flash-lite-latest", "Gemini Flash-Lite · nhanh, rẻ"),
                    ("gemini-flash-latest", "Gemini Flash · cân bằng"),
                    ("gemini-pro-latest", "Gemini Pro · chất lượng cao")]
        }
    }
}

public enum TranslateMode: String, Codable, Sendable, CaseIterable {
    case onlineOnly = "geminiOnly"
    case offlineOnly
    case onlineWithOfflineFallback = "geminiWithOfflineFallback"

    public var title: String {
        switch self {
        case .onlineOnly: return "Chỉ online (AI)"
        case .offlineOnly: return "Chỉ offline (Apple)"
        case .onlineWithOfflineFallback: return "Online, dự phòng offline"
        }
    }
}

public struct TranslateSettings: Codable, Equatable, Sendable {
    public var mode: TranslateMode = .onlineWithOfflineFallback
    public var provider: TranslateProvider = .claude
    /// Model Claude.
    public var model: String = "claude-haiku-5-5"
    public var geminiModel: String = "gemini-flash-lite-latest"
    public var contextLines: Int = 3
    public var requestsPerMinute: Int = 12
    public var requestsPerDay: Int = 450
    /// Thay bản dịch offline bằng bản Claude khi còn hạn mức.
    public var upgradeOffline: Bool = true

    public init() {}

    /// Model của nhà cung cấp đang chọn.
    public var activeModel: String { provider == .claude ? model : geminiModel }

    enum CodingKeys: String, CodingKey {
        case mode, provider, model, geminiModel, contextLines, requestsPerMinute, requestsPerDay, upgradeOffline
    }

    public init(from decoder: Decoder) throws {
        self.init()
        let c = try decoder.container(keyedBy: CodingKeys.self)
        mode = try c.decodeIfPresent(TranslateMode.self, forKey: .mode) ?? mode
        model = try c.decodeIfPresent(String.self, forKey: .model) ?? model
        provider = try c.decodeIfPresent(TranslateProvider.self, forKey: .provider) ?? provider
        geminiModel = try c.decodeIfPresent(String.self, forKey: .geminiModel) ?? geminiModel
        // Hồ sơ cũ lưu tên model Gemini trong `model`: tách sang `geminiModel`.
        if model.hasPrefix("gemini") { geminiModel = model; model = TranslateSettings().model }
        contextLines = try c.decodeIfPresent(Int.self, forKey: .contextLines) ?? contextLines
        requestsPerMinute = try c.decodeIfPresent(Int.self, forKey: .requestsPerMinute) ?? requestsPerMinute
        requestsPerDay = try c.decodeIfPresent(Int.self, forKey: .requestsPerDay) ?? requestsPerDay
        upgradeOffline = try c.decodeIfPresent(Bool.self, forKey: .upgradeOffline) ?? upgradeOffline
    }
}

public enum SubtitlePosition: String, Codable, Sendable, CaseIterable {
    case above, below, over, free

    public var title: String {
        switch self {
        case .above: return "Trên hộp thoại"
        case .below: return "Dưới hộp thoại"
        case .over: return "Đè lên hộp thoại"
        case .free: return "Tự do (kéo thả)"
        }
    }
}

public enum SubtitleAlignment: String, Codable, Sendable, CaseIterable {
    case left, center, right, justified

    public var title: String {
        switch self {
        case .left: return "Trái"
        case .center: return "Giữa"
        case .right: return "Phải"
        case .justified: return "Đều"
        }
    }
}

public struct SubtitleSettings: Codable, Equatable, Sendable {
    public var position: SubtitlePosition = .above
    public var fontSize: Double = 28
    public var freeX: Double = 0.5
    public var freeY: Double = 0.85
    /// Độ đậm của nền đen sau phụ đề, 0...1.
    public var backgroundOpacity: Double = 0.6
    /// Căn lề chữ phụ đề; "đều" căn cả hai mép (dòng cuối căn trái).
    public var alignment: SubtitleAlignment = .center
    /// Số dòng tối đa trước khi cắt bớt.
    public var maxLines: Int = 12

    public init() {}

    enum CodingKeys: String, CodingKey { case position, fontSize, freeX, freeY, backgroundOpacity, alignment, maxLines }

    public init(from decoder: Decoder) throws {
        self.init()
        let c = try decoder.container(keyedBy: CodingKeys.self)
        position = try c.decodeIfPresent(SubtitlePosition.self, forKey: .position) ?? position
        fontSize = try c.decodeIfPresent(Double.self, forKey: .fontSize) ?? fontSize
        freeX = try c.decodeIfPresent(Double.self, forKey: .freeX) ?? freeX
        freeY = try c.decodeIfPresent(Double.self, forKey: .freeY) ?? freeY
        backgroundOpacity = try c.decodeIfPresent(Double.self, forKey: .backgroundOpacity) ?? backgroundOpacity
        alignment = try c.decodeIfPresent(SubtitleAlignment.self, forKey: .alignment) ?? alignment
        maxLines = min(max(try c.decodeIfPresent(Int.self, forKey: .maxLines) ?? maxLines, 1), 30)
    }
}

public struct Profile: Codable, Equatable, Sendable, Identifiable {
    public var id: String
    public var name: String
    public var region: Region
    /// Vùng phụ (vd. ô tên nhân vật): chữ của mọi vùng được ghép lại rồi dịch chung.
    public var extraRegions: [Region] = []
    public var ocr = OCRSettings()
    public var detector = DetectorSettings()
    public var translate = TranslateSettings()
    public var subtitle = SubtitleSettings()
    public var glossary: [String: String] = [:]

    public init(id: String, name: String, region: Region) {
        self.id = id
        self.name = name
        self.region = region
    }

    enum CodingKeys: String, CodingKey { case id, name, region, extraRegions, ocr, detector, translate, subtitle, glossary }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? id
        region = try c.decodeIfPresent(Region.self, forKey: .region) ?? Profile.default.region
        extraRegions = try c.decodeIfPresent([Region].self, forKey: .extraRegions) ?? []
        ocr = try c.decodeIfPresent(OCRSettings.self, forKey: .ocr) ?? OCRSettings()
        detector = try c.decodeIfPresent(DetectorSettings.self, forKey: .detector) ?? DetectorSettings()
        translate = try c.decodeIfPresent(TranslateSettings.self, forKey: .translate) ?? TranslateSettings()
        subtitle = try c.decodeIfPresent(SubtitleSettings.self, forKey: .subtitle) ?? SubtitleSettings()
        glossary = try c.decodeIfPresent([String: String].self, forKey: .glossary) ?? [:]
    }

    /// Vùng chính rồi tới các vùng phụ.
    public var allRegions: [Region] { [region] + extraRegions }

    /// Hình chữ nhật bao quanh mọi vùng, dùng để đặt phụ đề.
    public var unionRegion: Region {
        let all = allRegions.map { $0.clamped() }
        let minX = all.map(\.x).min() ?? region.x, minY = all.map(\.y).min() ?? region.y
        let maxX = all.map { $0.x + $0.width }.max() ?? region.x, maxY = all.map { $0.y + $0.height }.max() ?? region.y
        return Region(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
    }

    public static var `default`: Profile {
        Profile(id: "default", name: "Mặc định", region: Region(x: 0.1, y: 0.72, width: 0.8, height: 0.22))
    }

    public static var zeldaTOTK: Profile {
        var p = Profile(id: "zelda-totk", name: "Zelda: Tears of the Kingdom",
                        region: Region(x: 0.18, y: 0.74, width: 0.64, height: 0.20))
        p.glossary = ["Link": "Link", "Hyrule": "Hyrule", "Rupee": "Rupee", "Zelda": "Zelda"]
        return p
    }
}
