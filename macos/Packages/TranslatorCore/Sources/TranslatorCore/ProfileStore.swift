import Foundation

/// Lưu profile dạng JSON, mỗi profile một file `<id>.json`.
public struct ProfileStore: Sendable {
    public let directory: URL

    public init(directory: URL) {
        self.directory = directory
    }

    public static var applicationSupport: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("GameTranslator", isDirectory: true)
    }

    public static func standard() -> ProfileStore {
        ProfileStore(directory: applicationSupport.appendingPathComponent("Profiles", isDirectory: true))
    }

    private var encoder: JSONEncoder {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        return e
    }

    /// Đảm bảo thư mục tồn tại và có ít nhất các profile mẫu.
    public func seedIfNeeded() throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        if try list().isEmpty {
            try save(.default)
            try save(.zeldaTOTK)
        }
    }

    public func list() throws -> [Profile] {
        guard FileManager.default.fileExists(atPath: directory.path) else { return [] }
        let files = try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
        let decoder = JSONDecoder()
        return files
            .filter { $0.pathExtension == "json" }
            .compactMap { try? decoder.decode(Profile.self, from: Data(contentsOf: $0)) }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    public func load(id: String) -> Profile? {
        try? JSONDecoder().decode(Profile.self, from: Data(contentsOf: fileURL(for: id)))
    }

    public func save(_ profile: Profile) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try encoder.encode(profile).write(to: fileURL(for: profile.id), options: .atomic)
    }

    public func delete(id: String) throws {
        try FileManager.default.removeItem(at: fileURL(for: id))
    }

    public func export(_ profile: Profile, to url: URL) throws {
        try encoder.encode(profile).write(to: url, options: .atomic)
    }

    /// Nhập profile từ file JSON; nếu trùng id thì ghi đè.
    @discardableResult
    public func importProfile(from url: URL) throws -> Profile {
        let profile = try JSONDecoder().decode(Profile.self, from: Data(contentsOf: url))
        try save(profile)
        return profile
    }

    /// Chuyển tên bất kỳ thành id an toàn làm tên file.
    public static func makeID(from name: String) -> String {
        let folded = name.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: .current)
            .replacingOccurrences(of: "đ", with: "d")
        let slug = folded.unicodeScalars.map { CharacterSet.alphanumerics.contains($0) ? Character($0) : "-" }
        let result = String(slug).split(separator: "-").joined(separator: "-")
        return result.isEmpty ? "profile-\(Int(Date().timeIntervalSince1970))" : result
    }

    private func fileURL(for id: String) -> URL {
        let safe = id.replacingOccurrences(of: "/", with: "_")
        return directory.appendingPathComponent("\(safe).json")
    }
}
