import AppKit
import CoreImage
import CoreImage.CIFilterBuiltins
import Metal
import QuartzCore
import SwiftUI

/// Cách phóng hình capture lên cửa sổ/màn hình.
enum VideoSmoothing: String, CaseIterable {
    /// `AVCaptureVideoPreviewLayer` của hệ thống (bilinear).
    case system
    /// Lanczos + làm nét nhẹ: cạnh mịn, ít nhòe khi phóng 1080p lên 4K.
    case smooth
    /// Nearest neighbor: nét, mỗi điểm ảnh thành khối vuông (1080p lên 4K là đúng 2x2).
    case pixel

    var title: String {
        switch self {
        case .system: return "Mặc định (hệ thống)"
        case .smooth: return "Mịn 4K (Lanczos + làm nét)"
        case .pixel: return "Điểm ảnh (nét, vuông)"
        }
    }
}

/// Vẽ từng khung capture bằng GPU (Core Image + Metal) ngay khi khung đến, không qua hàng đợi nào khác.
final class MetalVideoRenderer: @unchecked Sendable {
    let layer = CAMetalLayer()
    private let queue: MTLCommandQueue?
    private let ciContext: CIContext
    private let lock = NSLock()
    private var smoothing: VideoSmoothing = .smooth

    init() {
        let device = MTLCreateSystemDefaultDevice()
        queue = device?.makeCommandQueue()
        ciContext = queue.map { CIContext(mtlCommandQueue: $0) } ?? CIContext()
        layer.device = device
        layer.pixelFormat = .bgra8Unorm
        layer.framebufferOnly = false
        layer.maximumDrawableCount = 2
        layer.backgroundColor = NSColor.black.cgColor
    }

    func setSmoothing(_ value: VideoSmoothing) {
        lock.lock(); smoothing = value; lock.unlock()
    }

    func render(_ buffer: CVPixelBuffer) {
        guard let queue, layer.drawableSize.width > 1, let drawable = layer.nextDrawable(),
              let commandBuffer = queue.makeCommandBuffer() else { return }
        lock.lock(); let mode = smoothing; lock.unlock()
        let size = CGSize(width: drawable.texture.width, height: drawable.texture.height)
        let image = Self.compose(CIImage(cvPixelBuffer: buffer), into: size, smoothing: mode)
        ciContext.render(image, to: drawable.texture, commandBuffer: commandBuffer,
                         bounds: CGRect(origin: .zero, size: size), colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!)
        commandBuffer.present(drawable)
        commandBuffer.commit()
    }

    /// Phóng ảnh nguồn vừa khít `size` (giữ tỉ lệ, viền đen) rồi lật dọc để khớp gốc tọa độ của texture Metal.
    static func compose(_ source: CIImage, into size: CGSize, smoothing: VideoSmoothing) -> CIImage {
        let src = source.extent
        guard src.width > 0, src.height > 0 else { return CIImage(color: .black).cropped(to: CGRect(origin: .zero, size: size)) }
        let scale = min(size.width / src.width, size.height / src.height)
        var image = source.transformed(by: CGAffineTransform(translationX: -src.minX, y: -src.minY))

        switch smoothing {
        case .smooth where scale > 1.001:
            let lanczos = CIFilter.lanczosScaleTransform()
            lanczos.inputImage = image
            lanczos.scale = Float(scale)
            lanczos.aspectRatio = 1
            image = lanczos.outputImage ?? image.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
            let sharpen = CIFilter.sharpenLuminance()
            sharpen.inputImage = image
            sharpen.sharpness = 0.35
            sharpen.radius = 1.0
            image = sharpen.outputImage ?? image
        case .pixel:
            image = image.samplingNearest().transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        default:
            image = image.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        }

        let width = src.width * scale, height = src.height * scale
        image = image.transformed(by: CGAffineTransform(translationX: (size.width - width) / 2, y: (size.height - height) / 2))
        let bounds = CGRect(origin: .zero, size: size)
        let black = CIImage(color: .black).cropped(to: bounds)
        let flipped = image.composited(over: black)
            .transformed(by: CGAffineTransform(scaleX: 1, y: -1).translatedBy(x: 0, y: -size.height))
        return flipped.cropped(to: bounds)
    }
}

struct MetalVideoView: NSViewRepresentable {
    let renderer: MetalVideoRenderer

    func makeNSView(context: Context) -> MetalHostView { MetalHostView(renderer: renderer) }
    func updateNSView(_ nsView: MetalHostView, context: Context) {}

    final class MetalHostView: NSView {
        let renderer: MetalVideoRenderer

        init(renderer: MetalVideoRenderer) {
            self.renderer = renderer
            super.init(frame: .zero)
            wantsLayer = true
        }

        @available(*, unavailable)
        required init?(coder: NSCoder) { fatalError() }

        override func makeBackingLayer() -> CALayer { renderer.layer }

        override func viewDidChangeBackingProperties() {
            super.viewDidChangeBackingProperties()
            updateDrawableSize()
        }

        override func layout() {
            super.layout()
            updateDrawableSize()
        }

        private func updateDrawableSize() {
            let scale = window?.backingScaleFactor ?? NSScreen.main?.backingScaleFactor ?? 2
            renderer.layer.contentsScale = scale
            renderer.layer.drawableSize = CGSize(width: max(bounds.width * scale, 1), height: max(bounds.height * scale, 1))
        }
    }
}
