import AppKit
import SwiftUI
import TranslatorCore
import UniformTypeIdentifiers

/// Quản lý hồ sơ game: danh sách bên trái, trình sửa bên phải.
struct ProfilesView: View {
    @Bindable var state: AppState

    @State private var editingID = ""
    @State private var draft: Profile = .default
    @State private var glossaryRows: [GlossaryRow] = []
    @State private var showNewAlert = false
    @State private var newName = ""
    @State private var message: String?

    struct GlossaryRow: Identifiable, Equatable {
        let id = UUID()
        var key: String
        var value: String
    }

    var body: some View {
        HStack(spacing: 0) {
            sidebar
            Divider()
            editor
        }
        .frame(minWidth: 900, minHeight: 640)
        .background(Color(nsColor: .windowBackgroundColor))
        .font(.system(size: 13))
        .onAppear {
            if editingID.isEmpty { select(state.profile.id) }
        }
        .onChange(of: draft) { state.updateProfile(draft) }
        .onChange(of: state.profile) {
            if state.profile.id == editingID, state.profile != draft { draft = state.profile }
        }
        .onChange(of: state.profile.id) { select(state.profile.id) }
        .onChange(of: glossaryRows) {
            var dictionary: [String: String] = [:]
            for row in glossaryRows where !row.key.trimmingCharacters(in: .whitespaces).isEmpty {
                dictionary[row.key.trimmingCharacters(in: .whitespaces)] = row.value
            }
            if draft.glossary != dictionary { draft.glossary = dictionary }
        }
        .alert("Hồ sơ mới", isPresented: $showNewAlert) {
            TextField("Tên game", text: $newName)
            Button("Tạo") {
                let name = newName.trimmingCharacters(in: .whitespaces)
                if !name.isEmpty { state.createProfile(named: name) }
                newName = ""
            }
            Button("Hủy", role: .cancel) { newName = "" }
        } message: {
            Text("Hồ sơ mới bắt đầu từ cài đặt mặc định.")
        }
    }

    private func select(_ id: String) {
        guard let profile = state.profiles.first(where: { $0.id == id }) ?? (state.profile.id == id ? state.profile : nil) else { return }
        editingID = id
        draft = profile
        glossaryRows = profile.glossary.sorted { $0.key < $1.key }.map { GlossaryRow(key: $0.key, value: $0.value) }
    }

