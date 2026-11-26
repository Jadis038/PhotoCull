// 影·筛 / PhotoCull — RAW 快速缩略图：直接抽取相机内嵌 JPEG，绕开完整 RAW 解码
// 缩略图只求快，质量让位于速度；大图预览 / 100% 仍走高质量解码。
import Foundation
import ImageIO
import CoreGraphics

enum ThumbProbe {
    static func run(folder: URL) -> Int32 {
        let scan: ScanResult
        do { scan = try Scanner.scan(folder: folder) } catch {
            print("扫描失败：\(error.localizedDescription)"); return 1
        }
        var fastCount = 0
        var totalFastMS = 0.0
        var totalImageIOMS = 0.0
        for item in scan.items {
            let t1 = CFAbsoluteTimeGetCurrent()
            let fast = FastThumbnail.image(url: item.decodeURL, maxPixel: 160)
            let fastMS = (CFAbsoluteTimeGetCurrent() - t1) * 1000
            let t2 = CFAbsoluteTimeGetCurrent()
            let normal = ImageDecoder.thumbnail(url: item.decodeURL, maxPixel: 160, preferEmbedded: true)
            let normalMS = (CFAbsoluteTimeGetCurrent() - t2) * 1000
            if fast != nil { fastCount += 1 }
            totalFastMS += fastMS
            totalImageIOMS += normalMS
            print(String(format: "%@ | fast=%@ %.2fms | imageio=%@ %.1fms | %@",
                         item.name, fast != nil ? "YES" : "no", fastMS,
                         normal != nil ? "YES" : "no", normalMS,
                         item.ext.uppercased()))
        }
        print(String(format: "内嵌预览命中 %d/%d，fast 合计 %.0fms，ImageIO 合计 %.0fms",
                     fastCount, scan.items.count, totalFastMS, totalImageIOMS))
        return 0
    }
}

enum FastThumbnail {
    /// TIFF 容器类 RAW
    private static let tiffRAWExtensions: Set<String> = [
        "nef", "cr2", "arw", "srf", "sr2", "dng", "orf", "rw2", "raw",
        "pef", "srw", "erf", "mef", "mos", "mrw", "nrw", "rwl", "kdc",
        "dcr", "iiq", "cap", "3fr"
    ]

    static func image(url: URL, maxPixel: Int) -> CGImage? {
        guard let data = embeddedJPEGData(url: url) else { return nil }
        guard let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary) else {
            return nil
        }
        return CGImageSourceCreateThumbnailAtIndex(source, 0, [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel,
            kCGImageSourceShouldCacheImmediately: false
        ] as CFDictionary)
    }

    static func embeddedJPEGData(url: URL) -> Data? {
        let ext = url.pathExtension.lowercased()
        if tiffRAWExtensions.contains(ext) {
            if let data = TIFFPreviewExtractor.extract(url: url) { return data }
        }
        if ext == "raf", let data = RAFPreviewExtractor.extract(url: url) { return data }
        if ext == "cr3", let data = BoxPreviewExtractor.extract(url: url) { return data }
        return nil
    }
}

// MARK: - TIFF 容器

private enum TIFFPreviewExtractor {
    static func extract(url: URL) -> Data? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let header = read(handle, 0, 16), header.count >= 8 else { return nil }

        let little: Bool
        if header[0] == 0x49, header[1] == 0x49 { little = true }
        else if header[0] == 0x4D, header[1] == 0x4D { little = false }
        else { return nil }
        guard u16(header, 2, little) == 42 else { return nil }
        let ifd0 = u32(header, 4, little)
        guard ifd0 >= 8 else { return nil }

