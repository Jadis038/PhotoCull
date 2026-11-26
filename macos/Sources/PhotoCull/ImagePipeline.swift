// 影·筛 / PhotoCull — 图像解码、三级缓存与优先级调度
import Foundation
import ImageIO
import CoreGraphics
import AppKit

/// 解码种类
enum DecodeKind: String {
    case thumb   // 缩略图条 160px
    case preview // 大图预览 1024px
    case full    // 100% 全尺寸（后台）

    var cacheKind: String { rawValue }
}

struct DecodePriority: Comparable {
    let rawValue: Int
    static let current = DecodePriority(rawValue: 0)
    static let visible = DecodePriority(rawValue: 1)
    static let prefetch = DecodePriority(rawValue: 2)
    static let idle = DecodePriority(rawValue: 3)

    static func < (lhs: DecodePriority, rhs: DecodePriority) -> Bool { lhs.rawValue < rhs.rawValue }
}

// MARK: - 解码

enum ImageDecoder {
    /// 用 ImageIO 生成按 EXIF 方向校正后的缩略图。
    /// - preferEmbedded: true 时优先使用相机内嵌小图（条带缩略图，极快）
    static func thumbnail(url: URL, maxPixel: Int, preferEmbedded: Bool) -> CGImage? {
        // 缩略图极致加速：RAW 直接抽取内嵌 JPEG，不启动 RAW 解码管线
        if preferEmbedded, maxPixel <= 320, let fast = FastThumbnail.image(url: url, maxPixel: maxPixel) {
            return fast
        }
        guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary) else {
            return nil
        }
        var options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: !preferEmbedded,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel,
            kCGImageSourceShouldCacheImmediately: false
        ]
        if preferEmbedded {
            options[kCGImageSourceCreateThumbnailFromImageIfAbsent] = true
        }
        if let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) {
            return image
        }
        // AVIF / PNG / TIFF / EXR / JXL 等没有内嵌缩略图时，退回解码整图并缩放
        if preferEmbedded {
            let fallback: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: maxPixel,
                kCGImageSourceShouldCacheImmediately: false
            ]
            return CGImageSourceCreateThumbnailAtIndex(source, 0, fallback as CFDictionary)
        }
        return nil
    }

    /// 全尺寸解码：直接让 ImageIO 输出方向校正后的完整图像。
    /// 尼康等相机内嵌全尺寸 JPEG 时，这一步也走内嵌预览，速度远快于完整 RAW 处理。
    static func fullImage(url: URL, nativeMaxPixel: Int) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary) else {
            return nil
        }
        let maxPixel = max(nativeMaxPixel, 1024)
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel,
            kCGImageSourceShouldCacheImmediately: true
        ]
        if let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) {
            return image
        }
        // 兜底：直接解码（方向需要由调用方按 EXIF 处理）
        return CGImageSourceCreateImageAtIndex(source, 0, nil)
    }

    static func byteCost(of image: CGImage) -> Int {
        max(1, image.height * image.bytesPerRow)
    }
}

// MARK: - 内存缓存

final class ImageMemoryCache {
    private let cache = NSCache<NSString, CGImageBox>()

    init(costLimitBytes: Int) {
        cache.totalCostLimit = costLimitBytes
        cache.countLimit = 512
    }

    func image(for key: String) -> CGImage? {
        cache.object(forKey: key as NSString)?.image
    }

    func store(_ image: CGImage, for key: String) {
        cache.setObject(CGImageBox(image: image), forKey: key as NSString, cost: ImageDecoder.byteCost(of: image))
    }

    func remove(for key: String) {
        cache.removeObject(forKey: key as NSString)
    }

    func clear() {
        cache.removeAllObjects()
    }
}

final class CGImageBox {
    let image: CGImage
    init(image: CGImage) { self.image = image }
}

// MARK: - 磁盘缓存

final class ThumbnailCache {
    static let version = "v2"
    let directory: URL
    let index: CacheIndex
    private let trimLock = NSLock()
    private var lastTrimTime: TimeInterval = 0
    var limitBytes: Int64

    init(limitBytes: Int64) {
        AppInfo.ensureDirectories()
        directory = AppInfo.thumbnailCacheDirectory
        self.limitBytes = limitBytes
        index = CacheIndex()
    }

    static func cacheKey(url: URL, size: Int64, mtime: Date, kind: DecodeKind) -> String {
        let mtimeMillis = Int64(mtime.timeIntervalSince1970 * 1000)
        return Hashing.sha256("\(version)|\(kind.rawValue)|\(url.path)|\(size)|\(mtimeMillis)")
    }

