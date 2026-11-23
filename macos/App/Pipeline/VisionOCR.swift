import CoreGraphics
import Foundation
import TranslatorCore
import Vision

struct OCRResult: Sendable, Equatable {
    let text: String
    let confidence: Float
    let elapsedMs: Double
}

protocol OCREngine: Sendable {
    var name: String { get }
    func recognize(_ image: CGImage, settings: OCRSettings, customWords: [String]) throws -> OCRResult
}

struct VisionOCR: OCREngine {
    let name = "Apple Vision"
    private let minimumLineConfidence: Float = 0.3

    func recognize(_ image: CGImage, settings: OCRSettings, customWords: [String]) throws -> OCRResult {
        let start = DispatchTime.now()
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = settings.level == .accurate ? .accurate : .fast
        request.recognitionLanguages = ["en-US"]
        request.usesLanguageCorrection = true
        request.customWords = customWords
        request.minimumTextHeight = Float(settings.minimumTextHeight)

        try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])

        // Vision có gốc tọa độ ở góc dưới trái: sắp từ trên xuống bằng maxY giảm dần.
        let lines = (request.results ?? [])
            .sorted { $0.boundingBox.maxY > $1.boundingBox.maxY }
            .compactMap { $0.topCandidates(1).first }
            .filter { $0.confidence >= minimumLineConfidence }

        let confidence = lines.isEmpty ? 0 : lines.map(\.confidence).reduce(0, +) / Float(lines.count)
        let elapsed = Double(DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds) / 1_000_000
        return OCRResult(text: lines.map(\.string).joined(separator: "\n"), confidence: confidence, elapsedMs: elapsed)
    }
}
