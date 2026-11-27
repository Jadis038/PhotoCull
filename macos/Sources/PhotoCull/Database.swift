// 影·筛 / PhotoCull — SQLite（WAL）状态索引与缓存索引
import Foundation
import SQLite3

private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

enum SQLValue {
    case null
    case int(Int64)
    case double(Double)
    case text(String)

    var intValue: Int64? {
        switch self {
        case .int(let value): return value
        case .double(let value): return Int64(value)
        case .text(let value): return Int64(value)
        case .null: return nil
        }
    }

    var doubleValue: Double? {
        switch self {
        case .int(let value): return Double(value)
        case .double(let value): return value
        case .text(let value): return Double(value)
        case .null: return nil
        }
    }

    var stringValue: String? {
        switch self {
        case .text(let value): return value
        case .int(let value): return String(value)
        case .double(let value): return String(value)
        case .null: return nil
        }
    }
}

final class SQLiteDatabase {
    private var handle: OpaquePointer?
    private let lock = NSRecursiveLock()

    init(path: URL, readOnly: Bool = false) throws {
        let flags = readOnly ? SQLITE_OPEN_READONLY : (SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX)
        let result = sqlite3_open_v2(path.path, &handle, flags, nil)
        guard result == SQLITE_OK, handle != nil else {
            let message = handle.flatMap { String(cString: sqlite3_errmsg($0)) } ?? "无法打开数据库"
            if handle != nil { sqlite3_close(handle) }
            throw AppError("打开数据库失败：\(message)")
        }
        sqlite3_busy_timeout(handle, 8000)
        try execute("PRAGMA journal_mode=WAL;")
        try execute("PRAGMA synchronous=NORMAL;")
        try execute("PRAGMA foreign_keys=ON;")
    }

    deinit {
        sqlite3_close(handle)
    }

    func execute(_ sql: String, params: [Any?] = []) throws {
        lock.lock()
        defer { lock.unlock() }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(handle, sql, -1, &statement, nil) == SQLITE_OK else {
            throw AppError("SQL 准备失败：\(lastErrorMessage()) SQL=\(sql)")
        }
        defer { sqlite3_finalize(statement) }
        try bind(statement: statement, params: params)
        let stepResult = sqlite3_step(statement)
        guard stepResult == SQLITE_DONE || stepResult == SQLITE_ROW else {
            throw AppError("SQL 执行失败：\(lastErrorMessage()) SQL=\(sql)")
        }
    }

    @discardableResult
    func query(_ sql: String, params: [Any?] = [], row: ([String: SQLValue]) -> Void) throws -> Int {
        lock.lock()
        defer { lock.unlock() }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(handle, sql, -1, &statement, nil) == SQLITE_OK else {
            throw AppError("SQL 准备失败：\(lastErrorMessage()) SQL=\(sql)")
        }
        defer { sqlite3_finalize(statement) }
        try bind(statement: statement, params: params)

        var count = 0
        while true {
            let stepResult = sqlite3_step(statement)
            if stepResult == SQLITE_ROW {
                var dictionary: [String: SQLValue] = [:]
                for index in 0..<sqlite3_column_count(statement) {
                    let name = String(cString: sqlite3_column_name(statement, index))
                    switch sqlite3_column_type(statement, index) {
                    case SQLITE_INTEGER:
                        dictionary[name] = .int(sqlite3_column_int64(statement, index))
                    case SQLITE_FLOAT:
                        dictionary[name] = .double(sqlite3_column_double(statement, index))
                    case SQLITE_TEXT:
                        if let text = sqlite3_column_text(statement, index) {
                            dictionary[name] = .text(String(cString: text))
                        } else {
                            dictionary[name] = .null
                        }
                    default:
                        dictionary[name] = .null
                    }
                }
                row(dictionary)
                count += 1
            } else if stepResult == SQLITE_DONE {
                break
            } else {
                throw AppError("SQL 读取失败：\(lastErrorMessage())")
            }
        }
        return count
    }

    func transaction(_ body: () throws -> Void) throws {
        try execute("BEGIN IMMEDIATE;")
        do {
            try body()
            try execute("COMMIT;")
        } catch {
            try? execute("ROLLBACK;")
            throw error
        }
    }

