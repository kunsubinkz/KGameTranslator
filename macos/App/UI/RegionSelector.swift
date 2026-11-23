import AppKit
import SwiftUI
import TranslatorCore

/// Khoanh vùng hộp thoại trên ảnh tĩnh: kéo để vẽ mới, kéo bên trong (hoặc giữ Space) để di chuyển,
/// kéo tám chấm tròn để đổi kích thước. Return để lưu, Esc để hủy.
struct RegionSelector: View {
    @Bindable var state: AppState
    let videoRect: CGRect

    private enum Handle: CaseIterable {
        case topLeft, top, topRight, left, right, bottomLeft, bottom, bottomRight

        var affectsLeft: Bool { [.topLeft, .left, .bottomLeft].contains(self) }
        var affectsRight: Bool { [.topRight, .right, .bottomRight].contains(self) }
        var affectsTop: Bool { [.topLeft, .top, .topRight].contains(self) }
        var affectsBottom: Bool { [.bottomLeft, .bottom, .bottomRight].contains(self) }

        func position(in rect: CGRect) -> CGPoint {
            CGPoint(x: affectsLeft ? rect.minX : affectsRight ? rect.maxX : rect.midX,
                    y: affectsTop ? rect.minY : affectsBottom ? rect.maxY : rect.midY)
        }
    }

    private enum DragMode {
        case create(start: CGPoint)
        case move(origin: CGRect)
        case resize(Handle, origin: CGRect)
    }

    @State private var regions: [Region] = [Profile.default.region]
    @State private var active = 0
    @State private var dragMode: DragMode?
    @State private var spaceHeld = false
    @State private var keyMonitor: Any?
    @State private var ocrResult: AppState.OCRTestResult?
    @State private var ocrFailed = false
    @State private var isTesting = false

    private let minimumSize: CGFloat = 12

    /// Vùng đang chỉnh (vùng phụ khác được chọn bằng cách bấm vào nó).
    private var region: Region {
        get { regions[min(active, regions.count - 1)] }
        nonmutating set { regions[min(active, regions.count - 1)] = newValue }
    }

    private func rect(of region: Region) -> CGRect {
        let r = region.clamped()
        return CGRect(x: videoRect.minX + r.x * videoRect.width, y: videoRect.minY + r.y * videoRect.height,
                      width: r.width * videoRect.width, height: r.height * videoRect.height)
    }

    private var selection: CGRect { rect(of: region) }

    var body: some View {
        ZStack(alignment: .topLeading) {
            snapshotLayer
            dimLayer
            Color.clear.contentShape(Rectangle()).gesture(dragGesture)
            selectionLayer
            overlays
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear {
            regions = state.profile.allRegions
            active = 0
            installKeyMonitor()
        }
        .onDisappear { removeKeyMonitor() }
    }

    // MARK: - Lớp vẽ

    @ViewBuilder
    private var snapshotLayer: some View {
        Color.black
        if let image = state.snapshot {
            Image(decorative: image, scale: 1)
                .resizable()
                .frame(width: videoRect.width, height: videoRect.height)
                .position(x: videoRect.midX, y: videoRect.midY)
        }
    }

    private var dimLayer: some View {
        Canvas { context, size in
            var path = Path()
            path.addRect(CGRect(origin: .zero, size: size))
            for r in regions { path.addRect(rect(of: r)) }
            context.fill(path, with: .color(.black.opacity(0.6)), style: FillStyle(eoFill: true))
        }
        .allowsHitTesting(false)
    }

    private var selectionLayer: some View {
        let rect = selection
        return ZStack(alignment: .topLeading) {
            ForEach(Array(regions.enumerated()), id: \.offset) { index, other in
                if index != active {
                    let r = self.rect(of: other)
                    Rectangle().strokeBorder(Theme.darkBlue.opacity(0.7), style: StrokeStyle(lineWidth: 1.5, dash: [6, 4]))
                        .frame(width: r.width, height: r.height)
                        .position(x: r.midX, y: r.midY)
                    Text("Vùng \(index + 1)")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 8).padding(.vertical, 2)
                        .background(Theme.darkBlue.opacity(0.7), in: RoundedRectangle(cornerRadius: 5))
                        .fixedSize()
                        .offset(x: r.minX, y: r.minY > 24 ? r.minY - 22 : r.maxY + 4)
                }
            }
            Rectangle().strokeBorder(Theme.darkBlue, lineWidth: 2)
                .frame(width: rect.width, height: rect.height)
                .position(x: rect.midX, y: rect.midY)
            ForEach(Handle.allCases, id: \.self) { handle in
                let point = handle.position(in: rect)
                Circle().fill(.white)
                    .overlay(Circle().strokeBorder(Theme.darkBlue, lineWidth: 2))
                    .frame(width: 12, height: 12)
                    .position(x: point.x, y: point.y)
            }
            Text((regions.count > 1 ? "Vùng \(active + 1)/\(regions.count) · " : "") + "x \(viNumber(region.x)) · y \(viNumber(region.y)) · rộng \(viNumber(region.width)) · cao \(viNumber(region.height))")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.white)
                .padding(.horizontal, 10).padding(.vertical, 4)
                .background(Theme.darkBlue, in: RoundedRectangle(cornerRadius: 6))
                .fixedSize()
                .offset(x: rect.minX, y: rect.minY > 40 ? rect.minY - 30 : rect.maxY + 8)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .allowsHitTesting(false)
    }

