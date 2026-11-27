// 影·筛 / PhotoCull — 查看会话：文件夹、排序、选择、标记、预取、状态
import Foundation
import ImageIO
import AppKit

struct ScanSummary {
    var folder: URL
    var total: Int
    var dataless: Int
    var durationMS: Double
}

final class ViewerSession {
    let settings = AppSettings.shared
    let memoryCache: ImageMemoryCache
    let diskCache: ThumbnailCache
    let scheduler: DecodeScheduler

    private(set) var folderURL: URL?
    private(set) var items: [PhotoItem] = []
    private var itemByPath: [String: PhotoItem] = [:]
    private(set) var currentIndex: Int = -1
    private(set) var stateStore: StateStore?
    private(set) var markEngine: MarkEngine?
    private var undoStack: [UndoAction] = []
    private let maxUndo = 2000

    private let workQueue = DispatchQueue(label: "com.jadis038.photocull.viewer.work", qos: .userInitiated)
    private let metadataQueue = DispatchQueue(label: "com.jadis038.photocull.metadata", qos: .utility)
    private var folderGeneration = 0
    private var thumbDone: Set<String> = []
    private var metadataRequested: Set<String> = []
    private var preheatScheduled = false
    private var currentSortKey: SortKey
    private var currentSortOrder: SortOrder
    private var isCaptureSortPreparing = false

    // MARK: - 回调（主线程）

    var onFolderOpened: ((ScanSummary) -> Void)?
    var onScanFailed: ((String) -> Void)?
    var onSelectionChanged: ((PhotoItem) -> Void)?
    var onMetadataReady: ((PhotoItem) -> Void)?
    var onPreviewReady: ((String, CGImage?) -> Void)?
    var onFullReady: ((String, CGImage?) -> Void)?
    var onThumbnailReady: ((String, CGImage?) -> Void)?
    var onItemMarkChanged: ((PhotoItem) -> Void)?
    var onCountsChanged: ((Int, Int) -> Void)?
    var onThumbnailProgress: ((Int, Int) -> Void)?
    var onStatusText: ((String?) -> Void)?
    var onError: ((String) -> Void)?
    var onUndoAvailabilityChanged: ((Bool) -> Void)?
    var onQueueCountChanged: ((Int) -> Void)?
    var onFoldersSwitched: (() -> Void)?

    var currentItem: PhotoItem? {
        guard items.indices.contains(currentIndex) else { return nil }
        return items[currentIndex]
    }

    var markedCount: Int {
        items.reduce(0) { $0 + ($1.markState == .marked ? 1 : 0) }
    }

    var remainingCount: Int { max(0, items.count - markedCount) }

    var canUndo: Bool { !undoStack.isEmpty }

    init() {
        memoryCache = ImageMemoryCache(costLimitBytes: 512 * 1024 * 1024)
        diskCache = ThumbnailCache(limitBytes: settings.cacheLimitBytes)
        scheduler = DecodeScheduler(memoryCache: memoryCache, diskCache: diskCache)
        currentSortKey = settings.sortKey
        currentSortOrder = settings.sortOrder
        scheduler.maxConcurrent = settings.effectiveConcurrency
        scheduler.onResult = { [weak self] result in
            self?.handleDecodeResult(result)
        }
        DispatchQueue.global(qos: .utility).async { [diskCache] in
            diskCache.trim()
        }
    }

    func applySettings() {
        scheduler.maxConcurrent = settings.effectiveConcurrency
        diskCache.limitBytes = settings.cacheLimitBytes
        DispatchQueue.global(qos: .utility).async { [diskCache] in diskCache.trim() }
    }

    // MARK: - 打开文件夹

