// 影·筛 / PhotoCull — 标记引擎：单张即时复制/移动、取消、回收站、撤销
import Foundation
import AppKit

/// 撤销动作（内存栈，容量 2000）
enum UndoAction {
    case undoMarkCopy(record: MarkRecord)
    case undoMarkMove(record: MarkRecord)
    case undoUnmarkCopy(record: MarkRecord, trashURLs: [String: String])
    case undoUnmarkMove(record: MarkRecord)
}

struct UndoResult {
    let action: UndoAction
    let record: MarkRecord?
    let error: String?
}

enum MarkEngineError: LocalizedError {
    case sourceMissing(String)
    case sourceUnreadable(String)
    case targetExists(String)
    case notEnoughSpace(needed: Int64, available: Int64)
    case copyFailed(String)
    case trashFailed(String)
    case cancelled

    var errorDescription: String? {
        switch self {
        case .sourceMissing(let path): return "源文件已不存在：\(path)"
        case .sourceUnreadable(let path): return "源文件不可读：\(path)"
        case .targetExists(let path): return "目标文件已存在：\(path)"
        case .notEnoughSpace(let needed, let available):
            return "磁盘剩余空间不足：需要约 \(Fmt.bytes(needed))，可用 \(Fmt.bytes(available))"
        case .copyFailed(let message): return "复制失败：\(message)"
        case .trashFailed(let message): return "移入回收站失败：\(message)"
        case .cancelled: return "操作已取消"
        }
    }
}

final class MarkEngine {
    let folder: URL
    let stateStore: StateStore

    private let operationQueue = DispatchQueue(label: "com.jadis038.photocull.mark", qos: .userInitiated)
    private let lock = NSLock()
    private var tokens: [UUID: MarkToken] = [:]
    private var tokenPaths: [UUID: String] = [:]

    /// 主线程回调
    var onStateChange: ((String, MarkState) -> Void)?
    var onRecordChanged: ((String, MarkRecord?) -> Void)?
    var onError: ((String, String) -> Void)?
    var onQueueCountChanged: ((Int) -> Void)?
    var onUndoAvailable: ((UndoAction) -> Void)?
    var onBusyChanged: ((Bool) -> Void)?
    var onUndoResult: ((UndoResult) -> Void)?

    var defaultMode: OperationMode = .copy
    var filterFolder: URL { folder.appendingPathComponent(Scanner.filterFolderName, isDirectory: true) }

    var queueCount: Int {
        lock.lock(); defer { lock.unlock() }
        return tokens.count
    }

    init(folder: URL, stateStore: StateStore) {
        self.folder = folder
        self.stateStore = stateStore
    }

    // MARK: - 入队

    @discardableResult
    func enqueueMark(item: PhotoItem, mode: OperationMode) -> UUID? {
        enqueue(path: item.currentURL.path, kind: .mark, item: item, mode: mode, record: nil)
    }

    @discardableResult
    func enqueueUnmark(item: PhotoItem, record: MarkRecord) -> UUID? {
        enqueue(path: record.sourcePath, kind: .unmark, item: item, mode: record.mode, record: record)
    }

    private enum TaskKind { case mark, unmark }

    private func enqueue(path: String, kind: TaskKind, item: PhotoItem, mode: OperationMode, record: MarkRecord?) -> UUID? {
        let token = MarkToken()
        let id = token.id
        lock.lock()
        tokens[id] = token
        tokenPaths[id] = path
        let count = tokens.count
        lock.unlock()
        notifyQueueCount(count)
        notifyBusy(count > 0)

        operationQueue.async { [weak self] in
            guard let self else { return }
            defer { self.finishToken(id) }
            if token.cancelled {
                DispatchQueue.main.async { self.onStateChange?(path, .none) }
                return
            }
            do {
                switch kind {
                case .mark:
                    try self.performMark(item: item, mode: mode, token: token)
                case .unmark:
                    guard let record else { return }
                    try self.performUnmark(item: item, record: record, token: token)
                }
            } catch let error as MarkEngineError {
                if case .cancelled = error {
                    DispatchQueue.main.async { self.onStateChange?(path, .none) }
                } else {
                    self.reportFailure(path: path, message: error.localizedDescription)
                }
            } catch {
                self.reportFailure(path: path, message: error.localizedDescription)
            }
        }
        return id
    }

