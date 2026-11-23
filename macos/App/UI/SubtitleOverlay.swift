import SwiftUI
import TranslatorCore

/// Phụ đề tiếng Việt đè lên hình; vị trí tính theo khung video thực để không lệch khi có viền đen.
struct SubtitleOverlay: View {
    @Bindable var state: AppState
    let videoRect: CGRect

    private var settings: SubtitleSettings { state.profile.subtitle }
    private var regionRect: CGRect {
        let r = state.profile.unionRegion.clamped()
        return CGRect(x: videoRect.minX + r.x * videoRect.width, y: videoRect.minY + r.y * videoRect.height,
                      width: r.width * videoRect.width, height: r.height * videoRect.height)
    }

    var body: some View {
        ZStack {
            if let subtitle = state.subtitle {
                placed(SubtitleBubble(subtitle: subtitle, showOriginal: state.showOriginal,
                                      fontSize: settings.fontSize * videoRect.height / 1080,
                                      backgroundOpacity: settings.backgroundOpacity,
                                      alignment: settings.alignment,
                                      maxLines: settings.maxLines,
                                      fillWidth: settings.position != .free,
                                      maxWidth: settings.position == .free ? videoRect.width * 0.9 : max(regionRect.width, 200))
                    .id(subtitle.source + (subtitle.target ?? "")))
                    .transition(.opacity)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .animation(.easeInOut(duration: 0.15), value: state.subtitle)
        .allowsHitTesting(settings.position == .free)
    }

    @ViewBuilder
    private func placed(_ bubble: some View) -> some View {
        let region = regionRect
        let margin: CGFloat = 8
        switch settings.position {
        case .above:
            bubble.frame(width: region.width, height: max(region.minY - videoRect.minY - margin, 0), alignment: .bottom)
                .position(x: region.midX, y: videoRect.minY + max(region.minY - videoRect.minY - margin, 0) / 2)
        case .below:
            let height = max(videoRect.maxY - region.maxY - margin, 0)
            bubble.frame(width: region.width, height: height, alignment: .top)
                .position(x: region.midX, y: region.maxY + margin + height / 2)
        case .over:
            bubble.frame(width: region.width, height: region.height, alignment: .center)
                .position(x: region.midX, y: region.midY)
        case .free:
            bubble
                .position(x: videoRect.minX + settings.freeX * videoRect.width,
                          y: videoRect.minY + settings.freeY * videoRect.height)
                .gesture(DragGesture(coordinateSpace: .local).onChanged { value in
                    state.profile.subtitle.freeX = min(max((value.location.x - videoRect.minX) / videoRect.width, 0), 1)
                    state.profile.subtitle.freeY = min(max((value.location.y - videoRect.minY) / videoRect.height, 0), 1)
                })
        }
    }
}

private struct SubtitleBubble: View {
    let subtitle: SubtitleState
    let showOriginal: Bool
    let fontSize: CGFloat
    let backgroundOpacity: Double
    let alignment: SubtitleAlignment
    let maxLines: Int
    /// Giãn bằng bề rộng tối đa (vùng OCR) để căn trái/phải/đều có tác dụng.
    let fillWidth: Bool
    let maxWidth: CGFloat

    private var textAlignment: TextAlignment {
        switch alignment {
        case .left, .justified: return .leading
        case .center: return .center
        case .right: return .trailing
        }
    }

    private var stackAlignment: HorizontalAlignment {
        switch alignment {
        case .left, .justified: return .leading
        case .center: return .center
        case .right: return .trailing
        }
    }

    var body: some View {
        VStack(alignment: stackAlignment, spacing: 4) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                if let target = subtitle.target {
                    if alignment == .justified {
                        JustifiedText(text: target, fontSize: max(fontSize, 10), maxLines: maxLines)
                    } else {
                        Text(target)
                    }
                } else {
                    Text(subtitle.source).opacity(0.7)
                    ProgressView().controlSize(.small)
                }
                statusIcon
            }
            .font(.system(size: max(fontSize, 10), weight: .semibold))
            .foregroundStyle(.white)
            .shadow(color: .black, radius: 1, x: 1, y: 1)
            .shadow(color: .black, radius: 1, x: -1, y: -1)
            .shadow(color: .black, radius: 3)

            if showOriginal, subtitle.target != nil {
                Text(subtitle.source)
                    .font(.system(size: max(fontSize * 0.54, 8), weight: .medium))
                    .foregroundStyle(Theme.darkText)
                    .shadow(color: .black, radius: 1, x: 1, y: 1)
                    .shadow(color: .black, radius: 3)
            }
        }
        .multilineTextAlignment(textAlignment)
        .lineLimit(showOriginal ? maxLines + 2 : maxLines)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .frame(maxWidth: fillWidth && alignment != .center ? .infinity : nil, alignment: .leading)
        .background(Color.black.opacity(backgroundOpacity), in: RoundedRectangle(cornerRadius: 10))
        .frame(maxWidth: maxWidth)
        .fixedSize(horizontal: false, vertical: true)
    }

    @ViewBuilder
    private var statusIcon: some View {
        let symbol: String? = {
            guard subtitle.target != nil, let engine = subtitle.engine else { return nil }
            if subtitle.cached { return "bolt.fill" }
            if engine == "apple" { return "wifi.slash" }
            if engine == "original" { return "exclamationmark.triangle.fill" }
            return nil
        }()
        if let symbol {
            Image(systemName: symbol).font(.system(size: max(fontSize * 0.5, 8))).foregroundStyle(.yellow.opacity(0.9))
        }
    }
}

/// Chữ căn đều hai mép: SwiftUI `Text` không hỗ trợ nên dùng `NSAttributedString`.
private struct JustifiedText: NSViewRepresentable {
    let text: String
    let fontSize: CGFloat
    let maxLines: Int

    private var attributed: NSAttributedString {
        let style = NSMutableParagraphStyle()
        style.alignment = .justified
        style.lineBreakMode = .byWordWrapping
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black
        shadow.shadowBlurRadius = 3
        shadow.shadowOffset = .zero
        return NSAttributedString(string: text, attributes: [
            .font: NSFont.systemFont(ofSize: fontSize, weight: .semibold),
            .foregroundColor: NSColor.white,
            .paragraphStyle: style,
            .shadow: shadow,
        ])
    }

    func makeNSView(context: Context) -> NSTextField {
        let field = NSTextField(wrappingLabelWithString: "")
        field.drawsBackground = false
        field.isBezeled = false
        field.isSelectable = false
        field.lineBreakMode = .byWordWrapping
        return field
    }

    func updateNSView(_ field: NSTextField, context: Context) {
        field.attributedStringValue = attributed
        field.maximumNumberOfLines = maxLines
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: NSTextField, context: Context) -> CGSize? {
        let width = proposal.width ?? 600
        let rect = attributed.boundingRect(with: NSSize(width: width, height: .greatestFiniteMagnitude),
                                           options: [.usesLineFragmentOrigin, .usesFontLeading])
        return CGSize(width: width, height: ceil(rect.height) + 2)
    }
}