    func fileURL(for key: String) -> URL {
        let shard = String(key.prefix(2))
        return directory.appendingPathComponent(shard, isDirectory: true).appendingPathComponent("\(key).jpg")
    }

    func image(for key: String) -> CGImage? {
        let path = fileURL(for: key)
        guard FileManager.default.fileExists(atPath: path.path),
              let source = CGImageSourceCreateWithURL(path as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
            return nil
        }
        FileTools.touch(path)
        index.touch(key: key)
        return image
    }

    @discardableResult
    func store(_ image: CGImage, for key: String, kind: DecodeKind) -> Bool {
        let target = fileURL(for: key)
        let directory = target.deletingLastPathComponent()
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        } catch {
            Log.info("创建缓存目录失败：\(error.localizedDescription)")
            return false
        }
        let temporary = directory.appendingPathComponent("\(key).tmp")
        guard let destination = CGImageDestinationCreateWithURL(temporary as CFURL, "public.jpeg" as CFString, 1, nil) else {
            return false
        }
        CGImageDestinationAddImage(destination, image, [kCGImageDestinationLossyCompressionQuality: 0.82] as CFDictionary)
        guard CGImageDestinationFinalize(destination) else {
            FileTools.removeIfExists(temporary)
            return false
        }
        do {
            if FileManager.default.fileExists(atPath: target.path) {
                try FileManager.default.removeItem(at: target)
            }
            try FileManager.default.moveItem(at: temporary, to: target)
        } catch {
            FileTools.removeIfExists(temporary)
            return false
        }
        let bytes = FileTools.fileSize(target)
        index.record(key: key, kind: kind.cacheKind, path: target.path, bytes: bytes)
        scheduleTrimIfNeeded()
        return true
    }

    func currentSize() -> Int64 {
        index.totalBytes()
    }

    func clear() {
        FileTools.removeIfExists(directory)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        index.clear()
        Log.info("已清空缩略图缓存")
    }

    var entriesCount: Int {
        var count = 0
        // 粗略统计，用于状态展示
        let enumerator = FileManager.default.enumerator(at: directory, includingPropertiesForKeys: nil)
        while let _ = enumerator?.nextObject() as? URL { count += 1 }
        return count
    }

    private func scheduleTrimIfNeeded() {
        trimLock.lock()
        let now = Date().timeIntervalSince1970
        if now - lastTrimTime < 30 { trimLock.unlock(); return }
        lastTrimTime = now
        trimLock.unlock()
        DispatchQueue.global(qos: .utility).async { [weak self] in
            self?.trim()
        }
    }

    /// LRU 清理到上限的 90%
    func trim() {
        let total = currentSize()
        guard total > limitBytes else { return }
        let target = Int64(Double(limitBytes) * 0.9)
        var current = total
        while current > target {
            let entries = index.oldestEntries(limit: 64)
            if entries.isEmpty { break }
            for entry in entries {
                FileTools.removeIfExists(URL(fileURLWithPath: entry.path))
                index.remove(key: entry.key)
                current -= entry.bytes
                if current <= target { break }
            }
        }
        Log.info("缓存 LRU 清理完成，当前约 \(Fmt.bytes(currentSize()))")
    }
}

// MARK: - 优先级解码调度器

final class DecodeScheduler {
    struct Result {
        let path: String
        let kind: DecodeKind
        let image: CGImage?
        let fromCache: Bool
        let durationMS: Double
    }

    private final class Task {
        let key: String
        let path: String
        let url: URL
        let kind: DecodeKind
        let nativeMaxPixel: Int
        let generation: Int
        let seq: Int
        var priority: DecodePriority
        var cancelled = false

        init(key: String, path: String, url: URL, kind: DecodeKind, nativeMaxPixel: Int, generation: Int, seq: Int, priority: DecodePriority) {
            self.key = key
            self.path = path
            self.url = url
            self.kind = kind
            self.nativeMaxPixel = nativeMaxPixel
            self.generation = generation
            self.seq = seq
            self.priority = priority
        }
    }

    private let controlQueue = DispatchQueue(label: "com.jadis038.photocull.decode.control")
    private let workQueue = DispatchQueue(label: "com.jadis038.photocull.decode.work", qos: .userInitiated, attributes: .concurrent)
    private let memoryCache: ImageMemoryCache
    private let diskCache: ThumbnailCache