    func cancel(path: String) {
        lock.lock()
        for (id, tokenPath) in tokenPaths where tokenPath == path { tokens[id]?.cancelled = true }
        lock.unlock()
    }

    func cancelAll() {
        lock.lock()
        for (id, _) in tokens { tokens[id]?.cancelled = true }
        lock.unlock()
    }

    private func finishToken(_ id: UUID) {
        lock.lock()
        tokens.removeValue(forKey: id)
        tokenPaths.removeValue(forKey: id)
        let count = tokens.count
        lock.unlock()
        notifyQueueCount(count)
        notifyBusy(count > 0)
    }

    private func notifyQueueCount(_ count: Int) {
        DispatchQueue.main.async { self.onQueueCountChanged?(count) }
    }

    private func notifyBusy(_ busy: Bool) {
        DispatchQueue.main.async { self.onBusyChanged?(busy) }
    }

    private func reportFailure(path: String, message: String) {
        DispatchQueue.main.async {
            self.onStateChange?(path, .none)
            self.onError?(path, message)
        }
    }

    // MARK: - 标记（复制/移动）

    private func performMark(item: PhotoItem, mode: OperationMode, token: MarkToken) throws {
        let source = item.currentURL
        guard FileManager.default.fileExists(atPath: source.path) else {
            throw MarkEngineError.sourceMissing(source.path)
        }
        guard FileManager.default.isReadableFile(atPath: source.path) else {
            throw MarkEngineError.sourceUnreadable(source.path)
        }

        try FileTools.ensureDirectory(filterFolder)

        var sources: [URL] = [source]
        sources.append(contentsOf: companionSources(for: source))

        // 同组统一冲突后缀：(1)、(2)…绝不覆盖
        let targets = groupedTargets(for: sources, in: filterFolder)

        if mode == .copy {
            let needed = sources.reduce(Int64(0)) { $0 + FileTools.fileSize($1) }
            let required = Int64(Double(needed) * 1.05) + 64 * 1024 * 1024
            if let available = FileTools.availableCapacity(at: filterFolder), available < required {
                throw MarkEngineError.notEnoughSpace(needed: required, available: available)
            }
        }

        DispatchQueue.main.async { self.onStateChange?(item.currentURL.path, .processing) }

        var createdTargets: [URL] = []
        var movedPairs: [(source: URL, target: URL)] = []

        do {
            for (index, sourceURL) in sources.enumerated() {
                if token.cancelled { throw MarkEngineError.cancelled }
                let target = targets[index]
                let partURL = URL(fileURLWithPath: target.path + ".part")

                if mode == .copy {
                    FileTools.removeIfExists(partURL)
                    try FileManager.default.copyItem(at: sourceURL, to: partURL)
                    if token.cancelled {
                        FileTools.removeIfExists(partURL)
                        throw MarkEngineError.cancelled
                    }
                    if FileManager.default.fileExists(atPath: target.path) {
                        // 理论上前面已经选好不冲突的名字；极端竞态下再改一次
                        let renamed = FileTools.conflictFreeURL(for: target)
                        try FileManager.default.moveItem(at: partURL, to: renamed)
                        createdTargets.append(renamed)
                    } else {
                        try FileManager.default.moveItem(at: partURL, to: target)
                        createdTargets.append(target)
                    }
                } else {
                    // 同卷移动是原子瞬时的；不覆盖
                    if FileManager.default.fileExists(atPath: target.path) {
                        throw MarkEngineError.targetExists(target.path)
                    }
                    try FileManager.default.moveItem(at: sourceURL, to: target)
                    movedPairs.append((sourceURL, target))
                    createdTargets.append(target)
                }
            }
        } catch {
            // 回滚本次操作写入的所有文件（只回滚这一张，不影响队列里其他照片）
            if mode == .copy {
                for target in createdTargets { FileTools.removeIfExists(target) }
                for target in targets {
                    FileTools.removeIfExists(URL(fileURLWithPath: target.path + ".part"))
                }
            } else {
                for pair in movedPairs.reversed() {
                    if FileManager.default.fileExists(atPath: pair.target.path) {
                        try? FileManager.default.moveItem(at: pair.target, to: pair.source)
                    }
                }
            }
            throw error
        }

        // 记录索引（主文件 + 伴生文件）
        let mainTarget = createdTargets.first ?? targets.first!
        let companionPairs: [MarkRecord.Companion] = zip(sources.dropFirst(), createdTargets.dropFirst()).map {
            MarkRecord.Companion(sourcePath: $0.path, copyPath: $1.path)
        }
        let record = MarkRecord(
            sourcePath: source.path,
            sourceSize: FileTools.fileSize(source),
            sourceMTime: FileTools.modificationDate(source).timeIntervalSince1970,
            copyPath: mainTarget.path,
            copySize: FileTools.fileSize(mainTarget),
            copyMTime: FileTools.modificationDate(mainTarget).timeIntervalSince1970,
            mode: mode,
            markedAt: Date().timeIntervalSince1970,
            companions: companionPairs
        )
        do {
            try stateStore.save(record)
        } catch {
            // 索引写不进去不能破坏文件状态：回滚文件并报错
            if mode == .copy {
                for target in createdTargets { FileTools.removeIfExists(target) }
            } else {
                for pair in movedPairs.reversed() {
                    try? FileManager.default.moveItem(at: pair.target, to: pair.source)
                }
            }
            throw error
        }

        DispatchQueue.main.async {
            self.onStateChange?(source.path, .marked)
            self.onRecordChanged?(source.path, record)
            if mode == .copy {
                self.onUndoAvailable?(.undoMarkCopy(record: record))
            } else {
                self.onUndoAvailable?(.undoMarkMove(record: record))
            }
        }
    }

