// 影·筛 / PhotoCull — macOS 共享基础工具
// 开源协议：MIT
import Foundation
import CryptoKit
import CoreGraphics

enum AppInfo {
    /// 用户可见名称使用 U+00B7 中间点
    static let name = "影·筛"
    static let latinName = "PhotoCull"
    static let version = "1.0.2"
    static let build = "1"
    static let bundleIdentifier = "com.jadis038.photocull"
    static let copyright = "© 2026 Галин 与影·筛贡献者"
    static let aiCredit = "由 DeepSeek / GLM 系列 AI 协作开发"
    static let author = "Галин"
    static let iconCredit = "图标：Ho Wing Own（YSLGOG）"

    /// 数据目录：~/Library/Application Support/影·筛
    /// 可用环境变量 YING_SHAI_DATA 覆盖（自动化测试用）
    static var dataDirectory: URL {
        if let custom = ProcessInfo.processInfo.environment["YING_SHAI_DATA"], !custom.isEmpty {
            return URL(fileURLWithPath: custom, isDirectory: true)
        }
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support", isDirectory: true)
        return base.appendingPathComponent("影·筛", isDirectory: true)
    }

    static var stateDirectory: URL { dataDirectory.appendingPathComponent("state", isDirectory: true) }
    static var cacheDirectory: URL { dataDirectory.appendingPathComponent("cache", isDirectory: true) }
    static var thumbnailCacheDirectory: URL { cacheDirectory.appendingPathComponent("thumbs", isDirectory: true) }
    static var logFileURL: URL { dataDirectory.appendingPathComponent("photocull.log") }

    static func ensureDirectories() {
        let fm = FileManager.default
        for url in [dataDirectory, stateDirectory, cacheDirectory, thumbnailCacheDirectory] {
            if !fm.fileExists(atPath: url.path) {
                try? fm.createDirectory(at: url, withIntermediateDirectories: true)
            }
        }
    }
}

// MARK: - 日志

enum Log {
    private static let queue = DispatchQueue(label: "com.jadis038.photocull.log")
    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return f
    }()

    static func info(_ message: String, file: String = #file, line: Int = #line) {
        let name = (file as NSString).lastPathComponent
        let text = "[\(formatter.string(from: Date()))] [\(name):\(line)] \(message)\n"
        queue.async {
            FileHandle.standardError.write(text.data(using: .utf8) ?? Data())
            if let data = text.data(using: .utf8) {
                if let handle = try? FileHandle(forWritingTo: AppInfo.logFileURL) {
                    handle.seekToEndOfFile()
                    handle.write(data)
                    try? handle.close()
                } else {
                    try? data.write(to: AppInfo.logFileURL)
                }
            }
        }
    }
}

// MARK: - 错误

struct AppError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

// MARK: - 哈希 / 路径

enum Hashing {
    static func sha256(_ string: String) -> String {
        let digest = SHA256.hash(data: Data(string.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    static func shortHash(_ string: String, length: Int = 16) -> String {
        String(sha256(string).prefix(length))
    }
}

// MARK: - 自然排序

enum NaturalSort {
    /// 文件名自然排序：IMG_2 < IMG_10，大小写不敏感，中文与宽字符按 Finder 习惯
    static func compare(_ lhs: String, _ rhs: String) -> ComparisonResult {
        let result = lhs.localizedStandardCompare(rhs)
        if result != .orderedSame { return result }
        return lhs.compare(rhs, options: [.caseInsensitive, .widthInsensitive], range: nil, locale: Locale(identifier: "en_US_POSIX"))
    }

    static func less(_ lhs: String, _ rhs: String) -> Bool {
        compare(lhs, rhs) == .orderedAscending
    }
}

// MARK: - 格式化

enum Fmt {
    static func bytes(_ value: Int64) -> String {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        formatter.allowedUnits = [.useKB, .useMB, .useGB]
        formatter.isAdaptive = true
        return formatter.string(fromByteCount: value)
    }

    static func count(_ value: Int) -> String {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        f.locale = Locale(identifier: "zh_CN")
        return f.string(from: NSNumber(value: value)) ?? "\(value)"
    }

    static func duration(ms: Double) -> String {
        if ms < 1000 { return String(format: "%.0f ms", ms) }
        return String(format: "%.2f s", ms / 1000)
    }
}

// MARK: - 文件工具

enum FileTools {
    static func isReadableFile(_ url: URL) -> Bool {
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), !isDir.boolValue else { return false }
        return FileManager.default.isReadableFile(atPath: url.path)
    }

    static func isDirectory(_ url: URL) -> Bool {
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir) else { return false }
        return isDir.boolValue
    }

    static func fileSize(_ url: URL) -> Int64 {
        (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize).flatMap { Int64($0) } ?? 0
    }

    static func modificationDate(_ url: URL) -> Date {
        (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? Date.distantPast
    }

    static func availableCapacity(at url: URL) -> Int64? {
        let values = try? url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        return values?.volumeAvailableCapacityForImportantUsage
    }

    /// 同一卷上的快速复制（APFS 上会走 clonefile），失败时关闭进度
    @discardableResult
    static func copyFile(from source: URL, to destination: URL) throws -> Int64 {
        let fm = FileManager.default
        if fm.fileExists(atPath: destination.path) {
            try fm.removeItem(at: destination)
        }
        try fm.copyItem(at: source, to: destination)
        return fileSize(destination)
    }

    static func ensureDirectory(_ url: URL) throws {
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir) {
            if !isDir.boolValue { throw AppError("目标路径不是文件夹：\(url.path)") }
            return
        }
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    }

    /// 目录内文件列表（不递归）
    static func listFiles(in directory: URL) -> [URL] {
        (try? FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey],
            options: [.skipsHiddenFiles]
        )) ?? []
    }

    /// 永不覆盖的同名冲突改名：原名 (1).ext
    static func conflictFreeURL(for desired: URL, reserved: Set<String> = []) -> URL {
        let fm = FileManager.default
        if !fm.fileExists(atPath: desired.path) && !reserved.contains(desired.path) {
            return desired
        }
        let dir = desired.deletingLastPathComponent()
        let ext = desired.pathExtension
        let base = desired.deletingPathExtension().lastPathComponent
        var index = 1
        while index < 10000 {
            let name = ext.isEmpty ? "\(base) (\(index))" : "\(base) (\(index)).\(ext)"
            let candidate = dir.appendingPathComponent(name)
            if !fm.fileExists(atPath: candidate.path) && !reserved.contains(candidate.path) {
                return candidate
            }
            index += 1
        }
        return dir.appendingPathComponent("\(base) (\(UUID().uuidString)).\(ext)")
    }

    /// 递归删除（仅用于临时文件）
    static func removeIfExists(_ url: URL) {
        try? FileManager.default.removeItem(at: url)
    }

    static func touch(_ url: URL) {
        try? FileManager.default.setAttributes([.modificationDate: Date()], ofItemAtPath: url.path)
    }

    /// 打开 Finder 并选中
    static func revealInFinder(_ url: URL) {
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }
}

