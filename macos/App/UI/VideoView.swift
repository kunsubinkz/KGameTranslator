import AppKit
import AVFoundation
import SwiftUI

/// Khung video: `AVCaptureVideoPreviewLayer` vẽ bằng GPU, giữ tỉ lệ, nền đen.
struct VideoView: NSViewRepresentable {
    let session: AVCaptureSession

    func makeNSView(context: Context) -> PreviewView {
        let view = PreviewView()
        view.previewLayer.session = session
        return view
    }

    func updateNSView(_ nsView: PreviewView, context: Context) {}

    final class PreviewView: NSView {
        let previewLayer = AVCaptureVideoPreviewLayer()

        override init(frame frameRect: NSRect) {
            super.init(frame: frameRect)
            wantsLayer = true
            layer = CALayer()
            layer?.backgroundColor = NSColor.black.cgColor
            previewLayer.videoGravity = .resizeAspect
            previewLayer.backgroundColor = NSColor.black.cgColor
            layer?.addSublayer(previewLayer)
        }

        @available(*, unavailable)
        required init?(coder: NSCoder) { fatalError() }

        override func layout() {
            super.layout()
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            previewLayer.frame = bounds
            CATransaction.commit()
        }
    }
}

extension CGSize {
    /// Hình chữ nhật video hiển thị thực (aspect fit) trong khung `container`.
    func aspectFit(in container: CGSize) -> CGRect {
        guard width > 0, height > 0, container.width > 0, container.height > 0 else {
            return CGRect(origin: .zero, size: container)
        }
        let scale = min(container.width / width, container.height / height)
        let size = CGSize(width: width * scale, height: height * scale)
        return CGRect(x: (container.width - size.width) / 2, y: (container.height - size.height) / 2,
                      width: size.width, height: size.height)
    }
}