    // MARK: - 取消标记

    private func performUnmark(item: PhotoItem, record: MarkRecord, token: MarkToken) throws {
        let sourcePath = record.sourcePath
        DispatchQueue.main.async { self.onStateChange?(sourcePath, .processing) }

        if record.mode == .copy {
            var trashMap: [String: String] = [:]
            var trashed: [(copy: String, trash: String)] = []
            for copyPath in record.allCopyPaths {
                if token.cancelled { throw MarkEngineError.cancelled }
                let copyURL = URL(fileURLWithPath: copyPath)
                guard FileManager.default.fileExists(atPath: copyURL.path) else { continue }
                do {
                    var resultingURL: NSURL?
                    try FileManager.default.trashItem(at: copyURL, resultingItemURL: &resultingURL)
                    let trashPath = (resultingURL as URL?)?.path ?? copyPath
                    trashMap[copyPath] = trashPath
                    trashed.append((copyPath, trashPath))
                } catch {
                    // 回滚已经进回收站的文件
                    for item in trashed {
                        let from = URL(fileURLWithPath: item.trash)
                        let to = URL(fileURLWithPath: item.copy)
                        if FileManager.default.fileExists(atPath: from.path) {
                            try? FileManager.default.moveItem(at: from, to: to)
                        }
                    }
                    throw MarkEngineError.trashFailed(error.localizedDescription)
                }
            }
            try? stateStore.remove(sourcePath: sourcePath)

            DispatchQueue.main.async {
                self.onStateChange?(sourcePath, .none)
                self.onRecordChanged?(sourcePath, nil)
                self.onUndoAvailable?(.undoUnmarkCopy(record: record, trashURLs: trashMap))
            }
        } else {
            // 移动模式取消：把副本移回原位（同名冲突改名，不覆盖）
            var movedBack: [(from: URL, to: URL)] = []
            do {
                let mainTarget = conflictFreeURLForRestore(record.sourcePath)
                try FileManager.default.moveItem(at: URL(fileURLWithPath: record.copyPath), to: mainTarget)
                movedBack.append((URL(fileURLWithPath: record.copyPath), mainTarget))
                for companion in record.companions {
                    let target = conflictFreeURLForRestore(companion.sourcePath)
                    if FileManager.default.fileExists(atPath: companion.copyPath) {
                        try FileManager.default.moveItem(at: URL(fileURLWithPath: companion.copyPath), to: target)
                        movedBack.append((URL(fileURLWithPath: companion.copyPath), target))
                    }
                }
                try stateStore.remove(sourcePath: sourcePath)
                DispatchQueue.main.async {
                    item.currentURL = mainTarget
                    self.onStateChange?(sourcePath, .none)
                    self.onRecordChanged?(sourcePath, nil)
                    self.onUndoAvailable?(.undoUnmarkMove(record: record))
                }
            } catch {
                // 回滚
                for pair in movedBack.reversed() {
                    if FileManager.default.fileExists(atPath: pair.to.path) {
                        try? FileManager.default.moveItem(at: pair.to, to: pair.from)
                    }
                }
                throw error
            }
        }
    }