        let fileSize = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)??.uint64Value ?? 0
        var candidates: [Data] = []
        var visited = Set<UInt32>()
        var budget = 32

        func walk(_ offset: UInt32, _ depth: Int) {
            guard budget > 0, depth <= 3, offset >= 8, UInt64(offset) < fileSize, !visited.contains(offset) else { return }
            visited.insert(offset)
            budget -= 1

            guard let countData = read(handle, UInt64(offset), 2), countData.count == 2 else { return }
            let count = Int(u16(countData, 0, little))
            guard count > 0, count < 512 else { return }
            let tableSize = 2 + count * 12 + 4
            guard let table = read(handle, UInt64(offset), tableSize), table.count >= tableSize else { return }

            var entries: [(tag: UInt16, type: UInt16, count: Int, value: UInt32, entry: Int)] = []
            entries.reserveCapacity(count)
            for index in 0..<count {
                let base = 2 + index * 12
                entries.append((u16(table, base, little), u16(table, base + 2, little),
                                Int(u32(table, base + 4, little)), u32(table, base + 8, little), base))
            }

            var compression = 1
            var stripOffsets: [UInt32] = []
            var stripCounts: [UInt32] = []
            var subIFDs: [UInt32] = []

            for entry in entries {
                switch entry.tag {
                case 0x0103:
                    compression = Int(u16(table, entry.entry + 8, little) & 0xFFFF)
                case 0x0201:
                    if let lengthEntry = entries.first(where: { $0.tag == 0x0202 }) {
                        let length = lengthEntry.value
                        if length > 512, length < 32 * 1024 * 1024,
                           let data = read(handle, UInt64(entry.value), Int(length)), isJPEG(data) {
                            candidates.append(data)
                        }
                    }
                case 0x0111:
                    stripOffsets = values(table: table, entry: entry.entry, type: entry.type, count: entry.count,
                                          little: little, handle: handle)
                case 0x0117:
                    stripCounts = values(table: table, entry: entry.entry, type: entry.type, count: entry.count,
                                         little: little, handle: handle)
                case 0x014A:
                    subIFDs = values(table: table, entry: entry.entry, type: entry.type, count: entry.count,
                                     little: little, handle: handle)
                default:
                    break
                }
            }

            if compression == 7, !stripOffsets.isEmpty {
                var data = Data()
                for (index, offset) in stripOffsets.enumerated() {
                    let length = index < stripCounts.count ? stripCounts[index] : 0
                    if length > 0, length < 32 * 1024 * 1024,
                       let chunk = read(handle, UInt64(offset), Int(length)) {
                        data.append(chunk)
                    }
                }
                if isJPEG(data) { candidates.append(data) }
            }

            for sub in subIFDs where sub >= 8 { walk(sub, depth + 1) }

            let next = u32(table, 2 + count * 12, little)
            if next >= 8 { walk(next, depth + 1) }
        }

        walk(ifd0, 0)
        guard !candidates.isEmpty else { return nil }
        candidates.sort { $0.count < $1.count }
        for candidate in candidates.prefix(6) where candidate.count >= 1024 {
            return candidate
        }
        return candidates.first
    }

    private static func values(table: Data, entry: Int, type: UInt16, count: Int,
                               little: Bool, handle: FileHandle) -> [UInt32] {
        guard count > 0, count < 10000 else { return [] }
        let elementSize: Int
        switch type {
        case 3: elementSize = 2
        case 4: elementSize = 4
        default: return []
        }
        let total = elementSize * count
        var raw: Data
        if total <= 4 {
            raw = Data()
            for index in 0..<total {
                let shift = little ? (entry + 8 + index) : (entry + 8 + (total - 1 - index))
                raw.append(table[shift])
            }
        } else {
            let offset = u32(table, entry + 8, little)
            guard let chunk = read(handle, UInt64(offset), total) else { return [] }
            raw = chunk
        }
        var result: [UInt32] = []
        result.reserveCapacity(count)
        for index in 0..<count {
            let offset = index * elementSize
            if elementSize == 2 {
                result.append(UInt32(u16(raw, offset, little)))
            } else {
                result.append(u32(raw, offset, little))
            }
        }
        return result
    }

    private static func read(_ handle: FileHandle, _ offset: UInt64, _ count: Int) -> Data? {
        guard count > 0 else { return Data() }
        do {
            try handle.seek(toOffset: offset)
            return try handle.read(upToCount: count)
        } catch {
            return nil
        }
    }

    private static func isJPEG(_ data: Data) -> Bool {
        data.count > 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF
    }

    static func u16(_ data: Data, _ offset: Int, _ little: Bool) -> UInt16 {
        guard offset >= 0, offset + 2 <= data.count else { return 0 }
        let b0 = UInt16(data[offset]), b1 = UInt16(data[offset + 1])
        return little ? (b0 | (b1 << 8)) : ((b0 << 8) | b1)
    }

    static func u32(_ data: Data, _ offset: Int, _ little: Bool) -> UInt32 {
        guard offset >= 0, offset + 4 <= data.count else { return 0 }
        let b0 = UInt32(data[offset]), b1 = UInt32(data[offset + 1])
        let b2 = UInt32(data[offset + 2]), b3 = UInt32(data[offset + 3])
        return little ? (b0 | (b1 << 8) | (b2 << 16) | (b3 << 24))
                      : ((b0 << 24) | (b1 << 16) | (b2 << 8) | b3)
    }
}

