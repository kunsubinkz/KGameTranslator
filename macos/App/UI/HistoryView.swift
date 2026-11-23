import AppKit
import SwiftUI
import TranslatorCore
import UniformTypeIdentifiers

struct HistoryView: View {
    let state: AppState

    @State private var profileID = ""
    @State private var search = ""
    @State private var items: [CachedTranslation] = []
    @State private var stats = (count: 0, hits: 0)
    @State private var selectedKey: String?
    @State private var editingKey: String?
    @State private var draft = ""
    @State private var isRegenerating = false

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f
    }()

    private var filtered: [CachedTranslation] {
        let query = search.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return items }
        let options: String.CompareOptions = [.caseInsensitive, .diacriticInsensitive]
        return items.filter {
            $0.source.range(of: query, options: options) != nil || $0.target.range(of: query, options: options) != nil
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            columnHeader
            Divider()
            if filtered.isEmpty {
                ContentUnavailableView(items.isEmpty ? "Chưa có bản dịch" : "Không tìm thấy câu nào",
                                       systemImage: "text.bubble",
                                       description: Text("Các câu đã dịch sẽ hiện ở đây để bạn xem và sửa."))
                    .frame(maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(filtered) { row($0) }
                    }
                }
            }
            footer
        }
        .frame(minWidth: 820, minHeight: 480)
        .background(Color(nsColor: .controlBackgroundColor))
        .font(.system(size: 13))
        .onAppear {
            if profileID.isEmpty { profileID = state.profile.id }
            reload()
        }
        .onChange(of: profileID) { editingKey = nil; selectedKey = nil; reload() }
        .onChange(of: state.subtitle) { reload() }
    }

    // MARK: - Khung

    private var header: some View {
        TitleBar(title: "Lịch sử bản dịch") {
            Picker("", selection: $profileID) {
                ForEach(state.profiles) { Text($0.name).tag($0.id) }
            }
            .labelsHidden().frame(width: 190)
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Tìm câu thoại", text: $search).textFieldStyle(.plain)
            }
            .padding(.horizontal, 10).frame(width: 210, height: 28)
            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
            .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color(nsColor: .separatorColor)))
        }
    }

    private var columnHeader: some View {
        HStack(alignment: .top, spacing: 14) {
            Text("Thời gian").frame(width: 96, alignment: .leading)
            Text("Tiếng Anh").frame(maxWidth: .infinity, alignment: .leading)
            Text("Tiếng Việt").frame(maxWidth: .infinity, alignment: .leading)
            Text("Nguồn dịch").frame(width: 110, alignment: .leading)
            Text("Dùng lại").frame(width: 56, alignment: .trailing)
        }
        .font(.system(size: 12, weight: .semibold))
        .foregroundStyle(.secondary)
        .padding(.horizontal, 18).padding(.vertical, 8)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private func row(_ item: CachedTranslation) -> some View {
        let isEditing = editingKey == item.key
        let isSelected = selectedKey == item.key
        return VStack(spacing: 0) {
            HStack(alignment: .top, spacing: 14) {
                Text(Self.timeFormatter.string(from: item.createdAt)).foregroundStyle(.secondary)
                    .frame(width: 96, alignment: .leading)
                Text(item.source).frame(maxWidth: .infinity, alignment: .leading)
                if isEditing {
                    editor(item).frame(maxWidth: .infinity, alignment: .leading)
                } else {
                    Text(item.target).frame(maxWidth: .infinity, alignment: .leading)
                }
                EngineBadge(engine: item.engine).frame(width: 110, alignment: .leading)
                Text("\(item.hits)").foregroundStyle(.secondary).frame(width: 56, alignment: .trailing)
            }
            .lineSpacing(2)
            .padding(.horizontal, 18).padding(.vertical, 11)
            Divider().opacity(0.6)
        }
        .background(isEditing ? Theme.blue.opacity(0.1) : isSelected ? Color.accentColor.opacity(0.08) : .clear)
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { beginEditing(item) }
        .onTapGesture { selectedKey = item.key }
    }

    private func editor(_ item: CachedTranslation) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            TextField("Bản dịch", text: $draft, axis: .vertical)
                .textFieldStyle(.plain)
                .padding(.horizontal, 8).padding(.vertical, 6)
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Theme.blue, lineWidth: 2))
                .onSubmit { save(item) }
            HStack(spacing: 8) {
                Button("Lưu sửa") { save(item) }
                    .buttonStyle(.borderedProminent).tint(Theme.blue).controlSize(.small)
                Button("Hủy") { editingKey = nil }.controlSize(.small)
            }
        }
    }

    private var footer: some View {
        HStack(spacing: 12) {
            Text("\(viInteger(stats.count)) câu · \(viInteger(stats.hits)) dùng lại từ cache").foregroundStyle(.secondary)
            Spacer()
            if isRegenerating { ProgressView().controlSize(.small) }
            Button("Dịch lại bằng AI online") { regenerate() }
                .disabled(selectedItem == nil || !state.hasAPIKey || isRegenerating)
                .help(state.hasAPIKey ? "Chọn một câu rồi dịch lại" : "Cần nhập API key trong Cài đặt")
            Button("Xuất CSV") { exportCSV() }.disabled(items.isEmpty)
        }
        .padding(.horizontal, 18).frame(height: 44)
        .background(Color(nsColor: .windowBackgroundColor))
        .overlay(alignment: .top) { Divider() }
    }

    // MARK: - Hành động

    private var selectedItem: CachedTranslation? {
        items.first { $0.key == selectedKey }
    }

    private func reload() {
        guard !profileID.isEmpty else { return }
        items = state.cache?.history(profileId: profileID, limit: 5000) ?? []
        stats = state.cache?.stats(profileId: profileID) ?? (0, 0)
    }

    private func beginEditing(_ item: CachedTranslation) {
        selectedKey = item.key
        editingKey = item.key
        draft = item.target
    }

    private func save(_ item: CachedTranslation) {
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        editingKey = nil
        guard !trimmed.isEmpty, trimmed != item.target else { return }
        state.cache?.updateTarget(key: item.key, target: trimmed)
        reload()
    }

    private func regenerate() {
        guard let item = selectedItem else { return }
        isRegenerating = true
        Task {
            _ = await state.regenerateOnline(item)
            isRegenerating = false
            reload()
        }
    }

    private func exportCSV() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.commaSeparatedText]
        panel.nameFieldStringValue = "\(profileID)-lich-su.csv"
        guard panel.runModal() == .OK, let url = panel.url else { return }

        func field(_ value: String) -> String { "\"" + value.replacingOccurrences(of: "\"", with: "\"\"") + "\"" }
        let iso = ISO8601DateFormatter()
        var lines = ["Thời gian,Tiếng Anh,Tiếng Việt,Nguồn dịch,Dùng lại"]
        for item in items {
            lines.append([iso.string(from: item.createdAt), item.source, item.target, item.engine]
                .map(field).joined(separator: ",") + ",\(item.hits)")
        }
        let data = Data(("\u{FEFF}" + lines.joined(separator: "\n")).utf8)
        try? data.write(to: url, options: .atomic)
    }
}