    private func bind(statement: OpaquePointer?, params: [Any?]) throws {
        for (offset, value) in params.enumerated() {
            let index = Int32(offset + 1)
            switch value {
            case nil:
                sqlite3_bind_null(statement, index)
            case let value as Int:
                sqlite3_bind_int64(statement, index, Int64(value))
            case let value as Int64:
                sqlite3_bind_int64(statement, index, value)
            case let value as Double:
                sqlite3_bind_double(statement, index, value)
            case let value as Date:
                sqlite3_bind_double(statement, index, value.timeIntervalSince1970)
            case let value as Bool:
                sqlite3_bind_int64(statement, index, value ? 1 : 0)
            case let value as String:
                sqlite3_bind_text(statement, index, value, -1, SQLITE_TRANSIENT)
            default:
                sqlite3_bind_text(statement, index, "\(value!)", -1, SQLITE_TRANSIENT)
            }
        }
    }

    private func lastErrorMessage() -> String {
        handle.flatMap { String(cString: sqlite3_errmsg($0)) } ?? "未知错误"
    }
}

// MARK: - 每个文件夹的状态索引

final class StateStore {
    let folder: URL
    let databasePath: URL
    private let database: SQLiteDatabase

    init(folder: URL) throws {
        self.folder = folder.standardizedFileURL
        AppInfo.ensureDirectories()
        let hash = Hashing.shortHash("folder|" + folder.standardizedFileURL.path, length: 24)
        databasePath = AppInfo.stateDirectory.appendingPathComponent("\(hash).sqlite3")
        database = try SQLiteDatabase(path: databasePath)
        try migrate()
    }

    private func migrate() throws {
        try database.execute("""
            CREATE TABLE IF NOT EXISTS records (
                path TEXT PRIMARY KEY,
                source_size INTEGER NOT NULL,
                source_mtime REAL NOT NULL,
                copy_path TEXT NOT NULL,
                copy_size INTEGER NOT NULL,
                copy_mtime REAL NOT NULL,
                mode TEXT NOT NULL,
                marked_at REAL NOT NULL,
                companions TEXT NOT NULL DEFAULT '[]'
            );
        """)
        try database.execute("""
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """)
        try database.execute("INSERT OR REPLACE INTO meta(key, value) VALUES (?, ?);",
                             params: ["folder", folder.path])
    }

    func loadAll() -> [String: MarkRecord] {
        var result: [String: MarkRecord] = [:]
        let decoder = JSONDecoder()
        do {
            try database.query("SELECT * FROM records;") { row in
                guard let path = row["path"]?.stringValue,
                      let copyPath = row["copy_path"]?.stringValue,
                      let modeText = row["mode"]?.stringValue,
                      let mode = OperationMode(rawValue: modeText) else { return }
                var companions: [MarkRecord.Companion] = []
                if let companionsText = row["companions"]?.stringValue,
                   let data = companionsText.data(using: .utf8) {
                    companions = (try? decoder.decode([MarkRecord.Companion].self, from: data)) ?? []
                }
                let record = MarkRecord(
                    sourcePath: path,
                    sourceSize: row["source_size"]?.intValue ?? 0,
                    sourceMTime: row["source_mtime"]?.doubleValue ?? 0,
                    copyPath: copyPath,
                    copySize: row["copy_size"]?.intValue ?? 0,
                    copyMTime: row["copy_mtime"]?.doubleValue ?? 0,
                    mode: mode,
                    markedAt: row["marked_at"]?.doubleValue ?? 0,
                    companions: companions
                )
                result[path] = record
            }
        } catch {
            Log.info("读取状态索引失败：\(error.localizedDescription)")
        }
        return result
    }

    func save(_ record: MarkRecord) throws {
        let encoder = JSONEncoder()
        let companionsData = (try? encoder.encode(record.companions)) ?? Data()
        let companionsText = String(data: companionsData, encoding: .utf8) ?? "[]"
        try database.execute("""
            INSERT OR REPLACE INTO records
            (path, source_size, source_mtime, copy_path, copy_size, copy_mtime, mode, marked_at, companions)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);
        """, params: [
            record.sourcePath, record.sourceSize, record.sourceMTime,
            record.copyPath, record.copySize, record.copyMTime,
            record.mode.rawValue, record.markedAt, companionsText
        ])
    }

    func remove(sourcePath: String) throws {
        try database.execute("DELETE FROM records WHERE path = ?;", params: [sourcePath])
    }

    func clear() throws {
        try database.execute("DELETE FROM records;")
    }

