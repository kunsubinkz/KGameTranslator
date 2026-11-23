import AppKit
import CoreImage
import Foundation
import Metal
import TranslatorCore

/// `GameTranslator --selftest`: kiểm tra nhanh các thành phần không cần capture card hay quyền hệ thống.
enum SelfTest {
    /// Vẽ ảnh nửa trên đỏ, nửa dưới xanh vào texture Metal và đọc lại: trên phải là đỏ, dưới phải là xanh.
    static func metalOrientationOK() -> Bool {
        guard let device = MTLCreateSystemDefaultDevice(), let queue = device.makeCommandQueue() else { return false }
        let top = CIImage(color: CIColor(red: 1, green: 0, blue: 0)).cropped(to: CGRect(x: 0, y: 18, width: 32, height: 18))
        let bottom = CIImage(color: CIColor(red: 0, green: 0, blue: 1)).cropped(to: CGRect(x: 0, y: 0, width: 32, height: 18))
        let source = top.composited(over: bottom)  // gốc dưới trái: y lớn là phía trên
        let size = CGSize(width: 64, height: 36)
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: 64, height: 36, mipmapped: false)
        descriptor.usage = [.shaderRead, .shaderWrite]
        descriptor.storageMode = .shared
        guard let texture = device.makeTexture(descriptor: descriptor) else { return false }
        for mode in VideoSmoothing.allCases {
            guard let commandBuffer = queue.makeCommandBuffer() else { return false }
            let image = MetalVideoRenderer.compose(source, into: size, smoothing: mode)
            CIContext(mtlCommandQueue: queue).render(image, to: texture, commandBuffer: commandBuffer,
                                                      bounds: CGRect(origin: .zero, size: size), colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!)
            commandBuffer.commit()
            commandBuffer.waitUntilCompleted()
            var pixels = [UInt8](repeating: 0, count: 64 * 36 * 4)
            texture.getBytes(&pixels, bytesPerRow: 64 * 4, from: MTLRegionMake2D(0, 0, 64, 36), mipmapLevel: 0)
            func px(_ x: Int, _ y: Int) -> (b: UInt8, g: UInt8, r: UInt8) { let i = (y * 64 + x) * 4; return (pixels[i], pixels[i + 1], pixels[i + 2]) }
            let t = px(32, 4), b = px(32, 31)
            guard t.r > 200, t.b < 60, b.b > 200, b.r < 60 else { print("  hướng ảnh sai ở chế độ \(mode): trên=\(t) dưới=\(b)"); return false }
        }
        return true
    }

    static func run() -> Int32 {
        var failures = 0
        func check(_ name: String, _ ok: Bool, _ detail: String = "") {
            print("\(ok ? "✓" : "✗") \(name)\(detail.isEmpty ? "" : " — \(detail)")")
            if !ok { failures += 1 }
        }

        check("Chuẩn hóa chuỗi", TextNormalizer.normalize("| think\nso ▼") == "I think so")

        if let cache = try? TranslationCache(path: ":memory:") {
            cache.store(profileId: "t", source: "Hello", target: "Xin chào", engine: "apple")
            check("Cache SQLite", cache.lookup(profileId: "t", source: "hello")?.target == "Xin chào")
        } else {
            check("Cache SQLite", false, "không mở được")
        }

        let region = Region(x: 0.1, y: 0.7, width: 0.5, height: 0.2)
        let rect = PipelineCoordinator.cropRect(for: region, in: CGRect(x: 0, y: 0, width: 1000, height: 500))
        check("Vùng cắt", rect == CGRect(x: 100, y: 50, width: 500, height: 100), "\(rect)")

        check("Bộ vẽ Metal (hướng ảnh, phóng 2x)", metalOrientationOK())

        let expected = "Welcome to Hyrule, brave traveler."
        guard let image = renderText(expected) else {
            check("Render ảnh thử", false)
            return 1
        }
        let preprocessor = Preprocessor()
        let ciImage = CIImage(cgImage: image)
        check("Thumbnail", (preprocessor.thumbnail(ciImage)?.count ?? 0) > 0)

        var settings = OCRSettings()
        settings.minimumTextHeight = 0
        if let prepared = preprocessor.prepare(ciImage, settings: settings),
           let result = try? VisionOCR().recognize(prepared, settings: settings, customWords: ["Hyrule"]) {
            let cer = TextNormalizer.characterErrorRate(
                expected: expected.lowercased(),
                actual: (TextNormalizer.normalize(result.text) ?? "").lowercased())
            check("Vision OCR", cer < 0.15, "\"\(result.text)\" CER \(String(format: "%.2f", cer)), \(Int(result.elapsedMs)) ms")
        } else {
            check("Vision OCR", false, "không chạy được")
        }

        print(failures == 0 ? "Selftest OK" : "Selftest: \(failures) lỗi")
        return failures == 0 ? 0 : 1
    }

    static func renderText(_ text: String) -> CGImage? {
        let width = 900, height = 140
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height, bitsPerSample: 8, samplesPerPixel: 4,
            hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
            let context = NSGraphicsContext(bitmapImageRep: rep) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        NSColor.black.setFill()
        NSRect(x: 0, y: 0, width: width, height: height).fill()
        NSAttributedString(string: text, attributes: [
            .font: NSFont.systemFont(ofSize: 48, weight: .medium), .foregroundColor: NSColor.white,
        ]).draw(at: NSPoint(x: 24, y: 40))
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage
    }
}