    /// fileURL 可以是文件夹，也可以是「打开方式」传入的单张图片
    func open(url: URL, selectFileName: String? = nil) {
        var folder = url.standardizedFileURL
        var targetName = selectFileName

        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: folder.path, isDirectory: &isDir) {
            if !isDir.boolValue {
                targetName = folder.lastPathComponent
                folder = folder.deletingLastPathComponent()
                // 若传入的是「筛选」里的副本，回到它的上级原文件夹
                if folder.lastPathComponent == Scanner.filterFolderName {
                    folder = folder.deletingLastPathComponent()
                }
            }
        } else {
            onScanFailed?("路径不存在：\(url.path)")
            return
        }

        guard FileTools.isDirectory(folder) else {
            onScanFailed?("不是文件夹：\(folder.path)")
            return
        }

        folderGeneration += 1
        let generation = folderGeneration

        scheduler.cancelAll()
        markEngine?.cancelAll()
        markEngine = nil
        stateStore?.close()
        stateStore = nil
        items = []
        itemByPath = [:]
        currentIndex = -1
        thumbDone.removeAll()
        metadataRequested.removeAll()
        undoStack.removeAll()
        preheatScheduled = false
        onUndoAvailabilityChanged?(false)
        onCountsChanged?(0, 0)
        onThumbnailProgress?(0, 0)
        onStatusText?("正在读取文件夹…")