// MARK: - 富士 RAF

private enum RAFPreviewExtractor {
    static func extract(url: URL) -> Data? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        try? handle.seek(toOffset: 0)
        guard let data = try? handle.read(upToCount: 128), data.count >= 96 else { return nil }
        let magic = String(data: data.prefix(15), encoding: .ascii) ?? ""
        guard magic.hasPrefix("FUJIFILMCCD-RAW") else { return nil }

        for pair in [(84, 88), (92, 96)] {
            let offset = u32BE(data, pair.0)
            let length = u32BE(data, pair.1)
            guard offset > 0, length > 1024, length < 32 * 1024 * 1024 else { continue }
            try? handle.seek(toOffset: UInt64(offset))
            if let chunk = try? handle.read(upToCount: Int(length)), chunk.count >= 4,
               chunk[0] == 0xFF, chunk[1] == 0xD8, chunk[2] == 0xFF {
                return chunk
            }
        }
        return nil
    }

    private static func u32BE(_ data: Data, _ offset: Int) -> UInt32 {
        guard offset + 4 <= data.count else { return 0 }
        return (UInt32(data[offset]) << 24) | (UInt32(data[offset + 1]) << 16)
             | (UInt32(data[offset + 2]) << 8) | UInt32(data[offset + 3])
    }
}

// MARK: - 佳能 CR3（ISO BMFF，扫描 moov 里的内嵌 JPEG）

private enum BoxPreviewExtractor {
    static func extract(url: URL) -> Data? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        let fileSize = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)??.uint64Value ?? 0
        let readSize = Int(min(fileSize, 12 * 1024 * 1024))
        guard readSize > 1024 else { return nil }
        try? handle.seek(toOffset: 0)
        guard let data = try? handle.read(upToCount: readSize), data.count > 1024 else { return nil }

        var candidates: [(offset: Int, length: Int)] = []
        let bytes = [UInt8](data)
        var index = 0
        while index + 3 < bytes.count {
            if bytes[index] == 0xFF, bytes[index + 1] == 0xD8, bytes[index + 2] == 0xFF {
                var end = index + 3
                while end + 1 < bytes.count {
                    if bytes[end] == 0xFF, bytes[end + 1] == 0xD9 {
                        let length = end + 2 - index
                        if length > 2048 && length < 20 * 1024 * 1024 {
                            candidates.append((index, length))
                        }
                        break
                    }
                    end += 1
                }
                index = max(index + 3, end + 2)
            } else {
                index += 1
            }
        }
        guard !candidates.isEmpty else { return nil }
        candidates.sort { $0.length < $1.length }
        guard let best = candidates.first else { return nil }
        return data.subdata(in: best.offset..<(best.offset + best.length))
    }
}