    private var pending: [String: Task] = [:]
    private var order: [Task] = []
    private var activeKeys: Set<String> = []
    private var activeCount = 0
    private var generation = 0
    private var nextSeq = 0
    private var configuredMax = 4
    private var copyActive = false

    var onResult: ((Result) -> Void)?

    var pendingCount: Int {
        controlQueue.sync { order.count + activeCount }
    }

    init(memoryCache: ImageMemoryCache, diskCache: ThumbnailCache) {
        self.memoryCache = memoryCache
        self.diskCache = diskCache
    }

    var maxConcurrent: Int {
        get { controlQueue.sync { configuredMax } }
        set {
            controlQueue.async {
                self.configuredMax = max(1, newValue)
                self.pump()
            }
        }
    }

    /// 复制/移动任务运行时把解码并发降到 1，避免抢 IO
    func setCopyActive(_ active: Bool) {
        controlQueue.async {
            self.copyActive = active
            if !active { self.pump() }
        }
    }

    /// 请求解码。内存缓存命中会立即回调；同一文件同一档位只会有一个任务。
    func request(item: PhotoItem, kind: DecodeKind, nativeMaxPixel: Int = 0, priority: DecodePriority) {
        let decodeURL = item.decodeURL
        let key = ThumbnailCache.cacheKey(url: decodeURL, size: item.size, mtime: item.mtime, kind: kind)
        if let cached = memoryCache.image(for: key) {
            let result = Result(path: item.url.path, kind: kind, image: cached, fromCache: true, durationMS: 0)
            DispatchQueue.main.async { self.onResult?(result) }
            return
        }
        controlQueue.async {
            if let existing = self.pending[key] {
                existing.priority = min(existing.priority, priority)
                return
            }
            if self.activeKeys.contains(key) { return }
            let task = Task(key: key, path: item.url.path, url: decodeURL, kind: kind,
                            nativeMaxPixel: nativeMaxPixel, generation: self.generation,
                            seq: self.nextSeq, priority: priority)
            self.nextSeq += 1
            self.pending[key] = task
            self.order.append(task)
            self.pump()
        }
    }

    func cancelAll() {
        controlQueue.async {
            self.generation += 1
            for task in self.order { task.cancelled = true }
            self.order.removeAll()
            self.pending.removeAll()
        }
    }

    private func effectiveMax() -> Int {
        copyActive ? 1 : max(1, configuredMax)
    }

    private func pump() {
        dispatchPrecondition(condition: .onQueue(controlQueue))
        while activeCount < effectiveMax() {
            guard let task = popNext() else { break }
            if task.cancelled { continue }
            activeKeys.insert(task.key)
            activeCount += 1
            run(task)
        }
    }

    private func popNext() -> Task? {
        guard !order.isEmpty else { return nil }
        var bestIndex = 0
        for index in 1..<order.count {
            let candidate = order[index]
            let best = order[bestIndex]
            if candidate.priority < best.priority ||
                (candidate.priority == best.priority && candidate.seq < best.seq) {
                bestIndex = index
            }
        }
        let task = order.remove(at: bestIndex)
        pending.removeValue(forKey: task.key)
        return task
    }

    private func run(_ task: Task) {
        let queue = workQueue
        queue.async {
            let start = CFAbsoluteTimeGetCurrent()
            var image: CGImage?
            var fromCache = false

            if task.kind != .full, let cached = self.diskCache.image(for: task.key) {
                image = cached
                fromCache = true
            }

            if image == nil, !task.cancelled {
                switch task.kind {
                case .thumb:
                    image = ImageDecoder.thumbnail(url: task.url, maxPixel: 160, preferEmbedded: true)
                case .preview:
                    image = ImageDecoder.thumbnail(url: task.url, maxPixel: 2048, preferEmbedded: false)
                case .full:
                    image = ImageDecoder.fullImage(url: task.url, nativeMaxPixel: task.nativeMaxPixel)
                }
                if let image, task.kind != .full {
                    self.diskCache.store(image, for: task.key, kind: task.kind)
                }
            }

            if let image, task.kind != .full {
                self.memoryCache.store(image, for: task.key)
            }

            let duration = (CFAbsoluteTimeGetCurrent() - start) * 1000
            self.controlQueue.async {
                self.activeCount -= 1
                self.activeKeys.remove(task.key)
                let deliver = task.generation == self.generation && !task.cancelled
                if deliver {
                    let result = Result(path: task.path, kind: task.kind, image: image,
                                        fromCache: fromCache, durationMS: duration)
                    DispatchQueue.main.async {
                        self.onResult?(result)
                    }
                }
                self.pump()
            }
        }
    }
}
