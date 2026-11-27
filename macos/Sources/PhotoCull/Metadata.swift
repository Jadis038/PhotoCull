// 影·筛 / PhotoCull — 元数据读取（只打开一次文件头，不做全解码）
import Foundation
import ImageIO

enum MetadataReader {
    /// 读取 EXIF / TIFF 元数据。RAW 与普通图片共用 ImageIO，不触发 demosaic。
    static func read(from url: URL) -> PhotoMetadata? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] else {
            return nil
        }

        var meta = PhotoMetadata()

        if let width = number(properties[kCGImagePropertyPixelWidth]) { meta.pixelWidth = Int(width) }
        if let height = number(properties[kCGImagePropertyPixelHeight]) { meta.pixelHeight = Int(height) }
        if let orientation = number(properties[kCGImagePropertyOrientation]), let value = UInt32(exactly: orientation) {
            meta.orientation = CGImagePropertyOrientation(rawValue: value) ?? .up
        } else {
            meta.orientation = .up
        }

        let tiff = properties[kCGImagePropertyTIFFDictionary] as? [CFString: Any]
        let exif = properties[kCGImagePropertyExifDictionary] as? [CFString: Any]

        meta.cameraMake = string(tiff?[kCGImagePropertyTIFFMake]) ?? string(exif?[kCGImagePropertyExifMakerNote])
        meta.cameraModel = string(tiff?[kCGImagePropertyTIFFModel])
        meta.lens = string(exif?[kCGImagePropertyExifLensModel])
        if meta.lens == nil { meta.lens = string(exif?[kCGImagePropertyExifLensMake]) }

        meta.focalLength = double(exif?[kCGImagePropertyExifFocalLength])
        meta.focalLength35 = int(exif?[kCGImagePropertyExifFocalLenIn35mmFilm])
        meta.aperture = double(exif?[kCGImagePropertyExifFNumber])
        meta.exposureTime = double(exif?[kCGImagePropertyExifExposureTime])
        meta.iso = isoValue(exif?[kCGImagePropertyExifISOSpeedRatings])

        if let dateText = string(exif?[kCGImagePropertyExifDateTimeOriginal]) {
            meta.captureDateText = normalizeExifDate(dateText)
            meta.captureDate = parseExifDate(dateText)
        } else if let dateText = string(tiff?[kCGImagePropertyTIFFDateTime]) {
            meta.captureDateText = normalizeExifDate(dateText)
            meta.captureDate = parseExifDate(dateText)
        }

        if let exifWidth = number(exif?[kCGImagePropertyExifPixelXDimension]),
           let exifHeight = number(exif?[kCGImagePropertyExifPixelYDimension]),
           (meta.pixelWidth ?? 0) == 0 {
            meta.pixelWidth = Int(exifWidth)
            meta.pixelHeight = Int(exifHeight)
        }

        if let width = meta.pixelWidth, let height = meta.pixelHeight, width > 0, height > 0 {
            // 有的 RAW 只给传感器尺寸，这里保留原始值
        }

        meta.colorSpaceName = string(properties[kCGImagePropertyProfileName])
        if meta.colorSpaceName == nil, let model = number(properties[kCGImagePropertyColorModel]) {
            meta.colorSpaceName = colorModelName(Int(model))
        }
        meta.formatName = formatLabel(for: url.pathExtension.lowercased(), typeIdentifier: CGImageSourceGetType(source) as String?)

        return meta
    }

    /// 只读取拍摄日期（用于按拍摄时间排序的后台补全）
    static func readCaptureDate(from url: URL) -> Date? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] else {
            return nil
        }
        let exif = properties[kCGImagePropertyExifDictionary] as? [CFString: Any]
        let tiff = properties[kCGImagePropertyTIFFDictionary] as? [CFString: Any]
        if let text = string(exif?[kCGImagePropertyExifDateTimeOriginal]) ?? string(tiff?[kCGImagePropertyTIFFDateTime]) {
            return parseExifDate(text)
        }
        return nil
    }

    /// 读取拍摄日期的快速路径：RAW 优先从内嵌 JPEG 的 EXIF 读取，避免完整 RAW 元数据开销
    static func readCaptureDateFast(from url: URL) -> Date? {
        if MediaKind.from(extension: url.pathExtension.lowercased()).isRaw,
           let data = FastThumbnail.embeddedJPEGData(url: url),
           let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
           let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] {
            let exif = properties[kCGImagePropertyExifDictionary] as? [CFString: Any]
            if let text = string(exif?[kCGImagePropertyExifDateTimeOriginal]) {
                return parseExifDate(text)
            }
            let tiff = properties[kCGImagePropertyTIFFDictionary] as? [CFString: Any]
            if let text = string(tiff?[kCGImagePropertyTIFFDateTime]) {
                return parseExifDate(text)
            }
        }
        return readCaptureDate(from: url)
    }

    /// 判断文件能不能被系统图像框架打开（不完整解码）
    static func canDecode(url: URL) -> Bool {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary) else {
            return false
        }
        return CGImageSourceGetCount(source) > 0
    }

    // MARK: - 值转换

    private static func number(_ value: Any?) -> Double? {
        if let n = value as? NSNumber { return n.doubleValue }
        if let d = value as? Double { return d }
        if let i = value as? Int { return Double(i) }
        if let s = value as? String { return Double(s) }
        return nil
    }

    private static func double(_ value: Any?) -> Double? {
        if let n = value as? NSNumber { return n.doubleValue }
        if let d = value as? Double { return d }
        return nil
    }

    private static func int(_ value: Any?) -> Int? {
        if let n = value as? NSNumber { return n.intValue }
        if let i = value as? Int { return i }
        return nil
    }

    private static func string(_ value: Any?) -> String? {
        if let s = value as? String, !s.isEmpty { return s }
        return nil
    }

    private static func isoValue(_ value: Any?) -> Int? {
        if let n = value as? NSNumber { return n.intValue }
        if let array = value as? [NSNumber], let first = array.first { return first.intValue }
        if let array = value as? [Any], let first = array.first as? NSNumber { return first.intValue }
        return nil
    }

    private static func colorModelName(_ value: Int) -> String? {
        switch value {
        case 1: return "Gray"
        case 2: return "RGB"
        case 3: return "YCbCr"
        case 4: return "CMYK"
        case 5: return "Lab"
        case 6: return "DeviceN"
        default: return nil
        }
    }

    private static func formatLabel(for ext: String, typeIdentifier: String?) -> String {
        switch ext {
        case "jpg", "jpeg", "jpe", "jfif": return "JPEG"
        case "png": return "PNG"
        case "tif", "tiff": return "TIFF"
        case "bmp": return "BMP"
        case "gif": return "GIF"
        case "webp": return "WebP"
        case "heic", "heif", "hif": return "HEIC"
        case "avif": return "AVIF"
        case "jxl": return "JPEG XL"
        case "exr": return "OpenEXR"
        default:
            if let label = MediaKind.rawBrandLabel(ext: ext) { return label }
            if let typeIdentifier, typeIdentifier.contains("raw") { return "RAW" }
            return ext.uppercased()
        }
    }

    // MARK: - 日期

    private static let exifFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy:MM:dd HH:mm:ss"
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone.current
        return formatter
    }()

    private static func parseExifDate(_ text: String) -> Date? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return exifFormatter.date(from: trimmed)
    }

    private static func normalizeExifDate(_ text: String) -> String {
        // "2026:10:27 21:47:17" -> "2026-10-27 21:47:17"
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 10 else { return trimmed }
        var chars = Array(trimmed)
        chars[4] = "-"
        chars[7] = "-"
        return String(chars)
    }
}