    /// 索引失效/丢失时，按文件名匹配「筛选」目录中的副本重建
    func rebuildFromFilterFolder(items: [PhotoItem]) -> [String: MarkRecord] {
        let filterFolder = folder.appendingPathComponent(Scanner.filterFolderName, isDirectory: true)
        guard FileTools.isDirectory(filterFolder) else { return [:] }

        let files = FileTools.listFiles(in: filterFolder).filter { SupportedFormats.imageExtensions.contains($0.pathExtension.lowercased()) }
        guard !files.isEmpty else { return [:] }

        var byLowerName: [String: URL] = [:]
        for file in files { byLowerName[file.lastPathComponent.lowercased()] = file }

        var result: [String: MarkRecord] = [:]
        for item in items {
            guard let copyURL = byLowerName[item.name.lowercased()] else { continue }
            let copySize = FileTools.fileSize(copyURL)
            let copyMTime = FileTools.modificationDate(copyURL).timeIntervalSince1970
            var companions: [MarkRecord.Companion] = []
            let base = copyURL.deletingPathExtension().lastPathComponent.lowercased()
            for file in files where file != copyURL {
                let fileBase = file.deletingPathExtension().lastPathComponent.lowercased()
                if fileBase == base {
                    companions.append(MarkRecord.Companion(sourcePath: file.path, copyPath: file.path))
                }
            }
            let record = MarkRecord(
                sourcePath: item.url.path,
                sourceSize: item.size,
                sourceMTime: item.mtime.timeIntervalSince1970,
                copyPath: copyURL.path,
                copySize: copySize,
                copyMTime: copyMTime,
                mode: .copy,
                markedAt: Date().timeIntervalSince1970,
                companions: companions
            )
            result[item.url.path] = record
            do { try save(record) } catch { Log.info("重建索引写入失败：\(error.localizedDescription)") }
        }
        return result
    }

    func close() {
        // SQLiteDatabase 在析构时关闭
    }
}

// MARK: - 缩略图磁盘缓存索引

final class CacheIndex {
    private var database: SQLiteDatabase?
    private let lock = NSLock()

    init() {
        AppInfo.ensureDirectories()
        do {
            let db = try SQLiteDatabase(path: AppInfo.cacheDirectory.appendingPathComponent("cache.sqlite3"))
            try db.execute("""
                CREATE TABLE IF NOT EXISTS cache (
                    key TEXT PRIMARY KEY,
                    kind TEXT NOT NULL,
                    path TEXT NOT NULL,
                    bytes INTEGER NOT NULL,
                    created REAL NOT NULL,
                    accessed REAL NOT NULL
                );
            """)
            try db.execute("CREATE INDEX IF NOT EXISTS idx_cache_accessed ON cache(accessed);")
            database = db
        } catch {
            Log.info("缓存索引初始化失败：\(error.localizedDescription)")
            database = nil
        }
    }

    func record(key: String, kind: String, path: String, bytes: Int64) {
        guard let database else { return }
        let now = Date().timeIntervalSince1970
        lock.lock()
        defer { lock.unlock() }
        do {
            try database.execute("""
                INSERT OR REPLACE INTO cache(key, kind, path, bytes, created, accessed)
                VALUES (?, ?, ?, ?, COALESCE((SELECT created FROM cache WHERE key = ?), ?), ?);
            """, params: [key, kind, path, bytes, key, now, now])
        } catch {
            Log.info("缓存索引写入失败：\(error.localizedDescription)")
        }
    }

    func touch(key: String) {
        guard let database else { return }
        lock.lock()
        defer { lock.unlock() }
        try? database.execute("UPDATE cache SET accessed = ? WHERE key = ?;",
                              params: [Date().timeIntervalSince1970, key])
    }

    func remove(key: String) {
        guard let database else { return }
        lock.lock()
        defer { lock.unlock() }
        try? database.execute("DELETE FROM cache WHERE key = ?;", params: [key])
    }

    func totalBytes() -> Int64 {
        guard let database else { return 0 }
        var total: Int64 = 0
        lock.lock()
        defer { lock.unlock() }
        do {
            try database.query("SELECT COALESCE(SUM(bytes), 0) AS total FROM cache;") { row in
                total = row["total"]?.intValue ?? 0
            }
        } catch {
            Log.info("读取缓存体积失败：\(error.localizedDescription)")
        }
        return total
    }

    /// 返回最久未访问的条目（key 与文件路径）
    func oldestEntries(limit: Int) -> [(key: String, path: String, bytes: Int64)] {
        guard let database else { return [] }
        var result: [(String, String, Int64)] = []
        lock.lock()
        defer { lock.unlock() }
        do {
            try database.query("SELECT key, path, bytes FROM cache ORDER BY accessed ASC LIMIT ?;",
                               params: [limit]) { row in
                guard let key = row["key"]?.stringValue, let path = row["path"]?.stringValue else { return }
                result.append((key, path, row["bytes"]?.intValue ?? 0))
            }
        } catch {
            Log.info("读取缓存 LRU 失败：\(error.localizedDescription)")
        }
        return result
    }

    func clear() {
        guard let database else { return }
        lock.lock()
        defer { lock.unlock() }
        try? database.execute("DELETE FROM cache;")
    }
}