// 需要 AppKit 的 NSWorkspace
#if canImport(AppKit)
import AppKit
#endif

// MARK: - 媒体类型

enum MediaKind: String {
    case raw, jpeg, heic, tiff, png, webp, avif, jxl, exr, bmp, gif, other

    var isRaw: Bool { self == .raw }

    var displayName: String {
        switch self {
        case .raw: return "RAW"
        case .jpeg: return "JPEG"
        case .heic: return "HEIF"
        case .tiff: return "TIFF"
        case .png: return "PNG"
        case .webp: return "WebP"
        case .avif: return "AVIF"
        case .jxl: return "JPEG XL"
        case .exr: return "OpenEXR"
        case .bmp: return "BMP"
        case .gif: return "GIF"
        case .other: return "图片"
        }
    }

    static func from(extension ext: String) -> MediaKind {
        switch ext {
        case "nef", "cr2", "cr3", "arw", "raf", "orf", "rw2", "pef", "dng", "srw",
             "x3f", "raw", "3fr", "erf", "mef", "mos", "mrw", "nrw", "rwl", "kdc",
             "dcr", "iiq", "cap", "srf", "sr2":
            return .raw
        case "jpg", "jpeg", "jpe", "jfif":
            return .jpeg
        case "heic", "heif", "hif":
            return .heic
        case "tif", "tiff":
            return .tiff
        case "png":
            return .png
        case "webp":
            return .webp
        case "avif":
            return .avif
        case "jxl":
            return .jxl
        case "exr":
            return .exr
        case "bmp":
            return .bmp
        case "gif":
            return .gif
        default:
            return .other
        }
    }

    /// RAW 品牌标注（信息栏用）
    static func rawBrandLabel(ext: String) -> String? {
        switch ext {
        case "nef": return "尼康 NEF"
        case "cr2", "cr3": return "佳能 CR2/CR3"
        case "arw", "srf", "sr2": return "索尼 ARW"
        case "raf": return "富士 RAF"
        case "orf": return "奥林巴斯 ORF"
        case "rw2", "raw": return "松下 RW2"
        case "pef": return "宾得 PEF"
        case "dng": return "DNG"
        case "srw": return "三星 SRW"
        case "x3f": return "适马 X3F"
        default: return nil
        }
    }
}

enum SupportedFormats {
    /// 扫描白名单（第一层图片文件）
    static let imageExtensions: Set<String> = [
        // 常见图片
        "jpg", "jpeg", "jpe", "jfif", "png", "tif", "tiff", "bmp", "gif", "webp", "heic", "heif", "hif", "avif",
        "jxl", "exr",
        // RAW（ImageIO / 系统 RAW 支持）
        "nef", "cr2", "cr3", "arw", "raf", "orf", "rw2", "pef", "dng", "srw", "x3f",
        "raw", "3fr", "erf", "mef", "mos", "mrw", "nrw", "rwl", "kdc", "dcr", "iiq", "cap", "srf", "sr2"
    ]
}

// MARK: - 排序方式

enum SortKey: String, CaseIterable {
    case name
    case captureTime
    case modifiedTime
    case size

    var label: String {
        switch self {
        case .name: return "文件名"
        case .captureTime: return "拍摄时间"
        case .modifiedTime: return "修改时间"
        case .size: return "文件大小"
        }
    }
}

enum SortOrder: String, CaseIterable {
    case ascending
    case descending
    var label: String { self == .ascending ? "升序" : "降序" }
}

// MARK: - 标记方式

enum OperationMode: String, Codable, CaseIterable {
    case copy
    case move

    var label: String { self == .copy ? "复制" : "移动" }
    var verb: String { self == .copy ? "复制" : "移动" }
}
