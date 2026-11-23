import SwiftUI
import TranslatorCore

/// Thanh trượt có nhãn và giá trị in đậm ở trên, dùng trong các thẻ cài đặt.
struct ValueSlider: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    let display: (Double) -> String

    var body: some View {
        VStack(spacing: 6) {
            HStack {
                Text(title)
                Spacer()
                Text(display(value)).fontWeight(.semibold).monospacedDigit()
            }
            Slider(value: $value, in: range).controlSize(.small).tint(Theme.blue)
        }
    }
}

struct LabeledControl<Control: View>: View {
    let title: String
    @ViewBuilder var control: Control

    var body: some View {
        HStack {
            Text(title)
            Spacer(minLength: 8)
            control
        }
    }
}

/// Thẻ "Phát hiện chữ mới".
struct DetectorCard: View {
    @Binding var profile: Profile

    var body: some View {
        SettingsCard(title: "Phát hiện chữ mới") {
            VStack(spacing: 14) {
                ValueSlider(title: "Chờ chữ ổn định", value: $profile.detector.stableMs, range: 100...1500) {
                    "\(Int($0)) ms"
                }
                ValueSlider(title: "Ẩn phụ đề sau", value: $profile.detector.hideDelayMs, range: 200...3000) {
                    "\(Int($0)) ms"
                }
                ValueSlider(title: "Ngưỡng thay đổi", value: $profile.detector.changeThreshold, range: 0.5...20) {
                    viNumber($0, digits: 1)
                }
                LabeledControl(title: "Lấy mẫu mỗi \(profile.detector.sampleEveryNFrames) khung") {
                    Stepper("", value: $profile.detector.sampleEveryNFrames, in: 3...60).labelsHidden()
                }
            }
            .font(.system(size: 13))
            .padding(.vertical, 10)
        }
    }
}

/// Thẻ "Nhận dạng chữ (OCR)".
struct OCRCard: View {
    @Binding var profile: Profile

    var body: some View {
        SettingsCard(title: "Nhận dạng chữ (OCR)") {
            VStack(spacing: 12) {
                LabeledControl(title: "Mức nhận dạng") {
                    Picker("", selection: $profile.ocr.level) {
                        Text("Chính xác").tag(OCRLevel.accurate)
                        Text("Nhanh").tag(OCRLevel.fast)
                    }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                }
                ValueSlider(title: "Phóng to ảnh", value: $profile.ocr.scale, range: 1...4) { viNumber($0, digits: 1) + "x" }
                ValueSlider(title: "Tương phản", value: $profile.ocr.contrast, range: 0.5...2.5) { viNumber($0, digits: 1) }
                ValueSlider(title: "Bỏ chữ nhỏ hơn", value: $profile.ocr.minimumTextHeight, range: 0...0.15) {
                    viNumber($0 * 100, digits: 1) + "% chiều cao"
                }
                LabeledControl(title: "Đảo màu (chữ sáng trên nền tối)") {
                    Toggle("", isOn: $profile.ocr.invert).toggleStyle(.switch).tint(Theme.green).labelsHidden()
                }
            }
            .font(.system(size: 13))
            .padding(.vertical, 10)
        }
    }
}

/// Thẻ "Phụ đề".
struct SubtitleCard: View {
    @Binding var profile: Profile
    @Binding var showOriginal: Bool

    var body: some View {
        SettingsCard(title: "Phụ đề") {
            VStack(spacing: 12) {
                LabeledControl(title: "Vị trí") {
                    Picker("", selection: $profile.subtitle.position) {
                        Text("Trên").tag(SubtitlePosition.above)
                        Text("Dưới").tag(SubtitlePosition.below)
                        Text("Đè lên").tag(SubtitlePosition.over)
                        Text("Tự do").tag(SubtitlePosition.free)
                    }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                }
                LabeledControl(title: "Căn lề chữ") {
                    Picker("", selection: $profile.subtitle.alignment) {
                        ForEach(SubtitleAlignment.allCases, id: \.self) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                }
                LabeledControl(title: "Số dòng tối đa") {
                    HStack(spacing: 8) {
                        Text("\(profile.subtitle.maxLines)").fontWeight(.semibold).monospacedDigit()
                        Stepper("", value: $profile.subtitle.maxLines, in: 1...30).labelsHidden()
                    }
                }
                ValueSlider(title: "Cỡ chữ", value: $profile.subtitle.fontSize, range: 12...72) { "\(Int($0)) pt" }
                ValueSlider(title: "Nền", value: $profile.subtitle.backgroundOpacity, range: 0...1) {
                    $0 < 0.01 ? "Không nền" : "Đen mờ \(Int(($0 * 100).rounded()))%"
                }
                LabeledControl(title: "Hiện chữ gốc bên dưới") {
                    Toggle("", isOn: $showOriginal).toggleStyle(.switch).tint(Theme.green).labelsHidden()
                }
            }
            .font(.system(size: 13))
            .padding(.vertical, 10)
        }
    }
}