// MARK: - 展示格式化

enum MetadataFormat {
    static func camera(_ meta: PhotoMetadata?) -> String {
        guard let meta else { return "—" }
        if let model = meta.cameraModel, !model.isEmpty {
            var text = model
            if let make = meta.cameraMake, !make.isEmpty, !model.uppercased().contains(make.prefix(4).uppercased()) {
                text = "\(make) \(model)"
            }
            return text
        }
        return "—"
    }

    static func lens(_ meta: PhotoMetadata?) -> String {
        guard let lens = meta?.lens, !lens.isEmpty else { return "—" }
        return lens
    }

    static func focal(_ meta: PhotoMetadata?) -> String {
        guard let value = meta?.focalLength, value > 0 else { return "—" }
        let main = String(format: "%.0f mm", value)
        if let eq = meta?.focalLength35, eq > 0, Int(value.rounded()) != eq {
            return "\(main)（等效 \(eq) mm）"
        }
        return main
    }

    static func aperture(_ meta: PhotoMetadata?) -> String {
        guard let value = meta?.aperture, value > 0 else { return "—" }
        if value.rounded() == value {
            return String(format: "f/%.0f", value)
        }
        return String(format: "f/%.1f", value)
    }

    static func shutter(_ meta: PhotoMetadata?) -> String {
        guard let value = meta?.exposureTime, value > 0 else { return "—" }
        if value >= 1 {
            return String(format: "%.1f 秒", value)
        }
        let denom = 1.0 / value
        return "1/\(Int(denom.rounded())) 秒"
    }

    static func iso(_ meta: PhotoMetadata?) -> String {
        guard let value = meta?.iso else { return "—" }
        return "\(value)"
    }

    static func captureTime(_ meta: PhotoMetadata?) -> String {
        guard let text = meta?.captureDateText, !text.isEmpty else { return "—" }
        return text
    }

    static func resolution(_ meta: PhotoMetadata?) -> String {
        guard let w = meta?.pixelWidth, let h = meta?.pixelHeight, w > 0, h > 0 else { return "—" }
        return "\(w) × \(h)"
    }

    static func colorSpace(_ meta: PhotoMetadata?) -> String {
        guard let name = meta?.colorSpaceName, !name.isEmpty else { return "—" }
        return name
    }

    static func format(_ meta: PhotoMetadata?) -> String {
        guard let name = meta?.formatName, !name.isEmpty else { return "—" }
        return name
    }
}
