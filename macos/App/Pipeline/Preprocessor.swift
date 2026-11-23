import CoreImage
import CoreImage.CIFilterBuiltins
import Foundation
import TranslatorCore

/// Tiền xử lý bằng Core Image (GPU): phóng to, đảo màu, tăng tương phản.
struct Preprocessor {
    let context: CIContext

    init(context: CIContext = CIContext()) {
        self.context = context
    }

    func prepare(_ image: CIImage, settings: OCRSettings) -> CGImage? {
        let extent = image.extent
        var result = image.transformed(by: CGAffineTransform(translationX: -extent.minX, y: -extent.minY))

        if settings.scale > 1.01 {
            let scale = CIFilter.lanczosScaleTransform()
            scale.inputImage = result
            scale.scale = Float(settings.scale)
            scale.aspectRatio = 1
            result = scale.outputImage ?? result
        }
        if settings.invert {
            let invert = CIFilter.colorInvert()
            invert.inputImage = result
            result = invert.outputImage ?? result
        }
        if abs(settings.contrast - 1) > 0.01 {
            let controls = CIFilter.colorControls()
            controls.inputImage = result
            controls.contrast = Float(settings.contrast)
            result = controls.outputImage ?? result
        }
        return context.createCGImage(result, from: result.extent)
    }

    /// Ảnh xám thu nhỏ (~160 px rộng) của vùng, dùng cho ChangeDetector.
    func thumbnail(_ image: CIImage, width: Int = 160) -> [UInt8]? {
        let extent = image.extent
        guard extent.width > 0, extent.height > 0 else { return nil }
        let scale = CGFloat(width) / extent.width
        let height = max(Int((extent.height * scale).rounded()), 1)
        let scaled = image.transformed(
            by: CGAffineTransform(scaleX: scale, y: CGFloat(height) / extent.height)
                .translatedBy(x: -extent.minX, y: -extent.minY))
        var pixels = [UInt8](repeating: 0, count: width * height)
        context.render(scaled, toBitmap: &pixels, rowBytes: width,
                       bounds: CGRect(x: 0, y: 0, width: width, height: height),
                       format: .L8, colorSpace: CGColorSpaceCreateDeviceGray())
        return pixels
    }
}