    private func conflictFreeURLForRestore(_ desiredPath: String) -> URL {
        let desired = URL(fileURLWithPath: desiredPath)
        let target = FileTools.conflictFreeURL(for: desired)
        return target
    }

    // MARK: - 撤销

    func undo(_ action: UndoAction) {
        operationQueue.async { [weak self] in
            guard let self else { return }
            DispatchQueue.main.async { self.onStateChange?(action.sourcePath, .processing) }
            do {
                let record = try self.performUndo(action)
                DispatchQueue.main.async {
                    self.onStateChange?(action.sourcePath, record == nil ? .none : .marked)
                    self.onRecordChanged?(action.sourcePath, record)
                    self.onUndoResult?(UndoResult(action: action, record: record, error: nil))
                }
            } catch {
                DispatchQueue.main.async {
                    self.onStateChange?(action.sourcePath, action.isUndoOfMark ? .none : .marked)
                    self.onUndoResult?(UndoResult(action: action, record: nil, error: error.localizedDescription))
                }
            }
        }
    }

    private func performUndo(_ action: UndoAction) throws -> MarkRecord? {
        switch action {
        case .undoMarkCopy(let record):
            for copyPath in record.allCopyPaths {
                let url = URL(fileURLWithPath: copyPath)
                if FileManager.default.fileExists(atPath: url.path) {
                    try FileManager.default.trashItem(at: url, resultingItemURL: nil)
                }
            }
            try stateStore.remove(sourcePath: record.sourcePath)
            return nil

        case .undoMarkMove(let record):
            let mainTarget = FileTools.conflictFreeURL(for: URL(fileURLWithPath: record.sourcePath))
            try FileManager.default.moveItem(at: URL(fileURLWithPath: record.copyPath), to: mainTarget)
            for companion in record.companions {
                if FileManager.default.fileExists(atPath: companion.copyPath) {
                    let target = FileTools.conflictFreeURL(for: URL(fileURLWithPath: companion.sourcePath))
                    try FileManager.default.moveItem(at: URL(fileURLWithPath: companion.copyPath), to: target)
                }
            }
            try stateStore.remove(sourcePath: record.sourcePath)
            return nil

        case .undoUnmarkCopy(let record, let trashURLs):
            for (copyPath, trashPath) in trashURLs {
                let from = URL(fileURLWithPath: trashPath)
                let to = URL(fileURLWithPath: copyPath)
                guard FileManager.default.fileExists(atPath: from.path) else {
                    throw AppError("回收站中的副本已不存在：\(trashPath)")
                }
                try FileManager.default.createDirectory(at: to.deletingLastPathComponent(), withIntermediateDirectories: true)
                if FileManager.default.fileExists(atPath: to.path) {
                    try FileManager.default.removeItem(at: to)
                }
                try FileManager.default.moveItem(at: from, to: to)
            }
            try stateStore.save(record)
            return record

        case .undoUnmarkMove(let record):
            try FileTools.ensureDirectory(filterFolder)
            let mainTarget = FileTools.conflictFreeURL(for: URL(fileURLWithPath: record.copyPath))
            try FileManager.default.moveItem(at: URL(fileURLWithPath: record.sourcePath), to: mainTarget)
            var companionsCopied: [MarkRecord.Companion] = []
            for companion in record.companions {
                if FileManager.default.fileExists(atPath: companion.sourcePath) {
                    let target = FileTools.conflictFreeURL(for: URL(fileURLWithPath: companion.copyPath))
                    try FileManager.default.moveItem(at: URL(fileURLWithPath: companion.sourcePath), to: target)
                    companionsCopied.append(MarkRecord.Companion(sourcePath: companion.sourcePath, copyPath: target.path))
                }
            }
            var updated = record
            updated.copyPath = mainTarget.path
            updated.copySize = FileTools.fileSize(mainTarget)
            updated.copyMTime = FileTools.modificationDate(mainTarget).timeIntervalSince1970
            updated.companions = companionsCopied
            try stateStore.save(updated)
            return updated
        }
    }

