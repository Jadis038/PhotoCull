// 影·筛 / PhotoCull — 文件夹扫描（第一层，不解码）
import Foundation

struct ScanResult {
    var folder: URL
    var items: [PhotoItem]
    var duration: Double
    var datalessCount: Int
    var skippedCount: Int
    var readError: Error?
}

enum Scanner {
    static let filterFolderName = "筛选"

    private static let excludedExtensions: Set<String> = [
        "tmp", "part", "download", "crdownload", "db", "db-wal", "db-shm",
        "sqlite", "sqlite3", "sqlite-wal", "sqlite-shm", "ds_store", "plist"
    ]

    /// 只列第一层图片文件，排除子目录、隐藏文件、临时文件、系统占位
    static func scan(folder: URL) throws -> ScanResult {
        let start = CFAbsoluteTimeGetCurrent()
        let fm = FileManager.default

        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: folder.path, isDirectory: &isDir), isDir.boolValue else {
            throw AppError("文件夹不存在：\(folder.path)")
        }
        guard fm.isReadableFile(atPath: folder.path) else {
            throw AppError("没有读取权限：\(folder.path)")
        }

        let keys: [URLResourceKey] = [
            .isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey,
            .fileSizeKey, .contentModificationDateKey,
            .isUbiquitousItemKey, .ubiquitousItemDownloadingStatusKey
        ]

        let contents = try fm.contentsOfDirectory(
            at: folder,
            includingPropertiesForKeys: keys,
            options: [.skipsHiddenFiles]
        )

        var items: [PhotoItem] = []
        items.reserveCapacity(contents.count)
        var datalessCount = 0
        var skipped = 0

        for url in contents {
            let name = url.lastPathComponent
            if name == filterFolderName { continue }
            if name.hasPrefix("._") || name.hasPrefix("~$") { skipped += 1; continue }
            if name.hasPrefix(".") { skipped += 1; continue }

            let ext = url.pathExtension.lowercased()
            guard SupportedFormats.imageExtensions.contains(ext), !excludedExtensions.contains(ext) else {
                skipped += 1
                continue
            }

            let values = try? url.resourceValues(forKeys: Set(keys))
            if values?.isSymbolicLink == true { skipped += 1; continue }
            if values?.isRegularFile != true { skipped += 1; continue }

            var dataless = false
            if values?.isUbiquitousItem == true {
                if let status = values?.ubiquitousItemDownloadingStatus, status != .current {
                    dataless = true
                    datalessCount += 1
                }
            }

            let size = Int64(values?.fileSize ?? 0)
            let mtime = values?.contentModificationDate ?? Date.distantPast
            let item = PhotoItem(url: url, size: size, mtime: mtime, isDataless: dataless)
            items.append(item)
        }

        items.sort { NaturalSort.less($0.name, $1.name) }
        for (index, item) in items.enumerated() { item.displayIndex = index }

        let duration = (CFAbsoluteTimeGetCurrent() - start) * 1000
        return ScanResult(folder: folder, items: items, duration: duration,
                          datalessCount: datalessCount, skippedCount: skipped)
    }

    /// 按指定方式排序（稳定：同名再按自然名）
    static func sort(_ items: [PhotoItem], by key: SortKey, order: SortOrder) -> [PhotoItem] {
        let sorted = items.sorted { lhs, rhs in
            var result: ComparisonResult = .orderedSame
            switch key {
            case .name:
                result = NaturalSort.compare(lhs.name, rhs.name)
            case .captureTime:
                result = compare(lhs.sortDate, rhs.sortDate)
            case .modifiedTime:
                result = compare(lhs.mtime, rhs.mtime)
            case .size:
                if lhs.size == rhs.size { result = .orderedSame }
                else { result = lhs.size < rhs.size ? .orderedAscending : .orderedDescending }
            }
            if result == .orderedSame {
                result = NaturalSort.compare(lhs.name, rhs.name)
            }
            return order == .ascending ? result == .orderedAscending : result == .orderedDescending
        }
        return sorted
    }

    private static func compare(_ lhs: Date, _ rhs: Date) -> ComparisonResult {
        if lhs == rhs { return .orderedSame }
        return lhs < rhs ? .orderedAscending : .orderedDescending
    }
}
