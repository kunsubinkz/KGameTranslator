import AppKit
import SwiftUI
import TranslatorCore

struct SettingsView: View {
    @Bindable var state: AppState
    @State var tab = 0

    var body: some View {
        TabView(selection: $tab) {
            GeneralTab(state: state).tabItem { Label("Chung", systemImage: "display") }.tag(0)
            TranslateTab(state: state).tabItem { Label("Dịch", systemImage: "character.bubble") }.tag(1)
            OCRTab(state: state).tabItem { Label("OCR", systemImage: "text.viewfinder") }.tag(2)
            SubtitleTab(state: state).tabItem { Label("Phụ đề", systemImage: "captions.bubble") }.tag(3)
            ShortcutsTab().tabItem { Label("Phím tắt", systemImage: "keyboard") }.tag(4)
        }
        .frame(width: 760, height: 660)
    }
}

private struct SettingsPage<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        ScrollView {
            VStack(spacing: 18) { content }
                .font(.system(size: 13))
                .padding(.horizontal, 28)
                .padding(.vertical, 22)
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }
}

@MainActor private func profileNote(_ state: AppState) -> some View {
    Text("Áp dụng cho hồ sơ đang dùng: \(state.profile.name)")
        .font(.system(size: 12)).foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading)
}

// MARK: - Chung

private struct GeneralTab: View {
    @Bindable var state: AppState

    var body: some View {
        SettingsPage {
            SettingsCard {
                SettingsRow(title: "Capture card", detail: "Thiết bị cung cấp hình game") {
                    Picker("", selection: $state.selectedVideoID) {
                        Text("Chưa chọn").tag(String?.none)
                        ForEach(state.videoDevices) { Text($0.name).tag(String?.some($0.id)) }
                    }
                    .labelsHidden().frame(width: 250)
                }
                SettingsRow(title: "Nguồn tiếng", detail: "Thường trùng tên với capture card") {
                    Picker("", selection: $state.selectedAudioID) {
                        Text("Không phát tiếng").tag(String?.none)
                        ForEach(state.audioDevices) { Text($0.name).tag(String?.some($0.id)) }
                    }
                    .labelsHidden().frame(width: 250)
                }
                SettingsRow(title: "Phát tiếng ra", detail: "Loa hoặc tai nghe") {
                    Picker("", selection: $state.selectedOutputID) {
                        Text("Mặc định của hệ thống").tag(String?.none)
                        ForEach(state.audioOutputs) { Text($0.name).tag(String?.some($0.id)) }
                    }
                    .labelsHidden().frame(width: 250)
                }
                SettingsRow(title: "Âm lượng", showsDivider: false) {
                    Slider(value: $state.volume, in: 0...1).frame(width: 250)
                }
            }

            SettingsCard {
                SettingsRow(title: "Hiển thị hình", detail: "Cách phóng 1080p lên màn 4K. Mịn/Điểm ảnh vẽ bằng GPU ngay khi khung đến") {
                    Picker("", selection: $state.videoSmoothing) {
                        ForEach(VideoSmoothing.allCases, id: \.self) { Text($0.title).tag($0) }
                    }
                    .labelsHidden().frame(width: 250)
                }
                SettingsRow(title: "Hiện khung vùng OCR", detail: "Viền nét đứt quanh vùng hộp thoại (ẩn khi toàn màn hình)") {
                    Toggle("", isOn: $state.showRegionOutline).toggleStyle(.switch).tint(Theme.green).labelsHidden()
                }
                SettingsRow(title: "Dữ liệu ứng dụng", detail: "Hồ sơ game và cache bản dịch", showsDivider: false) {
                    HStack {
                        Button("Làm mới thiết bị") { state.refreshDevices() }
                        Button("Mở thư mục") { NSWorkspace.shared.open(ProfileStore.applicationSupport) }
                    }
                }
            }
        }
    }
}

// MARK: - Dịch

private struct TranslateTab: View {
    @Bindable var state: AppState
    @State private var apiKey = ""
    @State private var isEditingKey = false
    @State private var testResult: String?
    @State private var testing = false

    private static let modes: [TranslateMode] = [.onlineOnly, .onlineWithOfflineFallback, .offlineOnly]

    private static let customTag = "__custom__"
    @State private var forceCustom = false

    private var modelBinding: Binding<String> {
        Binding(
            get: { state.profile.translate.activeModel },
            set: { value in
                if state.profile.translate.provider == .claude { state.profile.translate.model = value }
                else { state.profile.translate.geminiModel = value }
            })
    }

