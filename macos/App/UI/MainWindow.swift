import AppKit
import SwiftUI
import Translation
import TranslatorCore

struct MainWindow: View {
    @Bindable var state: AppState

    var body: some View {
        VStack(spacing: 0) {
            if state.isSelectingRegion {
                SelectionHeader()
            } else if !state.isFullScreen {
                ToolbarBar(state: state)
            }

            ZStack {
                Color.black
                if state.videoSmoothing == .system {
                    VideoView(session: state.capture.session)
                } else {
                    MetalVideoView(renderer: state.renderer)
                }
                GeometryReader { geo in
                    let rect = state.videoSize.aspectFit(in: geo.size)
                    ZStack {
                        if !state.isSelectingRegion {
                            if state.showRegionOutline, !state.isFullScreen {
                                RegionOutline(regions: state.profile.allRegions, videoRect: rect)
                            }
                            SubtitleOverlay(state: state, videoRect: rect)
                            if !state.isFullScreen { VideoBadges(state: state) }
                        }
                        if state.isSelectingRegion {
                            RegionSelector(state: state, videoRect: rect)
                        }
                    }
                }
                PermissionNotice(state: state)
                translationHost
            }

            if !state.isFullScreen, !state.isSelectingRegion {
                StatusBar(state: state)
            }
        }
        .background(Theme.darkBackground)
        .foregroundStyle(Theme.darkText)
        .preferredColorScheme(.dark)
        .frame(minWidth: 960, minHeight: 600)
        .task { await state.start() }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didEnterFullScreenNotification)) { _ in
            state.isFullScreen = true
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didExitFullScreenNotification)) { _ in
            state.isFullScreen = false
        }
    }

    /// View ẩn để lấy `TranslationSession` cho dịch offline.
    private var translationHost: some View {
        Color.clear
            .frame(width: 1, height: 1)
            .translationTask(state.apple.configuration) { session in
                await state.apple.run(session: session)
            }
    }
}

// MARK: - Thanh công cụ

private struct ToolbarBar: View {
    @Bindable var state: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        HStack(spacing: 14) {
            Color.clear.frame(width: 70, height: 1)  // chừa chỗ cho ba nút cửa sổ
            deviceMenu
            profileMenu
            Spacer(minLength: 8)
            volume
            regionButton
            TranslateSwitch(isOn: $state.translationEnabled)
            iconButton("arrow.up.left.and.arrow.down.right", help: "Toàn màn hình (⌃⌘F)") { state.toggleFullScreen() }
            SettingsLink {
                Image(systemName: "gearshape")
                    .font(.system(size: 14))
                    .frame(width: 30, height: 30)
                    .background(Theme.darkPill, in: RoundedRectangle(cornerRadius: 7))
            }
            .buttonStyle(.plain)
            .help("Cài đặt (⌘,)")
        }
        .padding(.horizontal, 16)
        .frame(height: 52)
        .background(WindowDragArea())
        .background(Theme.darkBar)
        .overlay(alignment: .bottom) { Rectangle().fill(.black).frame(height: 1) }
    }

    private var deviceTitle: String {
        guard let device = state.videoDevices.first(where: { $0.id == state.selectedVideoID }) else {
            return "Chưa chọn capture card"
        }
        if case .running(_, let h, let fps, _) = state.captureStatus { return "\(device.name) · \(h)p\(fps)" }
        return device.name
    }

    private var deviceMenu: some View {
        Menu {
            Picker("Capture card", selection: $state.selectedVideoID) {
                Text("Chưa chọn").tag(String?.none)
                ForEach(state.videoDevices) { Text($0.name).tag(String?.some($0.id)) }
            }
            .pickerStyle(.inline)
            Picker("Nguồn tiếng", selection: $state.selectedAudioID) {
                Text("Không phát tiếng").tag(String?.none)
                ForEach(state.audioDevices) { Text($0.name).tag(String?.some($0.id)) }
            }
            .pickerStyle(.inline)
            Picker("Phát tiếng ra", selection: $state.selectedOutputID) {
                Text("Mặc định của hệ thống").tag(String?.none)
                ForEach(state.audioOutputs) { Text($0.name).tag(String?.some($0.id)) }
            }
            .pickerStyle(.inline)
        } label: {
            ToolbarPill(symbol: "web.camera", title: deviceTitle)
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
    }

    private var profileMenu: some View {
        Menu {
            Picker("Hồ sơ", selection: Binding(get: { state.profile.id }, set: { state.selectProfile(id: $0) })) {
                ForEach(state.profiles) { Text($0.name).tag($0.id) }
            }
            .pickerStyle(.inline)
            Divider()
            Button("Quản lý hồ sơ game...") { openWindow(id: "profiles") }
        } label: {
            ToolbarPill(symbol: "square.grid.2x2", title: state.profile.name)
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
    }

    private var volume: some View {
        HStack(spacing: 8) {
            Image(systemName: state.volume == 0 ? "speaker.slash" : "speaker.wave.2").font(.system(size: 14))
            Slider(value: $state.volume, in: 0...1).controlSize(.small).frame(width: 90).tint(Theme.darkText)
        }
        .help("Âm lượng")
    }

    private var regionButton: some View {
        Button { state.beginRegionSelection() } label: {
            HStack(spacing: 6) {
                Image(systemName: "viewfinder").font(.system(size: 13))
                Text("Chọn vùng")
                Text("⌘R").foregroundStyle(Theme.darkSecondary)
            }
            .font(.system(size: 13))
            .padding(.horizontal, 12)
            .frame(height: 30)
            .background(Theme.darkPill, in: RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Chọn vùng hộp thoại")
    }

    private func iconButton(_ symbol: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 14))
                .frame(width: 30, height: 30)
                .background(Theme.darkPill, in: RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .help(help)
    }
}

private struct ToolbarPill: View {
    let symbol: String
    let title: String

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: symbol).font(.system(size: 13))
            Text(title).lineLimit(1)
            Image(systemName: "chevron.down").font(.system(size: 9, weight: .bold)).foregroundStyle(Theme.darkSecondary)
        }
        .font(.system(size: 13))
        .foregroundStyle(Theme.darkText)
        .padding(.horizontal, 10)
        .frame(height: 30)
        .background(Theme.darkPill, in: RoundedRectangle(cornerRadius: 7))
    }
}