    // MARK: - 伴生文件与目标命名

    /// 同主文件名的 .xmp / .aae / .thm 以及双拍 JPG/HEIF
    func companionSources(for source: URL) -> [URL] {
        let directory = source.deletingLastPathComponent()
        let base = source.deletingPathExtension().lastPathComponent.lowercased()
        let sourcePath = source.standardizedFileURL.path
        let allowed: Set<String> = ["xmp", "aae", "thm", "jpg", "jpeg", "heic", "heif", "hif"]

        var result: [URL] = []
        var seen = Set<String>()
        for candidate in FileTools.listFiles(in: directory) {
            let candidateBase = candidate.deletingPathExtension().lastPathComponent.lowercased()
            guard candidateBase == base else { continue }
            if candidate.standardizedFileURL.path == sourcePath { continue }
            guard allowed.contains(candidate.pathExtension.lowercased()) else { continue }
            if seen.insert(candidate.standardizedFileURL.path).inserted {
                result.append(candidate)
            }
        }
        result.sort { NaturalSort.less($0.lastPathComponent, $1.lastPathComponent) }
        return result
    }

    private func groupedTargets(for sources: [URL], in filterFolder: URL) -> [URL] {
        let reserved = Set<String>()
        for index in 0..<10000 {
            var candidates: [URL] = []
            var seen = Set<String>()
            var ok = true
            for source in sources {
                let name = candidateName(for: source.lastPathComponent, index: index)
                let target = filterFolder.appendingPathComponent(name)
                if seen.contains(target.path.lowercased()) || reserved.contains(target.path) {
                    ok = false
                    break
                }
                seen.insert(target.path.lowercased())
                if FileManager.default.fileExists(atPath: target.path) {
                    ok = false
                    break
                }
                candidates.append(target)
            }
            if ok, candidates.count == sources.count { return candidates }
        }
        return sources.map { filterFolder.appendingPathComponent($0.lastPathComponent) }
    }

    private func candidateName(for fileName: String, index: Int) -> String {
        guard index > 0 else { return fileName }
        let url = URL(fileURLWithPath: fileName)
        let ext = url.pathExtension
        let base = url.deletingPathExtension().lastPathComponent
        return ext.isEmpty ? "\(base) (\(index))" : "\(base) (\(index)).\(ext)"
    }
}

/// 队列取消令牌
final class MarkToken {
    let id = UUID()
    var cancelled = false
}

extension UndoAction {
    var sourcePath: String {
        switch self {
        case .undoMarkCopy(let record), .undoMarkMove(let record),
             .undoUnmarkCopy(let record, _), .undoUnmarkMove(let record):
            return record.sourcePath
        }
    }

    var isUndoOfMark: Bool {
        switch self {
        case .undoMarkCopy, .undoMarkMove: return true
        default: return false
        }
    }
}
