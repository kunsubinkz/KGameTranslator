import SwiftUI

struct StatusBar: View {
    let state: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        HStack(spacing: 20) {
            HStack(spacing: 6) {
                Circle().fill(status.color).frame(width: 8, height: 8)
                Text(status.text)
            }
            if state.profile.translate.mode != .offlineOnly { Text(state.profile.translate.model) }
            if let remaining = state.quotaRemaining {
                Text("Còn \(remaining)/\(state.quotaLimit) request hôm nay")
            }
            Spacer()
            if let ocr = state.ocrMs { Text("OCR \(Int(ocr)) ms") }
            if let translate = state.translateMs { Text("Dịch \(Int(translate)) ms") }
            if let cpu = state.cpuPercent { Text("CPU \(Int(cpu.rounded()))%") }
            Button { openWindow(id: "history") } label: {
                Text("Lịch sử ") + Text("⌘Y").foregroundStyle(Theme.darkSecondary)
            }
            .buttonStyle(.plain)
        }
        .font(.system(size: 12))
        .foregroundStyle(Color(hex: 0xD1D1D6))
        .padding(.horizontal, 16)
        .frame(height: 32)
        .background(Theme.darkBar)
        .overlay(alignment: .top) { Rectangle().fill(.black).frame(height: 1) }
    }

    private var status: (text: String, color: Color) {
        switch state.captureStatus {
        case .idle: return ("Chưa chạy", .gray)
        case .interrupted: return ("Capture bị gián đoạn", .orange)
        case .disconnected(let name): return ("Mất kết nối: \(name)", .orange)
        case .failed(let message): return ("Lỗi: \(message)", .red)
        case .running: break
        }
        guard state.translationEnabled else { return ("Dịch đang tắt", .gray) }
        guard let subtitle = state.subtitle else {
            return (state.lastEngine == nil ? "Chờ câu thoại" : "Đang chờ câu mới", .gray)
        }
        guard subtitle.target != nil, let engine = subtitle.engine else { return ("Đang dịch...", Theme.statusGreen) }
        if subtitle.cached { return ("Lấy từ cache", Theme.statusGreen) }
        switch engine {
        case "apple": return ("Đã dịch offline (Apple)", .orange)
        case "original": return ("Không dịch được, hiện chữ gốc", .orange)
        case "manual": return ("Bản sửa tay", Theme.statusGreen)
        default: return (engine.hasPrefix("gemini") ? "Đã dịch bằng Gemini" : "Đã dịch bằng Claude", Theme.statusGreen)
        }
    }
}
