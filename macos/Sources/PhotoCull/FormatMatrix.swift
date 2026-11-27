// 影·筛 / PhotoCull — 格式测试矩阵（逐个真实文件验证打开与解码）
import Foundation
import ImageIO
import CoreGraphics

enum FormatMatrix {
    static func run(folder: URL, csvPath: String?) -> Int32 {
        let scan: ScanResult
        do {
            scan = try Scanner.scan(folder: folder)
        } catch {
            print("扫描失败：\(error.localizedDescription)")
            return 1
        }
        guard !scan.items.isEmpty else {
            print("文件夹里没有支持的图片")
            return 1
        }

        var rows: [[String]] = [[
            "序号", "文件名", "扩展名", "大小", "可打开", "解码类型", "像素宽", "像素高", "方向",
            "缩略图ms", "预览ms", "全解ms", "机型", "镜头", "拍摄时间", "ISO", "光圈", "快门", "色彩空间"
        ]]

        var openable = 0
        var failedNames: [String] = []
        var totalMS: Double = 0

        for (index, item) in scan.items.enumerated() {
            autoreleasepool {
                let start = CFAbsoluteTimeGetCurrent()
                let source = CGImageSourceCreateWithURL(item.url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary)
                var type = "—"
                var width = 0
                var height = 0
                var orientation = 1
                if let source {
                    type = (CGImageSourceGetType(source) as String?) ?? "—"
                    if let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] {
                        width = props[kCGImagePropertyPixelWidth] as? Int ?? 0
                        height = props[kCGImagePropertyPixelHeight] as? Int ?? 0
                        orientation = props[kCGImagePropertyOrientation] as? Int ?? 1
                    }
                }

                var thumbMS = -1.0
                var previewMS = -1.0
                var fullMS = -1.0
                var thumbOK = false
                var previewOK = false

                if let source {
                    var t = CFAbsoluteTimeGetCurrent()
                    let thumb = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                        kCGImageSourceCreateThumbnailFromImageAlways: false,
                        kCGImageSourceCreateThumbnailWithTransform: true,
                        kCGImageSourceThumbnailMaxPixelSize: 160
                    ] as CFDictionary)
                    thumbMS = (CFAbsoluteTimeGetCurrent() - t) * 1000
                    thumbOK = thumb != nil
                    t = CFAbsoluteTimeGetCurrent()
                    let preview = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                        kCGImageSourceCreateThumbnailFromImageIfAbsent: true,
                        kCGImageSourceCreateThumbnailFromImageAlways: true,
                        kCGImageSourceCreateThumbnailWithTransform: true,
                        kCGImageSourceThumbnailMaxPixelSize: 2048
                    ] as CFDictionary)
                    previewOK = preview != nil
                    previewMS = (CFAbsoluteTimeGetCurrent() - t) * 1000
                    t = CFAbsoluteTimeGetCurrent()
                    if let full = ImageDecoder.fullImage(url: item.url, nativeMaxPixel: max(width, height)) {
                        Bench.renderToBitmap(full)
                        fullMS = (CFAbsoluteTimeGetCurrent() - t) * 1000
                    }
                }

                let metadata = MetadataReader.read(from: item.url)
                let cost = (CFAbsoluteTimeGetCurrent() - start) * 1000
                totalMS += cost
                if source != nil && (thumbOK || previewOK) { openable += 1 } else { failedNames.append(item.name) }

                rows.append([
                    "\(index + 1)", item.name, item.ext.uppercased(), Fmt.bytes(item.size),
                    (source != nil ? "是" : "否"), type, "\(width)", "\(height)", "\(orientation)",
                    String(format: "%.0f", thumbMS), String(format: "%.0f", previewMS), String(format: "%.0f", fullMS),
                    MetadataFormat.camera(metadata), MetadataFormat.lens(metadata), MetadataFormat.captureTime(metadata),
                    MetadataFormat.iso(metadata), MetadataFormat.aperture(metadata), MetadataFormat.shutter(metadata),
                    MetadataFormat.colorSpace(metadata)
                ])
            }
        }

        let outputURL: URL
        if let csvPath, !csvPath.isEmpty {
            outputURL = URL(fileURLWithPath: csvPath)
        } else {
            let desktop = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first
                ?? FileManager.default.temporaryDirectory
            outputURL = desktop.appendingPathComponent("影筛-格式矩阵.csv")
        }
        let text = rows.map { row in
            row.map { "\"" + $0.replacingOccurrences(of: "\"", with: "\"\"") + "\"" }.joined(separator: ",")
        }.joined(separator: "\n")
        do {
            try text.data(using: .utf8)?.write(to: outputURL)
        } catch {
            print("CSV 写入失败：\(error.localizedDescription)")
        }

        print("""
        格式矩阵：\(scan.items.count) 个文件，可打开 \(openable) 个，失败 \(failedNames.count) 个
        总耗时：\(String(format: "%.1f", totalMS / 1000)) 秒
        CSV：\(outputURL.path)
        """)
        if !failedNames.isEmpty {
            print("无法打开：")
            for name in failedNames { print("  - \(name)") }
        }
        return 0
    }
}
