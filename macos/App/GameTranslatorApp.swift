import AppKit
import SwiftUI

@main
struct GameTranslatorApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @State private var state = AppState()

    init() {
        if CommandLine.arguments.contains("--selftest") {
            exit(SelfTest.run())
        }
    }

    var body: some Scene {
        Window("Game Translator", id: "main") {
            MainWindow(state: state)
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 1180, height: 760)
        .commands { AppCommands(state: state) }

        Window("Lịch sử bản dịch", id: "history") {
            HistoryView(state: state)
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 960, height: 660)

        Window("Hồ sơ game", id: "profiles") {
            ProfilesView(state: state)
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 960, height: 720)

        Settings {
            SettingsView(state: state)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

struct AppCommands: Commands {
    let state: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some Commands {
        CommandMenu("Dịch") {
            Button(state.translationEnabled ? "Tắt dịch" : "Bật dịch") {
                state.translationEnabled.toggle()
            }
            .keyboardShortcut("t")

            Button("Chọn vùng hộp thoại") { state.beginRegionSelection() }
                .keyboardShortcut("r")
            Button("Dịch lại câu hiện tại") { state.retranslate() }
                .keyboardShortcut("r", modifiers: [.command, .shift])
            Button(state.showOriginal ? "Ẩn chữ gốc" : "Hiện chữ gốc") { state.showOriginal.toggle() }
                .keyboardShortcut("o")

            Divider()

            Button("Toàn màn hình") { state.toggleFullScreen() }
                .keyboardShortcut("f", modifiers: [.command, .control])
            Button("Lịch sử bản dịch") { openWindow(id: "history") }
                .keyboardShortcut("y")
            Button("Hồ sơ game") { openWindow(id: "profiles") }
                .keyboardShortcut("p", modifiers: [.command, .shift])
        }
    }
}