    private var overlays: some View {
        ZStack(alignment: .top) {
            VStack(spacing: 10) {
                hint
                HStack(spacing: 10) {
                    Button { state.isSelectingRegion = false } label: {
                        Text("Hủy ") + Text("Esc").foregroundStyle(Theme.darkSecondary)
                    }
                    .buttonStyle(ActionButtonStyle(prominent: false))
                    .keyboardShortcut(.cancelAction)

                    Button { addRegion() } label: { Text("Thêm vùng") }
                        .buttonStyle(ActionButtonStyle(prominent: false))
                    if regions.count > 1 {
                        Button { removeActiveRegion() } label: { Text("Xóa vùng \(active + 1)") }
                            .buttonStyle(ActionButtonStyle(prominent: false))
                    }

                    Button { runOCRTest() } label: {
                        HStack(spacing: 6) {
                            if isTesting { ProgressView().controlSize(.small) }
                            Text(regions.count > 1 ? "Thử OCR các vùng" : "Thử OCR vùng này")
                        }
                    }
                    .buttonStyle(ActionButtonStyle(prominent: false))
                    .disabled(state.snapshot == nil || isTesting)

                    Button { state.commitRegions(regions) } label: {
                        Text("Lưu vào hồ sơ ") + Text("Return").foregroundStyle(.white.opacity(0.8))
                    }
                    .buttonStyle(ActionButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
                }
            }
            .padding(.top, 36)
            .frame(maxWidth: .infinity)

            if ocrResult != nil || ocrFailed {
                HStack {
                    Spacer()
                    ocrCard
                }
                .padding(.top, 120)
                .padding(.trailing, 24)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    private var hint: some View {
        HStack(spacing: 18) {
            Text("Khoanh đúng vùng chữ thoại, ") + Text("tránh").bold() + Text(" biểu tượng nút bấm. Cần thêm ô tên/ô khác: Thêm vùng")
            Rectangle().fill(Theme.darkBorder).frame(width: 1, height: 18)
            HStack(spacing: 4) {
                Text("Giữ")
                Text("Space")
                    .padding(.horizontal, 6).padding(.vertical, 1)
                    .background(Theme.darkPill, in: RoundedRectangle(cornerRadius: 4))
                    .foregroundStyle(Theme.darkText)
                Text("để di chuyển")
            }
            .foregroundStyle(Theme.darkSecondary)
        }
        .font(.system(size: 13))
        .foregroundStyle(Theme.darkText)
        .padding(.horizontal, 18).padding(.vertical, 12)
        .background(Theme.darkBar.opacity(0.94), in: RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(Theme.darkBorder))
    }

    private var ocrCard: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let result = ocrResult {
                Text("Kết quả thử OCR · \(Int(result.ocrMs)) ms · tin cậy \(Int((result.confidence * 100).rounded()))%")
                    .font(.system(size: 12)).foregroundStyle(Theme.darkSecondary)
                Text(result.text.isEmpty ? "(không đọc được chữ)" : result.text)
                    .font(.system(size: 14)).lineSpacing(2)
            } else {
                Text("Không thử được OCR").font(.system(size: 12)).foregroundStyle(Theme.darkSecondary)
                Text("Chưa có ảnh từ capture card.").font(.system(size: 14))
            }
        }
        .frame(width: 300, alignment: .leading)
        .padding(.horizontal, 16).padding(.vertical, 14)
        .background(Theme.darkBar.opacity(0.94), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Theme.darkBorder))
    }

    // MARK: - Kéo chuột

    private var dragGesture: some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                if dragMode == nil { dragMode = beginDrag(at: value.startLocation) }
                guard let mode = dragMode else { return }
                let dx = value.location.x - value.startLocation.x
                let dy = value.location.y - value.startLocation.y

                switch mode {
                case .create(let start):
                    if spaceHeld {
                        dragMode = .move(origin: selection)
                        return
                    }
                    apply(CGRect(x: min(start.x, value.location.x), y: min(start.y, value.location.y),
                                 width: abs(start.x - value.location.x), height: abs(start.y - value.location.y)))
                case .move(let origin):
                    apply(origin.offsetBy(dx: dx, dy: dy), keepingSize: true)
                case .resize(let handle, let origin):
                    var rect = origin
                    if handle.affectsLeft { rect.origin.x += dx; rect.size.width -= dx }
                    if handle.affectsRight { rect.size.width += dx }
                    if handle.affectsTop { rect.origin.y += dy; rect.size.height -= dy }
                    if handle.affectsBottom { rect.size.height += dy }
                    apply(rect.standardized)
                }
            }
            .onEnded { _ in
                dragMode = nil
                ocrResult = nil
                ocrFailed = false
            }
    }

    private func beginDrag(at point: CGPoint) -> DragMode {
        let rect = selection
        for handle in Handle.allCases {
            let p = handle.position(in: rect)
            if abs(p.x - point.x) <= 10, abs(p.y - point.y) <= 10 { return .resize(handle, origin: rect) }
        }
        if rect.contains(point) || spaceHeld { return .move(origin: rect) }
        if let index = regions.indices.last(where: { $0 != active && self.rect(of: regions[$0]).contains(point) }) {
            active = index
            return .move(origin: selection)
        }
        return .create(start: clamp(point))
    }

    /// Ghi vùng mới (tọa độ view) vào `region` dạng tương đối, giữ trong khung video.
    private func apply(_ rect: CGRect, keepingSize: Bool = false) {
        var r = rect
        if keepingSize {
            r.origin.x = min(max(r.minX, videoRect.minX), videoRect.maxX - r.width)
            r.origin.y = min(max(r.minY, videoRect.minY), videoRect.maxY - r.height)
        } else {
            let minX = max(r.minX, videoRect.minX), minY = max(r.minY, videoRect.minY)
            let maxX = min(r.maxX, videoRect.maxX), maxY = min(r.maxY, videoRect.maxY)
            r = CGRect(x: minX, y: minY, width: max(maxX - minX, minimumSize), height: max(maxY - minY, minimumSize))
        }
        region = Region(x: (r.minX - videoRect.minX) / videoRect.width, y: (r.minY - videoRect.minY) / videoRect.height,
                        width: r.width / videoRect.width, height: r.height / videoRect.height).clamped()
    }

    private func addRegion() {
        // Vùng mới đặt cạnh vùng hiện tại, người dùng kéo/chỉnh tiếp.
        let base = region
        var new = Region(x: base.x, y: min(base.y + base.height + 0.02, 0.9), width: base.width, height: min(base.height, 0.1))
        new = new.clamped()
        regions.append(new)
        active = regions.count - 1
        ocrResult = nil
        ocrFailed = false
    }

    private func removeActiveRegion() {
        guard regions.count > 1 else { return }
        regions.remove(at: active)
        active = min(active, regions.count - 1)
        ocrResult = nil
        ocrFailed = false
    }

    private func clamp(_ p: CGPoint) -> CGPoint {
        CGPoint(x: min(max(p.x, videoRect.minX), videoRect.maxX), y: min(max(p.y, videoRect.minY), videoRect.maxY))
    }

    // MARK: - Phím Space và thử OCR

    private func installKeyMonitor() {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp]) { event in
            guard event.keyCode == 49 else { return event }  // Space
            spaceHeld = event.type == .keyDown
            return nil
        }
    }

    private func removeKeyMonitor() {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
    }

    private func runOCRTest() {
        isTesting = true
        ocrFailed = false
        Task {
            let result = await state.testOCR(regions: regions)
            ocrResult = result
            ocrFailed = result == nil
            isTesting = false
        }
    }
}

private struct ActionButtonStyle: ButtonStyle {
    let prominent: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: prominent ? .semibold : .regular))
            .foregroundStyle(prominent ? Color.white : Theme.darkText)
            .padding(.horizontal, prominent ? 18 : 16)
            .frame(height: 34)
            .background(prominent ? Theme.darkBlue : Theme.darkPill, in: RoundedRectangle(cornerRadius: 8))
            .opacity(configuration.isPressed ? 0.8 : 1)
    }
}
