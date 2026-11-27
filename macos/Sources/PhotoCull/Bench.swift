// 影·筛 / PhotoCull — 性能基准（枚举、首屏、单张解码、缓存、内存）
import Foundation
import ImageIO
import CoreGraphics
import Darwin

enum Bench {
    static func run(folder: URL, csvPath: String?, limit: Int?) -> Int32 {
        print("""
        \(AppInfo.name) 性能基准
        文件夹：\(folder.path)
        """)
        let fileManager = FileManager.default

        let scanStart = CFAbsoluteTimeGetCurrent()
        let scan: ScanResult
        do {
            scan = try Scanner.scan(folder: folder)
        } catch {
            print("扫描失败：\(error.localizedDescription)")
            return 1
        }
        let scanMS = (CFAbsoluteTimeGetCurrent() - scanStart) * 1000
        let items = scan.items
        guard !items.isEmpty else {
            print("文件夹里没有支持的图片")
            return 1
        }
        print("文件数：\(items.count)")

        var rows: [[String]] = [["stage", "metric", "value", "unit", "note"]]
        func addRow(_ stage: String, _ metric: String, _ value: String, _ unit: String, _ note: String = "") {
            rows.append([stage, metric, value, unit, note])
        }
        addRow("enumerate", "files", "\(items.count)", "files")
        addRow("enumerate", "duration", String(format: "%.1f", scanMS), "ms", "第一层枚举")

        let memoryStart = physFootprint()
        var memoryPeak = memoryStart

        func sampleMemory() {
            memoryPeak = max(memoryPeak, physFootprint())
        }

        // 首屏 20 张（4 线程）
        let firstCount = min(items.count, 20)
        let firstStart = CFAbsoluteTimeGetCurrent()
        var firstDurations = [Double](repeating: 0, count: firstCount)
        var firstImages = [CGImage?](repeating: nil, count: firstCount)
        let lock = NSLock()
        var nextIndex = 0
        let group = DispatchGroup()
        for _ in 0..<4 {
            group.enter()
            DispatchQueue.global(qos: .userInitiated).async {
                while true {
                    lock.lock()
                    let index = nextIndex
                    nextIndex += 1
                    lock.unlock()
                    if index >= firstCount { break }
                    let start = CFAbsoluteTimeGetCurrent()
                    firstImages[index] = ImageDecoder.thumbnail(url: items[index].decodeURL, maxPixel: 160, preferEmbedded: true)
                    firstDurations[index] = (CFAbsoluteTimeGetCurrent() - start) * 1000
                }
                group.leave()
            }
        }
        group.wait()
        let firstMS = (CFAbsoluteTimeGetCurrent() - firstStart) * 1000
        let firstSuccess = firstImages.compactMap { $0 }.count
        print(String(format: "首屏 %d 张缩略图：%.0f ms（%d/%d 成功）", firstCount, firstMS, firstSuccess, firstCount))
        addRow("firstScreen", "duration", String(format: "%.0f", firstMS), "ms", "\(firstCount) 张")
        sampleMemory()

        // 缩略图批量采样
        let thumbCount = min(items.count, limit ?? 100)
        let thumbStart = CFAbsoluteTimeGetCurrent()
        var thumbDurations = [Double](repeating: -1, count: thumbCount)
        var thumbSuccess = 0
        var thumbNext = 0
        let thumbLock = NSLock()
        let thumbGroup = DispatchGroup()
        for _ in 0..<4 {
            thumbGroup.enter()
            DispatchQueue.global(qos: .userInitiated).async {
                while true {
                    thumbLock.lock()
                    let index = thumbNext
                    thumbNext += 1
                    thumbLock.unlock()
                    if index >= thumbCount { break }
                    let started = CFAbsoluteTimeGetCurrent()
                    let image = autoreleasepool { ImageDecoder.thumbnail(url: items[index].decodeURL, maxPixel: 160, preferEmbedded: true) }
                    thumbDurations[index] = (CFAbsoluteTimeGetCurrent() - started) * 1000
                    if image != nil { thumbLock.lock(); thumbSuccess += 1; thumbLock.unlock() }
                }
                thumbGroup.leave()
            }
        }
        thumbGroup.wait()
        let thumbWall = (CFAbsoluteTimeGetCurrent() - thumbStart) * 1000
        let validThumbDurations = thumbDurations.filter { $0 >= 0 }
        let p50 = percentile(validThumbDurations, 0.5)
        let p95 = percentile(validThumbDurations, 0.95)
        let avg = validThumbDurations.isEmpty ? 0 : validThumbDurations.reduce(0, +) / Double(validThumbDurations.count)
        let estimate2000 = thumbCount == 0 ? 0 : thumbWall / Double(thumbCount) * 2000
        print(String(format: "缩略图 %d 张：成功 %d，P50 %.1f ms，P95 %.1f ms，平均 %.1f ms，总耗时 %.0f ms",
                     thumbCount, thumbSuccess, p50, p95, avg, thumbWall))
        print(String(format: "  → 折算 2000 张约 %.0f ms（4 线程）", estimate2000))
        addRow("thumbnails", "sample", "\(thumbCount)", "files")
        addRow("thumbnails", "success", "\(thumbSuccess)", "files")
        addRow("thumbnails", "p50", String(format: "%.1f", p50), "ms")
        addRow("thumbnails", "p95", String(format: "%.1f", p95), "ms")
        addRow("thumbnails", "avg", String(format: "%.1f", avg), "ms")
        addRow("thumbnails", "wall", String(format: "%.0f", thumbWall), "ms")
        addRow("thumbnails", "estimate2000", String(format: "%.0f", estimate2000), "ms", "4 线程")
        sampleMemory()

        // 2048 预览
        let previewCount = min(items.count, 20)
        var previewDurations: [Double] = []
        for item in items.prefix(previewCount) {
            let started = CFAbsoluteTimeGetCurrent()
            autoreleasepool {
                _ = ImageDecoder.thumbnail(url: item.decodeURL, maxPixel: 2048, preferEmbedded: false)
            }
            previewDurations.append((CFAbsoluteTimeGetCurrent() - started) * 1000)
        }
        let previewP50 = percentile(previewDurations, 0.5)
        let previewP95 = percentile(previewDurations, 0.95)
        print(String(format: "2048 预览 %d 张：P50 %.1f ms，P95 %.1f ms", previewCount, previewP50, previewP95))
        addRow("preview", "p50", String(format: "%.1f", previewP50), "ms")
        addRow("preview", "p95", String(format: "%.1f", previewP95), "ms")
        sampleMemory()

        // 100% 全解
        var fullDurations: [Double] = []
        let fullCount = min(items.count, 3)
        for item in items.prefix(fullCount) {
            let native = max(item.metadata?.pixelWidth ?? 0, item.metadata?.pixelHeight ?? 0)
            let started = CFAbsoluteTimeGetCurrent()
            autoreleasepool {
                if let image = ImageDecoder.fullImage(url: item.decodeURL, nativeMaxPixel: native > 0 ? native : 20000) {
                    renderToBitmap(image)
                }
            }
            fullDurations.append((CFAbsoluteTimeGetCurrent() - started) * 1000)
        }
        let fullP50 = percentile(fullDurations, 0.5)
        let fullP95 = percentile(fullDurations, 0.95)
        print(String(format: "100%% 全解 %d 张：P50 %.0f ms，P95 %.0f ms", fullCount, fullP50, fullP95))
        addRow("full", "p50", String(format: "%.0f", fullP50), "ms")
        addRow("full", "p95", String(format: "%.0f", fullP95), "ms")
        sampleMemory()

        // 缓存读写
        let cacheImage = firstImages.compactMap({ $0 }).first
        firstImages.removeAll()
        let diskCache = ThumbnailCache(limitBytes: Int64(AppSettings.shared.cacheLimitGB * 1_000_000_000))
        if let image = cacheImage {
            let key = "bench-\(UUID().uuidString)"
            let writeStart = CFAbsoluteTimeGetCurrent()
            _ = diskCache.store(image, for: key, kind: .thumb)
            let writeMS = (CFAbsoluteTimeGetCurrent() - writeStart) * 1000
            let hitStart = CFAbsoluteTimeGetCurrent()
            let hit = diskCache.image(for: key) != nil
            let hitMS = (CFAbsoluteTimeGetCurrent() - hitStart) * 1000
            print(String(format: "磁盘缓存：写入 %.1f ms，命中 %.2f ms（%@）", writeMS, hitMS, hit ? "成功" : "失败"))
            addRow("cache", "write", String(format: "%.1f", writeMS), "ms")
            addRow("cache", "hit", String(format: "%.2f", hitMS), "ms", hit ? "hit" : "miss")
        }

        let peakMB = Double(memoryPeak) / 1024 / 1024
        print(String(format: "内存峰值采样：%.0f MB", peakMB))
        addRow("memory", "peak", String(format: "%.0f", peakMB), "MB", "进程 phys_footprint 采样")

        // 写 CSV
        let outputURL: URL
        if let csvPath, !csvPath.isEmpty {
            outputURL = URL(fileURLWithPath: csvPath)
        } else {
            let desktop = fileManager.urls(for: .desktopDirectory, in: .userDomainMask).first
                ?? fileManager.temporaryDirectory
            let formatter = DateFormatter()
            formatter.dateFormat = "yyyyMMdd-HHmmss"
            outputURL = desktop.appendingPathComponent("影筛-bench-\(formatter.string(from: Date())).csv")
        }
        if writeCSV(rows, to: outputURL) {
            print("CSV：\(outputURL.path)")
        } else {
            print("CSV 写入失败：\(outputURL.path)")
        }

        print("""

        目标对照（参考 4.2）：
          打开文件夹 → 列表可见  < 1.0s       实测 \(String(format: "%.0f", scanMS)) ms
          首屏 20 张缩略图      < 2.0s       实测 \(String(format: "%.0f", firstMS)) ms
          单张缩略图 P95        < 400ms      实测 \(String(format: "%.1f", p95)) ms
          全量 2000 张缩略图    < 3 分钟     折算 \(String(format: "%.0f", estimate2000 / 1000)) 秒
          内存峰值              < 1.2GB      采样 \(String(format: "%.0f", peakMB)) MB
        """)
        return 0
    }

    static func percentile(_ values: [Double], _ p: Double) -> Double {
        guard !values.isEmpty else { return 0 }
        let sorted = values.sorted()
        let index = min(sorted.count - 1, max(0, Int((Double(sorted.count - 1) * p).rounded())))
        return sorted[index]
    }

    static func renderToBitmap(_ image: CGImage) {
        guard let context = CGContext(data: nil, width: image.width, height: image.height,
                                      bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            return
        }
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        _ = context.makeImage()
    }

    static func physFootprint() -> Int64 {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.stride / MemoryLayout<integer_t>.stride)
        let result = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { rebound in
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), rebound, &count)
            }
        }
        return result == KERN_SUCCESS ? Int64(info.phys_footprint) : 0
    }

    private static func writeCSV(_ rows: [[String]], to url: URL) -> Bool {
        let text = rows.map { row in
            row.map { value in
                "\"" + value.replacingOccurrences(of: "\"", with: "\"\"") + "\""
            }.joined(separator: ",")
        }.joined(separator: "\n")
        do {
            try text.data(using: .utf8)?.write(to: url)
            return true
        } catch {
            return false
        }
    }
}
