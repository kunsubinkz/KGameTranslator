import AppKit
import SwiftUI

extension Color {
    init(hex: UInt32, opacity: Double = 1) {
        self.init(.sRGB,
                  red: Double((hex >> 16) & 0xFF) / 255,
                  green: Double((hex >> 8) & 0xFF) / 255,
                  blue: Double(hex & 0xFF) / 255,
                  opacity: opacity)
    }
}

/// Màu và kiểu theo bản thiết kế: cửa sổ chính tối (iOS dark grey), các cửa sổ còn lại dùng màu hệ thống.
enum Theme {
    // Cửa sổ chính (luôn tối)
    static let darkBackground = Color(hex: 0x1C1C1E)
    static let darkBar = Color(hex: 0x2C2C2E)
    static let darkPill = Color(hex: 0x3A3A3C)
    static let darkBorder = Color(hex: 0x48484A)
    static let darkText = Color(hex: 0xF2F2F7)
    static let darkSecondary = Color(hex: 0xAEAEB2)
    static let darkBlue = Color(hex: 0x0A84FF)
    static let statusGreen = Color(hex: 0x30D158)

    /// Nền thanh tiêu đề và thanh bên của các cửa sổ phụ (#E8E8ED sáng, #2C2C2E tối).
    static let chrome = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? NSColor(hex: 0x2C2C2E) : NSColor(hex: 0xE8E8ED)
    })

    // Cửa sổ sáng
    static let blue = Color(hex: 0x0A60D6)
    static let green = Color(hex: 0x1A7F37)
    static let orange = Color(hex: 0x8A5A00)

    static let claudeBadge = (fg: Color(hex: 0x0A4FB3), bg: Color(hex: 0xE3F0FF))
    static let offlineBadge = (fg: Color(hex: 0x8A5A00), bg: Color(hex: 0xFDF0D5))
    static let manualBadge = (fg: Color(hex: 0x1A6B34), bg: Color(hex: 0xE6F4EA))
}

extension NSColor {
    convenience init(hex: UInt32) {
        self.init(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                  blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
    }
}

/// Số theo kiểu Việt Nam: dấu phẩy thập phân (0,18).
func viNumber(_ value: Double, digits: Int = 2) -> String {
    let formatter = NumberFormatter()
    formatter.locale = Locale(identifier: "vi_VN")
    formatter.numberStyle = .decimal
    formatter.minimumFractionDigits = digits
    formatter.maximumFractionDigits = digits
    return formatter.string(from: NSNumber(value: value)) ?? String(value)
}

func viInteger(_ value: Int) -> String {
    let formatter = NumberFormatter()
    formatter.locale = Locale(identifier: "vi_VN")
    formatter.numberStyle = .decimal
    return formatter.string(from: NSNumber(value: value)) ?? String(value)
}

/// Thẻ trắng bo góc chứa các hàng cài đặt.
struct SettingsCard<Content: View>: View {
    var title: String?
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if let title {
                Text(title).font(.system(size: 13, weight: .semibold)).padding(.top, 12).padding(.bottom, 4)
            }
            content
        }
        .padding(.horizontal, 16)
        .padding(.vertical, title == nil ? 6 : 8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color(nsColor: .separatorColor).opacity(0.6)))
    }
}

/// Một hàng: tiêu đề và mô tả bên trái, điều khiển bên phải.
struct SettingsRow<Control: View>: View {
    let title: String
    var detail: String?
    var detailColor: Color = .secondary
    var showsDivider = true
    @ViewBuilder var control: Control

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .center, spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.system(size: 13, weight: .semibold))
                    if let detail { Text(detail).font(.system(size: 12)).foregroundStyle(detailColor) }
                }
                Spacer(minLength: 12)
                control
            }
            .padding(.vertical, 10)
            if showsDivider { Divider() }
        }
    }
}

/// Nhãn nguồn dịch dạng viên thuốc: Claude, Offline, Đã sửa tay.
struct EngineBadge: View {
    let engine: String

    var body: some View {
        let style = Self.style(for: engine)
        Text(style.title)
            .font(.system(size: 12))
            .padding(.horizontal, 8).padding(.vertical, 2)
            .foregroundStyle(style.fg)
            .background(style.bg, in: Capsule())
    }

    static func style(for engine: String) -> (title: String, fg: Color, bg: Color) {
        switch engine {
        case "manual": return ("Đã sửa tay", Theme.manualBadge.fg, Theme.manualBadge.bg)
        case "apple": return ("Offline", Theme.offlineBadge.fg, Theme.offlineBadge.bg)
        case let e where e.hasPrefix("claude"): return ("Claude", Theme.claudeBadge.fg, Theme.claudeBadge.bg)
        case let e where e.hasPrefix("gemini"): return ("Gemini", Theme.claudeBadge.fg, Theme.claudeBadge.bg)
        default: return (engine, .secondary, Color.gray.opacity(0.15))
        }
    }
}

/// Cho phép kéo cửa sổ bằng thanh tiêu đề tự vẽ khi dùng `.hiddenTitleBar`.
struct WindowDragArea: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { DragView() }
    func updateNSView(_ nsView: NSView, context: Context) {}

    private final class DragView: NSView {
        override var mouseDownCanMoveWindow: Bool { true }
    }
}

/// Thanh tiêu đề tự vẽ cho các cửa sổ phụ (chừa chỗ cho ba nút cửa sổ).
struct TitleBar<Content: View>: View {
    let title: String
    @ViewBuilder var trailing: Content

    var body: some View {
        HStack(spacing: 12) {
            Color.clear.frame(width: 70, height: 1)
            Text(title).font(.system(size: 13, weight: .semibold))
            Spacer()
            trailing
        }
        .padding(.horizontal, 16)
        .frame(height: 52)
        .background(WindowDragArea())
        .background(Theme.chrome)
        .overlay(alignment: .bottom) { Divider() }
    }
}