    // MARK: - Danh sách

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 4) {
            Color.clear.frame(height: 34).background(WindowDragArea())
            Text("HỒ SƠ")
                .font(.system(size: 11, weight: .semibold)).tracking(0.5).foregroundStyle(.secondary)
                .padding(.horizontal, 8).padding(.bottom, 4)

            ScrollView {
                VStack(spacing: 4) {
                    ForEach(state.profiles) { profile in
                        let selected = profile.id == editingID
                        HStack {
                            Text(profile.name).lineLimit(1)
                            Spacer()
                            if profile.id == state.profile.id, !selected {
                                Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundStyle(Theme.blue)
                            }
                        }
                        .fontWeight(selected ? .semibold : .regular)
                        .foregroundStyle(selected ? Color.white : Color.primary)
                        .padding(.horizontal, 10).padding(.vertical, 8)
                        .background(selected ? Theme.blue : .clear, in: RoundedRectangle(cornerRadius: 7))
                        .contentShape(Rectangle())
                        .onTapGesture { select(profile.id) }
                        .contextMenu {
                            Button("Nhân đôi") { state.createProfile(named: profile.name + " bản sao", basedOn: profile) }
                            Button("Xóa", role: .destructive) { state.deleteProfile(id: profile.id) }
                                .disabled(state.profiles.count < 2)
                        }
                    }
                }
            }

            Button { showNewAlert = true } label: {
                Text("+ Hồ sơ mới").frame(maxWidth: .infinity, minHeight: 30)
            }
            .buttonStyle(.bordered)
            HStack(spacing: 6) {
                Button { importFile() } label: { Text("Nhập JSON").frame(maxWidth: .infinity) }
                Button { exportFile() } label: { Text("Xuất JSON").frame(maxWidth: .infinity) }
            }
            .controlSize(.small)
            .buttonStyle(.bordered)
            if let message { Text(message).font(.system(size: 11)).foregroundStyle(.secondary) }
        }
        .padding(.horizontal, 10).padding(.bottom, 14)
        .frame(width: 230)
        .background(Theme.chrome)
    }

    // MARK: - Trình sửa

    private var isActive: Bool { editingID == state.profile.id }

    private var editor: some View {
        ScrollView {
            VStack(spacing: 14) {
                HStack(spacing: 12) {
                    TextField("Tên hồ sơ", text: $draft.name)
                        .textFieldStyle(.plain)
                        .font(.system(size: 18, weight: .semibold))
                    Button(isActive ? "Đang dùng" : "Dùng hồ sơ này") { state.selectProfile(id: editingID) }
                        .buttonStyle(.borderedProminent).tint(Theme.blue)
                        .disabled(isActive)
                }
                .padding(.top, 8)

                HStack(alignment: .top, spacing: 14) {
                    regionCard
                    DetectorCard(profile: $draft)
                }
                HStack(alignment: .top, spacing: 14) {
                    OCRCard(profile: $draft)
                    SubtitleCard(profile: $draft, showOriginal: $state.showOriginal)
                }
                glossaryCard
            }
            .padding(.horizontal, 26).padding(.vertical, 18)
        }
    }

    private var regionCard: some View {
        let r = draft.region.clamped()
        return SettingsCard(title: "Vùng hộp thoại") {
            VStack(spacing: 10) {
                GeometryReader { geo in
                    ZStack(alignment: .topLeading) {
                        if let image = state.snapshot {
                            Image(decorative: image, scale: 1).resizable()
                                .frame(width: geo.size.width, height: geo.size.height)
                        } else {
                            LinearGradient(stops: [.init(color: Color(hex: 0x5AA0DC), location: 0),
                                                   .init(color: Color(hex: 0xA8D2EE), location: 0.55),
                                                   .init(color: Color(hex: 0x4C8A3C), location: 0.6)],
                                           startPoint: .top, endPoint: .bottom)
                        }
                        Rectangle()
                            .strokeBorder(.white, style: StrokeStyle(lineWidth: 2, dash: [5, 3]))
                            .background(Theme.blue.opacity(0.35))
                            .frame(width: r.width * geo.size.width, height: r.height * geo.size.height)
                            .offset(x: r.x * geo.size.width, y: r.y * geo.size.height)
                    }
                }
                .aspectRatio(state.videoSize.width / max(state.videoSize.height, 1), contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 6))

                HStack {
                    Text("x \(viNumber(r.x))"); Spacer()
                    Text("y \(viNumber(r.y))"); Spacer()
                    Text("rộng \(viNumber(r.width))"); Spacer()
                    Text("cao \(viNumber(r.height))")
                }
                .foregroundStyle(.secondary)

                Button { state.beginRegionSelection(forProfile: editingID) } label: {
                    (Text("Chọn lại vùng ") + Text("⌘R").foregroundStyle(.secondary)).frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
            }
            .padding(.vertical, 10)
        }
    }

    private var glossaryCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Từ điển tên riêng").fontWeight(.semibold)
                    Spacer()
                    Text("Giữ nguyên khi dịch, giúp OCR đọc đúng").foregroundStyle(.secondary)
                }
                HStack(spacing: 12) {
                    Text("Tiếng Anh").frame(maxWidth: .infinity, alignment: .leading)
                    Text("Giữ / dịch thành").frame(maxWidth: .infinity, alignment: .leading)
                    Color.clear.frame(width: 24, height: 1)
                }
                .font(.system(size: 12, weight: .semibold)).foregroundStyle(.secondary)

                ForEach($glossaryRows) { $row in
                    HStack(spacing: 12) {
                        TextField("", text: $row.key).textFieldStyle(.roundedBorder)
                        TextField("", text: $row.value).textFieldStyle(.roundedBorder)
                        Button { glossaryRows.removeAll { $0.id == row.id } } label: {
                            Image(systemName: "minus.circle").foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain).frame(width: 24)
                    }
                }
                Button { glossaryRows.append(GlossaryRow(key: "", value: "")) } label: {
                    Label("Thêm từ", systemImage: "plus")
                }
                .controlSize(.small)
            }
            .padding(.vertical, 12)
        }
    }

    // MARK: - Nhập/xuất

    private func exportFile() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "\(draft.id).json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try state.exportProfile(draft, to: url); message = "Đã xuất." } catch { message = "Lỗi: \(error.localizedDescription)" }
    }

    private func importFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try state.importProfile(from: url)
            message = "Đã nhập."
        } catch { message = "Lỗi: \(error.localizedDescription)" }
    }
}
