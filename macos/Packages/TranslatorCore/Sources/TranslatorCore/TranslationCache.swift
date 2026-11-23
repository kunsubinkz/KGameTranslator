import CryptoKit
import Foundation
import SQLite3

public struct CachedTranslation: Sendable, Equatable, Identifiable {
    public let key: String
    public let profileId: String
    public let source: String
    public var target: String
    public let engine: String
    public let createdAt: Date
    public let hits: Int

    public var id: String { key }
}

/// Cache SQLite (file) cộng cache bộ nhớ giới hạn 500 mục.
public final class TranslationCache: @unchecked Sendable {
    public static let memoryLimit = 500

    private var db: OpaquePointer?
    private let lock = NSLock()
    private var memory: [String: CachedTranslation] = [:]
    private var memoryOrder: [String] = []

    /// `path` là file SQLite; dùng `":memory:"` cho test.
    public init(path: String) throws {
        if path != ":memory:" {
            try FileManager.default.createDirectory(
                at: URL(fileURLWithPath: path).deletingLastPathComponent(), withIntermediateDirectories: true)
        }
        guard sqlite3_open(path, &db) == SQLITE_OK else {
            throw TranslationError.unavailable("Không mở được cache: \(path)")
        }
        try exec("""
        CREATE TABLE IF NOT EXISTS translations (
            key TEXT PRIMARY KEY, profileId TEXT NOT NULL, source TEXT NOT NULL, target TEXT NOT NULL,
            engine TEXT NOT NULL, createdAt REAL NOT NULL, hits INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS idx_translations_created ON translations(createdAt DESC);
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """)
    }

    deinit { sqlite3_close(db) }

    public static func key(profileId: String, source: String) -> String {
        let digest = SHA256.hash(data: Data((profileId + "\n" + source.lowercased()).utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    public func lookup(profileId: String, source: String) -> CachedTranslation? {
        let key = Self.key(profileId: profileId, source: source)
        lock.lock(); defer { lock.unlock() }
        if let hit = memory[key] {
            bumpHits(key)
            return hit
        }
        guard let row = queryRows("SELECT key, profileId, source, target, engine, createdAt, hits FROM translations WHERE key = ?",
                                  binds: [key]).first else { return nil }
        remember(row)
        bumpHits(key)
        return row
    }

    public func store(profileId: String, source: String, target: String, engine: String) {
        let key = Self.key(profileId: profileId, source: source)
        let entry = CachedTranslation(key: key, profileId: profileId, source: source, target: target,
                                      engine: engine, createdAt: Date(), hits: 0)
        lock.lock(); defer { lock.unlock() }
        run("""
        INSERT INTO translations (key, profileId, source, target, engine, createdAt, hits) VALUES (?, ?, ?, ?, ?, ?, 0)
        ON CONFLICT(key) DO UPDATE SET target = excluded.target, engine = excluded.engine
        """, binds: [key, profileId, source, target, engine, entry.createdAt.timeIntervalSince1970])
        remember(entry)
    }

    /// Sửa bản dịch trong màn hình lịch sử; mặc định đánh dấu là sửa tay.
    public func updateTarget(key: String, target: String, engine: String = "manual") {
        lock.lock(); defer { lock.unlock() }
        run("UPDATE translations SET target = ?, engine = ? WHERE key = ?", binds: [target, engine, key])
        memory[key] = nil
        memoryOrder.removeAll { $0 == key }
    }

    public func history(profileId: String? = nil, limit: Int = 200) -> [CachedTranslation] {
        lock.lock(); defer { lock.unlock() }
        let columns = "SELECT key, profileId, source, target, engine, createdAt, hits FROM translations"
        if let profileId {
            return queryRows(columns + " WHERE profileId = ? ORDER BY createdAt DESC LIMIT ?", binds: [profileId, limit])
        }
        return queryRows(columns + " ORDER BY createdAt DESC LIMIT ?", binds: [limit])
    }

    /// Số câu đã lưu và tổng số lần dùng lại từ cache.
    public func stats(profileId: String) -> (count: Int, hits: Int) {
        lock.lock(); defer { lock.unlock() }
        var result = (count: 0, hits: 0)
        query("SELECT COUNT(*), COALESCE(SUM(hits), 0) FROM translations WHERE profileId = ?", binds: [profileId]) { stmt in
            result = (Int(sqlite3_column_int64(stmt, 0)), Int(sqlite3_column_int64(stmt, 1)))
        }
        return result
    }

    public func clear(profileId: String) {
        lock.lock(); defer { lock.unlock() }
        run("DELETE FROM translations WHERE profileId = ?", binds: [profileId])
        memory.removeAll()
        memoryOrder.removeAll()
    }

    public func setMeta(_ key: String, _ value: String) {
        lock.lock(); defer { lock.unlock() }
        run("INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            binds: [key, value])
    }

    public func meta(_ key: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        var result: String?
        query("SELECT value FROM meta WHERE key = ?", binds: [key]) { stmt in
            result = String(cString: sqlite3_column_text(stmt, 0))
        }
        return result
    }

    // MARK: - Nội bộ (đã giữ lock)

    private func remember(_ entry: CachedTranslation) {
        if memory[entry.key] == nil { memoryOrder.append(entry.key) }
        memory[entry.key] = entry
        while memoryOrder.count > Self.memoryLimit {
            memory[memoryOrder.removeFirst()] = nil
        }
    }

    private func bumpHits(_ key: String) {
        run("UPDATE translations SET hits = hits + 1 WHERE key = ?", binds: [key])
    }

    private func exec(_ sql: String) throws {
        guard sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK else {
            throw TranslationError.unavailable(String(cString: sqlite3_errmsg(db)))
        }
    }

    private func run(_ sql: String, binds: [Any]) {
        query(sql, binds: binds) { _ in }
    }

    private func queryRows(_ sql: String, binds: [Any]) -> [CachedTranslation] {
        var rows: [CachedTranslation] = []
        query(sql, binds: binds) { stmt in
            rows.append(CachedTranslation(
                key: String(cString: sqlite3_column_text(stmt, 0)),
                profileId: String(cString: sqlite3_column_text(stmt, 1)),
                source: String(cString: sqlite3_column_text(stmt, 2)),
                target: String(cString: sqlite3_column_text(stmt, 3)),
                engine: String(cString: sqlite3_column_text(stmt, 4)),
                createdAt: Date(timeIntervalSince1970: sqlite3_column_double(stmt, 5)),
                hits: Int(sqlite3_column_int(stmt, 6))))
        }
        return rows
    }

    private func query(_ sql: String, binds: [Any], row: (OpaquePointer) -> Void) {
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else { return }
        defer { sqlite3_finalize(stmt) }
        let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        for (index, value) in binds.enumerated() {
            let i = Int32(index + 1)
            switch value {
            case let v as String: sqlite3_bind_text(stmt, i, v, -1, transient)
            case let v as Int: sqlite3_bind_int64(stmt, i, Int64(v))
            case let v as Double: sqlite3_bind_double(stmt, i, v)
            default: sqlite3_bind_null(stmt, i)
            }
        }
        while sqlite3_step(stmt) == SQLITE_ROW { row(stmt) }
    }
}