    private var isCustomModel: Bool {
        forceCustom || !state.profile.translate.provider.presets.contains { $0.id == state.profile.translate.activeModel }
    }

    private var modelSelection: Binding<String> {
        Binding(
            get: { isCustomModel ? Self.customTag : state.profile.translate.activeModel },
            set: { value in
                if value == Self.customTag { forceCustom = true } else { forceCustom = false; modelBinding.wrappedValue = value }
            })
    }

    private var keyIsShown: Bool { state.hasAPIKey && !isEditingKey }

    private var maskedKey: String {
        let key = Keychain.read(account: Keychain.account(for: state.profile.translate.provider)) ?? ""
        return "••••••••••••" + key.suffix(4)
    }

    var body: some View {
        SettingsPage {
            SettingsCard {
                SettingsRow(title: "Chế độ dịch", detail: "Nguồn dịch cho phụ đề", showsDivider: false) {
                    Picker("", selection: $state.profile.translate.mode) {
                        ForEach(Self.modes, id: \.self) { Text($0.shortTitle).tag($0) }
                    }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                }
            }

            SettingsCard {
                SettingsRow(title: "Nhà cung cấp AI", detail: "Mặc định Claude; mỗi nhà cung cấp có API key riêng") {
                    Picker("", selection: $state.profile.translate.provider) {
                        ForEach(TranslateProvider.allCases, id: \.self) { Text($0.title).tag($0) }
                    }
                    .labelsHidden().frame(width: 270)
                }
                SettingsRow(title: "Khóa API \(state.profile.translate.provider == .claude ? "Claude" : "Gemini")", detail: testResult ?? "Lưu trong Keychain, không ghi vào hồ sơ hay log") {
                    HStack(spacing: 8) {
                        if keyIsShown {
                            Text(maskedKey)
                                .tracking(2)
                                .foregroundStyle(.secondary)
                                .frame(width: 200, height: 28, alignment: .leading)
                                .padding(.horizontal, 10)
                                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color(nsColor: .separatorColor)))
                            Button("Đổi") { isEditingKey = true }
                            Button("Thử") { runTest() }.disabled(testing)
                        } else {
                            SecureField("", text: $apiKey, prompt: Text(state.profile.translate.provider.keyPlaceholder))
                                .textFieldStyle(.roundedBorder).frame(width: 200)
                            Button("Lưu") {
                                state.setAPIKey(apiKey)
                                apiKey = ""
                                isEditingKey = false
                            }
                            .disabled(apiKey.isEmpty)
                            if state.hasAPIKey { Button("Hủy") { isEditingKey = false; apiKey = "" } }
                        }
                        if testing { ProgressView().controlSize(.small) }
                    }
                }
                SettingsRow(title: "Model", detail: "Chọn trong danh sách hoặc nhập tên model khác") {
                    HStack(spacing: 8) {
                        Picker("", selection: modelSelection) {
                            ForEach(state.profile.translate.provider.presets, id: \.id) { Text($0.title).tag($0.id) }
                            Text("Khác…").tag(Self.customTag)
                        }
                        .labelsHidden().frame(width: 270)
                        if isCustomModel {
                            TextField("", text: modelBinding, prompt: Text("Tên model"))
                                .textFieldStyle(.roundedBorder).frame(width: 200)
                        }
                    }
                }
                SettingsRow(title: "Câu ngữ cảnh gửi kèm", detail: "Các câu trước giúp giữ giọng nhân vật") {
                    HStack(spacing: 8) {
                        Text("\(state.profile.translate.contextLines)").fontWeight(.semibold)
                        Stepper("", value: $state.profile.translate.contextLines, in: 0...6).labelsHidden()
                    }
                }
                quotaBlock
            }

