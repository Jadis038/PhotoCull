// 影·筛 / PhotoCull — RAW 色彩空间直读
// 优先级：EXIF/IFD0 40961 → TIFF 40961 → DNG AsShotProfileName；都没有就返回 nil（显示“—”）。
import Foundation

enum ColorSpaceReader {
    static func rawColorSpace(url: URL) -> String? {
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

        var colorSpaceValue: Int?
        var asShotProfile: String?
        var visited = Set<UInt32>()
        var budget = 32

        func walk(_ offset: UInt32, depth: Int) {
            guard budget > 0, depth <= 3, offset >= 8, UInt64(offset) < fileSize, !visited.contains(offset) else { return }
            visited.insert(offset)
            budget -= 1

            guard let countData = read(handle, UInt64(offset), 2), countData.count == 2 else { return }
            let count = Int(u16(countData, 0, little))
            guard count > 0, count < 512 else { return }
            let tableSize = 2 + count * 12 + 4
            guard let table = read(handle, UInt64(offset), tableSize), table.count >= tableSize else { return }

            var exifOffset: UInt32 = 0
            var subIFDs: [UInt32] = []

            for index in 0..<count {
                let base = 2 + index * 12
                let tag = u16(table, base, little)
                let type = u16(table, base + 2, little)
                let valueCount = Int(u32(table, base + 4, little))

                switch tag {
                case 0x8769:
                    exifOffset = u32(table, base + 8, little)
                case 0x014A:
                    subIFDs = offsets(table, entry: base, type: type, count: valueCount,
                                      little: little, handle: handle)
                case 0xA001:
                    if colorSpaceValue == nil, type == 3, valueCount >= 1 {
                        colorSpaceValue = Int(u16(table, base + 8, little))
                    }
                case 0xC6F6:
                    if asShotProfile == nil, type == 2, valueCount > 1 {
                        asShotProfile = ascii(table, entry: base, count: valueCount,
                                              little: little, handle: handle)
                    }
                default:
                    break
                }
            }

            if exifOffset > 8 { walk(exifOffset, depth: depth + 1) }
            for sub in subIFDs where sub >= 8 { walk(sub, depth: depth + 1) }
            let next = u32(table, 2 + count * 12, little)
            if next >= 8 { walk(next, depth: depth + 1) }
        }

        walk(ifd0, depth: 0)

        if let value = colorSpaceValue {
            switch value {
            case 1: return "sRGB"
            case 2: return "Adobe RGB"
            case 0xFFFF: return "未校准"
            default: return nil
            }
        }
        if let asShotProfile, !asShotProfile.isEmpty { return asShotProfile }
        return nil
    }

    private static func ascii(_ table: Data, entry: Int, count: Int,
                              little: Bool, handle: FileHandle) -> String? {
        var data: Data
        if count <= 4 {
            data = table.subdata(in: (entry + 8)..<(entry + 8 + count))
        } else {
            let offset = u32(table, entry + 8, little)
            guard let chunk = read(handle, UInt64(offset), count) else { return nil }
            data = chunk
        }
        if let end = data.firstIndex(of: 0) { data = data.prefix(upTo: end) }
        return String(data: data, encoding: .utf8) ?? String(data: data, encoding: .ascii)
    }

    private static func offsets(_ table: Data, entry: Int, type: UInt16, count: Int,
                                little: Bool, handle: FileHandle) -> [UInt32] {
        guard count > 0, count < 64 else { return [] }
        let size = type == 3 ? 2 : 4
        let total = size * count
        var raw: Data
        if total <= 4 {
            raw = table.subdata(in: (entry + 8)..<(entry + 8 + total))
        } else {
            let offset = u32(table, entry + 8, little)
            guard let chunk = read(handle, UInt64(offset), total) else { return [] }
            raw = chunk
        }
        var result: [UInt32] = []
        for index in 0..<count {
            if size == 2 {
                result.append(UInt32(u16(raw, index * 2, little)))
            } else {
                result.append(u32(raw, index * 4, little))
            }
        }
        return result
    }

    private static func read(_ handle: FileHandle, _ offset: UInt64, _ count: Int) -> Data? {
        guard count > 0 else { return Data() }
        do {
            try handle.seek(toOffset: offset)
            return try handle.read(upToCount: count)
        } catch { return nil }
    }

    private static func u16(_ data: Data, _ offset: Int, _ little: Bool) -> UInt16 {
        guard offset + 2 <= data.count else { return 0 }
        let a = UInt16(data[offset]), b = UInt16(data[offset + 1])
        return little ? (a | (b << 8)) : ((a << 8) | b)
    }

    private static func u32(_ data: Data, _ offset: Int, _ little: Bool) -> UInt32 {
        guard offset + 4 <= data.count else { return 0 }
        let a = UInt32(data[offset]), b = UInt32(data[offset + 1])
        let c = UInt32(data[offset + 2]), d = UInt32(data[offset + 3])
        return little ? (a | (b << 8) | (c << 16) | (d << 24)) : ((a << 24) | (b << 16) | (c << 8) | d)
    }
}