        workQueue.async { [weak self] in
            guard let self else { return }
            do {
                self.cleanupTemporaryFiles(in: folder)
                let scan = try Scanner.scan(folder: folder)
                let store = try StateStore(folder: folder)
                var records = store.loadAll()
                if records.isEmpty && FileTools.isDirectory(folder.appendingPathComponent(Scanner.filterFolderName)) {
                    let rebuilt = store.rebuildFromFilterFolder(items: scan.items)
                    if !rebuilt.isEmpty {
                        records = rebuilt
                        Log.info("按文件名重建索引：\(rebuilt.count) 条")
                    }
                }

                // 校验副本是否还在
                var validRecords: [String: MarkRecord] = [:]
                for (path, record) in records {
                    if FileManager.default.fileExists(atPath: record.copyPath) {
                        var updated = record
                        updated.sourceSize = scan.items.first(where: { $0.url.path == path })?.size ?? record.sourceSize
                        validRecords[path] = updated
                    } else {
                        try? store.remove(sourcePath: path)
                    }
                }

                var scannedPaths = Set<String>()
                for item in scan.items {
                    scannedPaths.insert(item.url.path)
                    if let record = validRecords[item.url.path] {
                        item.record = record
                        item.markState = .marked
                        item.markMode = record.mode
                    }
                }

                // 索引里有、源目录里没有：移动模式的照片，或源文件被外部删除
                var recovered: [PhotoItem] = []
                for (path, record) in validRecords where !scannedPaths.contains(path) {
                    let sourceURL = URL(fileURLWithPath: path)
                    let item = PhotoItem(url: sourceURL,
                                         size: record.sourceSize,
                                         mtime: Date(timeIntervalSince1970: record.sourceMTime))
                    item.record = record
                    item.markState = .marked
                    item.markMode = record.mode
                    item.sourceMissing = !FileManager.default.fileExists(atPath: path)
                    recovered.append(item)
                }

                var allItems = scan.items + recovered
                allItems = Scanner.sort(allItems, by: self.settings.sortKey, order: self.settings.sortOrder)
                for (index, item) in allItems.enumerated() { item.displayIndex = index }

                DispatchQueue.main.async {
                    guard generation == self.folderGeneration else {
                        store.close()
                        return
                    }
                    self.folderURL = folder
                    self.stateStore = store
                    self.items = allItems
                    self.rebuildPathIndex()
                    self.setupEngine(folder: folder, store: store)
                    self.updateCounts()
                    self.onFolderOpened?(ScanSummary(folder: folder, total: allItems.count,
                                                     dataless: scan.datalessCount,
                                                     durationMS: scan.duration))
                    if AppSettings.shared.rememberLastFolder {
                        AppSettings.shared.lastFolderPath = folder.path
                    }

                    var index = 0
                    if let targetName, let found = allItems.firstIndex(where: { $0.name == targetName }) {
                        index = found
                    } else if let targetName, let found = allItems.firstIndex(where: {
                        $0.url.deletingPathExtension().lastPathComponent == (targetName as NSString).deletingPathExtension
                    }) {
                        index = found
                    }
                    if !allItems.isEmpty { self.select(index: index) }
                    self.startInitialThumbnails()
                    self.schedulePreheat()

                    if scan.datalessCount > 0 {
                        self.onStatusText?("有 \(scan.datalessCount) 个 iCloud 文件尚未下载，查看时可能较慢")
                    }
                }
            } catch {
                DispatchQueue.main.async {
                    guard generation == self.folderGeneration else { return }
                    self.onScanFailed?(error.localizedDescription)
                }
            }
        }
    }

    /// 清理上次异常退出残留的 .part
    func cleanupTemporaryFiles(in folder: URL? = nil) {
        guard let target = folder ?? folderURL else { return }
        let filter = target.appendingPathComponent(Scanner.filterFolderName, isDirectory: true)
        guard FileTools.isDirectory(filter) else { return }
        for file in FileTools.listFiles(in: filter) where file.lastPathComponent.hasSuffix(".part") {
            FileTools.removeIfExists(file)
        }
    }

    private func rebuildPathIndex() {
        itemByPath.removeAll()
        for item in items {
            itemByPath[item.url.path] = item
            itemByPath[item.currentURL.path] = item
            if let record = item.record { itemByPath[record.copyPath] = item }
        }
    }

    private func setupEngine(folder: URL, store: StateStore) {
        let engine = MarkEngine(folder: folder, stateStore: store)
        engine.defaultMode = settings.operationMode
        engine.onStateChange = { [weak self] path, state in
            self?.handleMarkState(path: path, state: state)
        }
        engine.onRecordChanged = { [weak self] path, record in
            self?.handleRecordChanged(path: path, record: record)
        }
        engine.onError = { [weak self] path, message in
            guard let self else { return }
            if let item = self.itemByPath[path], item.record != nil {
                item.markState = .marked
                self.onItemMarkChanged?(item)
            }
            self.updateCounts()
            self.onError?(message)
        }
        engine.onQueueCountChanged = { [weak self] count in
            self?.onQueueCountChanged?(count)
            if count == 0 { self?.onStatusText?(nil) }
        }
        engine.onBusyChanged = { [weak self] busy in
            self?.scheduler.setCopyActive(busy)
        }
        engine.onUndoAvailable = { [weak self] action in
            self?.pushUndo(action)
        }
        engine.onUndoResult = { [weak self] result in
            guard let self else { return }
            if let error = result.error { self.onError?(error) }
            self.onStatusText?(result.error == nil ? "已撤销" : nil)
            self.updateCounts()
        }
        markEngine = engine
    }

    private func handleMarkState(path: String, state: MarkState) {
        guard let item = itemByPath[path] else { return }
        item.markState = state
        if state == .marked, let record = item.record {
            item.markMode = record.mode
        }
        onItemMarkChanged?(item)
        updateCounts()
    }

    private func handleRecordChanged(path: String, record: MarkRecord?) {
        guard let item = itemByPath[path] else { return }
        item.record = record
        if let record {
            item.markMode = record.mode
        }
        onItemMarkChanged?(item)
        updateCounts()
    }

    private func pushUndo(_ action: UndoAction) {
        undoStack.append(action)
        if undoStack.count > maxUndo {
            undoStack.removeFirst(undoStack.count - maxUndo)
        }
        onUndoAvailabilityChanged?(true)
    }

    // MARK: - 选择与解码

    func select(index: Int) {
        guard items.indices.contains(index) else { return }
        currentIndex = index
        let item = items[index]
        onSelectionChanged?(item)
        ensureMetadata(for: item)
        scheduler.request(item: item, kind: .preview, priority: .current)
        prefetchNeighbours(around: index)
    }

    func select(offset: Int) {
        guard !items.isEmpty else { return }
        var next = currentIndex + offset
        next = max(0, min(items.count - 1, next))
        if next != currentIndex { select(index: next) }
    }

    private func prefetchNeighbours(around index: Int) {
        guard !items.isEmpty else { return }
        let lower = max(0, index - 10)
        let upper = min(items.count - 1, index + 10)
        for offsetIndex in lower...upper where offsetIndex != index {
            scheduler.request(item: items[offsetIndex], kind: .preview, priority: .prefetch)
        }
    }

    func requestThumbnail(for item: PhotoItem) {
        scheduler.request(item: item, kind: .thumb, priority: .visible)
    }

    func requestFull(for item: PhotoItem) {
        let native = max(item.metadata?.pixelWidth ?? 0, item.metadata?.pixelHeight ?? 0)
        scheduler.request(item: item, kind: .full, nativeMaxPixel: native > 0 ? native : 20000, priority: .current)
    }

    private func startInitialThumbnails() {
        guard !items.isEmpty else { return }
        let count = min(items.count, 20)
        for index in 0..<count {
            scheduler.request(item: items[index], kind: .thumb, priority: .visible)
        }
    }

    private func schedulePreheat() {
        guard !preheatScheduled else { return }
        preheatScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [weak self] in
            guard let self, let folder = self.folderURL else { return }
            let generation = self.folderGeneration
            let items = self.items
            self.workQueue.async {
                guard generation == self.folderGeneration, self.folderURL == folder else { return }
                for item in items where !self.thumbDone.contains(item.url.path) {
                    // 超大文件（如 200MB+ 的 PNG/TIFF/EXR）不主动预热，避免一次性占用大量内存；
                    // 滚动到可见时仍会按需生成。计入进度，避免状态永远停在“正在生成”。
                    if item.size > 80 * 1024 * 1024 {
                        let path = item.url.path
                        DispatchQueue.main.async {
                            if !self.thumbDone.contains(path) {
                                self.thumbDone.insert(path)
                                self.onThumbnailProgress?(self.thumbDone.count, self.items.count)
                            }
                        }
                        continue
                    }
                    self.scheduler.request(item: item, kind: .thumb, priority: .idle)
                }
            }
        }
    }

    private func handleDecodeResult(_ result: DecodeScheduler.Result) {
        let item = itemByPath[result.path]
        switch result.kind {
        case .thumb:
            if !thumbDone.contains(result.path) {
                thumbDone.insert(result.path)
                onThumbnailProgress?(thumbDone.count, items.count)
            }
            item?.thumbReady = result.image != nil
            onThumbnailReady?(result.path, result.image)
        case .preview:
            if currentItem?.url.path == result.path || item?.url.path == result.path {
                onPreviewReady?(result.path, result.image)
            }
        case .full:
            if currentItem?.url.path == result.path {
                onFullReady?(result.path, result.image)
            }
        }
    }

    // MARK: - 元数据

    func ensureMetadata(for item: PhotoItem) {
        if item.metadata != nil {
            onMetadataReady?(item)
            return
        }
        let key = item.url.path
        if metadataRequested.contains(key) { return }
        metadataRequested.insert(key)
        let decodeURL = item.decodeURL
        metadataQueue.async { [weak self] in
            let metadata = MetadataReader.read(from: decodeURL)
            DispatchQueue.main.async {
                guard let self else { return }
                item.metadata = metadata
                self.metadataRequested.remove(key)
                if self.currentItem === item {
                    self.onMetadataReady?(item)
                }
                if self.currentSortKey == .captureTime {
                    self.scheduleResortAfterMetadata()
                }
            }
        }
    }

    private var resortWorkItem: DispatchWorkItem?

    private func scheduleResortAfterMetadata() {
        resortWorkItem?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.currentSortKey == .captureTime else { return }
            self.applySort(key: self.currentSortKey, order: self.currentSortOrder, reload: true)
        }
        resortWorkItem = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6, execute: work)
    }

    // MARK: - 排序

    func setSort(key: SortKey, order: SortOrder) {
        currentSortKey = key
        currentSortOrder = order
        settings.sortKey = key
        settings.sortOrder = order
        if key == .captureTime {
            prepareCaptureTimeMetadata { [weak self] in
                self?.applySort(key: key, order: order, reload: true)
            }
        } else {
            applySort(key: key, order: order, reload: true)
        }
    }

    private func prepareCaptureTimeMetadata(completion: @escaping () -> Void) {
        guard !isCaptureSortPreparing else { return }
        isCaptureSortPreparing = true
        let snapshot = items
        let generation = folderGeneration
        onStatusText?("正在读取拍摄时间…")
        metadataQueue.async { [weak self] in
            guard let self else { return }
            var remaining = 0
            for item in snapshot {
                if generation != self.folderGeneration { break }
                if item.metadata?.captureDate == nil {
                    // 只补拍摄时间，信息栏需要时仍会完整读取
                    if let date = MetadataReader.readCaptureDateFast(from: item.decodeURL) {
                        DispatchQueue.main.async {
                            item.quickCaptureDate = date
                        }
                    } else {
                        remaining += 1
                    }
                }
            }
            DispatchQueue.main.async {
                self.isCaptureSortPreparing = false
                self.onStatusText?(nil)
                _ = remaining
                completion()
            }
        }
    }

    private func applySort(key: SortKey, order: SortOrder, reload: Bool) {
        guard !items.isEmpty else { return }
        let selectedPath = currentItem?.url.path
        items = Scanner.sort(items, by: key, order: order)
        for (index, item) in items.enumerated() { item.displayIndex = index }
        if let selectedPath, let index = items.firstIndex(where: { $0.url.path == selectedPath }) {
            currentIndex = index
        } else {
            currentIndex = min(currentIndex, items.count - 1)
        }
        rebuildPathIndex()
        if reload { onFoldersSwitched?() }
        if let item = currentItem { onSelectionChanged?(item) }
    }

    func resortFromSettings() {
        setSort(key: settings.sortKey, order: settings.sortOrder)
    }

    // MARK: - 标记 / 取消 / 撤销

    func toggleMark(_ item: PhotoItem) {
        guard let engine = markEngine else { return }
        switch item.markState {
        case .none, .error:
            item.markState = .queued
            item.markMode = settings.operationMode
            onItemMarkChanged?(item)
            engine.enqueueMark(item: item, mode: settings.operationMode)
        case .queued, .processing:
            item.markState = .none
            engine.cancel(path: item.currentURL.path)
            onItemMarkChanged?(item)
        case .marked:
            guard let record = item.record else {
                item.markState = .none
                onItemMarkChanged?(item)
                return
            }
            item.markState = .processing
            onItemMarkChanged?(item)
            engine.enqueueUnmark(item: item, record: record)
        }
        updateCounts()
    }

    func undo() {
        guard let action = undoStack.popLast(), let engine = markEngine else { return }
        onUndoAvailabilityChanged?(!undoStack.isEmpty)
        onStatusText?("正在撤销…")
        engine.undo(action)
    }

    func cancelAllOperations() {
        guard let engine = markEngine else { return }
        for item in items where item.markState == .queued {
            item.markState = .none
            onItemMarkChanged?(item)
        }
        engine.cancelAll()
        updateCounts()
    }

    func refresh() {
        guard let folder = folderURL else { return }
        let selectedName = currentItem?.name
        open(url: folder, selectFileName: selectedName)
    }

    private func updateCounts() {
        onCountsChanged?(markedCount, items.count)
    }

    func clearThumbnailCache() {
        diskCache.clear()
        memoryCache.clear()
        thumbDone.removeAll()
        onThumbnailProgress?(0, items.count)
        if let folder = folderURL {
            open(url: folder, selectFileName: currentItem?.name)
        }
    }

    // MARK: - 状态

    func ensureEngine() -> MarkEngine? { markEngine }
}