            SettingsCard {
                SettingsRow(title: "Gói dịch offline Anh → Việt", detail: availabilityText, detailColor: availabilityColor) {
                    availabilityButton
                }
                SettingsRow(title: "Nâng cấp bản dịch offline", detail: "Tự thay bằng bản AI online khi còn hạn mức",
                            showsDivider: false) {
                    Toggle("", isOn: $state.profile.translate.upgradeOffline)
                        .toggleStyle(.switch).tint(Theme.green).labelsHidden()
                }
            }
        }
        .task { await state.apple.refreshAvailability() }
    }

    private var quotaBlock: some View {
        let used = state.quotaRemaining.map { state.quotaLimit - $0 } ?? 0
        return VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Hạn mức hôm nay").fontWeight(.semibold)
                Spacer()
                Text("Còn \(state.quotaRemaining ?? state.quotaLimit)/\(state.quotaLimit) request · \(state.profile.translate.requestsPerMinute) request/phút")
                    .foregroundStyle(.secondary)
            }
            ProgressView(value: Double(max(used, 0)), total: Double(max(state.quotaLimit, 1))).tint(Theme.blue)
            HStack {
                Text("Đặt lại lúc nửa đêm giờ Thái Bình Dương").font(.system(size: 12)).foregroundStyle(.secondary)
                Spacer()
                Stepper("Mỗi phút", value: $state.profile.translate.requestsPerMinute, in: 1...60)
                Stepper("Mỗi ngày", value: $state.profile.translate.requestsPerDay, in: 10...5000, step: 10)
            }
            .font(.system(size: 12))
        }
        .padding(.vertical, 12)
    }

    private var availabilityText: String {
        switch state.apple.availability {
        case .unknown: return "Đang kiểm tra..."
        case .installed: return "Đã tải về máy · Apple Translation"
        case .needsDownload: return "Chưa tải gói ngôn ngữ"
        case .unsupported: return "macOS này chưa hỗ trợ cặp Anh → Việt"
        }
    }

    private var availabilityColor: Color {
        switch state.apple.availability {
        case .installed: return Theme.green
        case .unsupported: return .red
        default: return .secondary
        }
    }

    @ViewBuilder
    private var availabilityButton: some View {
        switch state.apple.availability {
        case .needsDownload:
            HStack {
                if state.apple.isPreparing { ProgressView().controlSize(.small) }
                Button("Tải gói dịch offline") { Task { await state.apple.prepare() } }.disabled(state.apple.isPreparing)
            }
        case .installed, .unknown:
            Button("Quản lý gói ngôn ngữ") {
                if let url = URL(string: "x-apple.systempreferences:com.apple.Localization-Settings.extension") {
                    NSWorkspace.shared.open(url)
                }
            }
        case .unsupported:
            EmptyView()
        }
    }

    private func runTest() {
        testing = true
        Task {
            testResult = await state.testTranslator()
            testing = false
        }
    }
}

private extension TranslateMode {
    var shortTitle: String {
        switch self {
        case .onlineOnly: return "Chỉ Claude"
        case .onlineWithOfflineFallback: return "Claude, dự phòng offline"
        case .offlineOnly: return "Chỉ offline"
        }
    }
}

// MARK: - OCR, Phụ đề, Phím tắt

private struct OCRTab: View {
    @Bindable var state: AppState

    var body: some View {
        SettingsPage {
            profileNote(state)
            OCRCard(profile: $state.profile)
            DetectorCard(profile: $state.profile)
        }
    }
}

private struct SubtitleTab: View {
    @Bindable var state: AppState

    var body: some View {
        SettingsPage {
            profileNote(state)
            SubtitleCard(profile: $state.profile, showOriginal: $state.showOriginal)
        }
    }
}

private struct ShortcutsTab: View {
    private let rows: [(String, String)] = [
        ("⌘T", "Bật/tắt dịch"), ("⌘R", "Chọn vùng hộp thoại"), ("⌘⇧R", "Dịch lại câu hiện tại (bỏ qua cache)"),
        ("⌘O", "Hiện/ẩn chữ gốc"), ("⌃⌘F", "Toàn màn hình"), ("⌘Y", "Lịch sử bản dịch"),
        ("⌘⇧P", "Hồ sơ game"), ("⌘,", "Cài đặt"),
    ]

    var body: some View {
        SettingsPage {
            SettingsCard {
                ForEach(Array(rows.enumerated()), id: \.offset) { index, row in
                    SettingsRow(title: row.1, showsDivider: index < rows.count - 1) {
                        Text(row.0)
                            .font(.system(size: 12, design: .monospaced))
                            .padding(.horizontal, 8).padding(.vertical, 2)
                            .background(Color(nsColor: .quaternaryLabelColor).opacity(0.5), in: RoundedRectangle(cornerRadius: 5))
                    }
                }
            }
            Text("Phím tắt hoạt động khi cửa sổ ứng dụng đang được chọn.")
                .font(.system(size: 12)).foregroundStyle(.secondary)
        }
    }
}