/// Công tắc dạng viên thuốc "Dịch bật/tắt" như thiết kế.
private struct TranslateSwitch: View {
    @Binding var isOn: Bool

    var body: some View {
        Button { isOn.toggle() } label: {
            HStack(spacing: 8) {
                if isOn { Text("Dịch bật") }
                Circle().fill(isOn ? Color.white : Theme.darkSecondary).frame(width: 22, height: 22)
                if !isOn { Text("Dịch tắt").foregroundStyle(Theme.darkSecondary) }
            }
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(.white)
            .padding(.leading, isOn ? 12 : 6)
            .padding(.trailing, isOn ? 6 : 12)
            .frame(height: 30)
            .background(isOn ? Theme.darkBlue : Theme.darkPill, in: Capsule())
            .animation(.easeInOut(duration: 0.15), value: isOn)
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Bật tắt dịch")
    }
}

private struct SelectionHeader: View {
    var body: some View {
        HStack(spacing: 12) {
            Color.clear.frame(width: 70, height: 1)
            Text("Chọn vùng hộp thoại").font(.system(size: 13, weight: .semibold))
            Text("Kéo chuột để khoanh vùng chứa chữ thoại").font(.system(size: 13)).foregroundStyle(Theme.darkSecondary)
            Spacer()
        }
        .padding(.horizontal, 16)
        .frame(height: 52)
        .background(WindowDragArea())
        .background(Theme.darkBar)
        .overlay(alignment: .bottom) { Rectangle().fill(.black).frame(height: 1) }
    }
}

// MARK: - Lớp phủ trên video

private struct RegionOutline: View {
    let regions: [Region]
    let videoRect: CGRect

    var body: some View {
        ZStack(alignment: .topLeading) {
            ForEach(Array(regions.enumerated()), id: \.offset) { index, region in
                let r = region.clamped()
                let rect = CGRect(x: videoRect.minX + r.x * videoRect.width, y: videoRect.minY + r.y * videoRect.height,
                                  width: r.width * videoRect.width, height: r.height * videoRect.height)
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Theme.darkBlue, style: StrokeStyle(lineWidth: 2, dash: [6, 4]))
                    .frame(width: rect.width, height: rect.height)
                    .position(x: rect.midX, y: rect.midY)
                Text(regions.count > 1 ? "Vùng OCR \(index + 1)" : "Vùng OCR")
                    .fixedSize()
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 8).padding(.vertical, 2)
                    .background(Theme.darkBlue, in: UnevenRoundedRectangle(topLeadingRadius: 5, topTrailingRadius: 5))
                    .offset(x: rect.minX, y: rect.minY - 22)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .allowsHitTesting(false)
    }
}

private struct VideoBadges: View {
    let state: AppState

    var body: some View {
        HStack(spacing: 8) {
            if case .running(let w, let h, let fps, let codec) = state.captureStatus {
                badge((h >= 1080 || w >= 1920 ? "1080p · \(fps) fps" : "\(w)x\(h) · \(fps) fps") + " · \(codec)")
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
        .padding(.top, 14).padding(.trailing, 16)
        .allowsHitTesting(false)
    }

    private func badge(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 12))
            .foregroundStyle(.white)
            .padding(.horizontal, 9).padding(.vertical, 4)
            .background(Color.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 6))
    }
}

private struct PermissionNotice: View {
    let state: AppState

    var body: some View {
        if state.permission == .denied {
            VStack(spacing: 12) {
                Image(systemName: "video.slash").font(.system(size: 40))
                Text("Chưa được cấp quyền Camera").font(.title3.bold())
                Text("Cho phép Game Translator dùng Camera (capture card) và Micro (tiếng game) trong System Settings > Privacy & Security.")
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 420)
                HStack {
                    Button("Mở Camera trong System Settings") { open("Privacy_Camera") }
                    Button("Mở Micro") { open("Privacy_Microphone") }
                }
            }
            .foregroundStyle(.white)
            .padding(24)
            .background(.black.opacity(0.7), in: RoundedRectangle(cornerRadius: 14))
        } else if state.videoDevices.isEmpty, state.permission == .granted {
            VStack(spacing: 8) {
                Image(systemName: "cable.connector").font(.system(size: 40))
                Text("Chưa thấy capture card").font(.title3.bold())
                Text("Cắm capture card (chuẩn UVC) vào Mac. Nên cắm thẳng, tránh hub USB rẻ tiền.")
            }
            .foregroundStyle(.white.opacity(0.85))
        }
    }

    private func open(_ pane: String) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") {
            NSWorkspace.shared.open(url)
        }
    }
}
