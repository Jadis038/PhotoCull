// 影·筛 / PhotoCull — 照片条目模型
import Foundation
import ImageIO

/// 单张照片在会话中的状态
enum MarkState: Equatable {
    case none          // 未标记
    case queued        // 已排入复制/移动队列，等待执行
    case processing    // 正在执行（复制/移动/删除）
    case marked        // 已移入筛选
    case error         // 上一次操作失败（短暂展示）

    var isMarkedOrPending: Bool {
        switch self {
        case .queued, .processing, .marked: return true
        case .none, .error: return false
        }
    }
}

/// 照片元数据（一次打开文件读取，不重复解码）
struct PhotoMetadata {
    var pixelWidth: Int?
    var pixelHeight: Int?
    var cameraMake: String?
    var cameraModel: String?
    var lens: String?
    var focalLength: Double?
    var focalLength35: Int?
    var aperture: Double?
    var exposureTime: Double?
    var iso: Int?
    var captureDate: Date?
    var captureDateText: String?
    var colorSpaceName: String?
    var profileName: String?
    var formatName: String?
    var orientation: CGImagePropertyOrientation = .up

    var resolutionText: String? {
        guard let w = pixelWidth, let h = pixelHeight else { return nil }
        return "\(w) × \(h)"
    }
}

/// 一条「已标记」记录：索引数据库中的一行
struct MarkRecord: Codable, Equatable {
    struct Companion: Codable, Equatable {
        var sourcePath: String
        var copyPath: String
    }

    var sourcePath: String
    var sourceSize: Int64
    var sourceMTime: Double
    var copyPath: String
    var copySize: Int64
    var copyMTime: Double
    var mode: OperationMode
    var markedAt: Double
    var companions: [Companion]

    var sourceURL: URL { URL(fileURLWithPath: sourcePath) }
    var copyURL: URL { URL(fileURLWithPath: copyPath) }

    var allCopyPaths: [String] {
        [copyPath] + companions.map { $0.copyPath }
    }
}

/// 会话中的一张照片
final class PhotoItem {
    let url: URL
    let name: String
    let ext: String
    let size: Int64
    let mtime: Date
    let kind: MediaKind
    let isDataless: Bool

    /// 实际源文件路径：移动模式下取消标记时可能因同名冲突改名
    var currentURL: URL

    var metadata: PhotoMetadata?
    /// 只读到拍摄时间时使用，避免抢占完整元数据
    var quickCaptureDate: Date?
    var markState: MarkState = .none
    /// 保护锁定（S 切换），持久化在 locks 表
    var isLocked: Bool = false
    /// 0~5 星，持久化在 stars 表
    var stars: Int = 0
    var markMode: OperationMode = .copy
    var record: MarkRecord?
    /// 索引中存在记录，但原文件已不存在
    var sourceMissing: Bool = false
    /// 缩略图是否已生成（磁盘缓存或已解码）
    var thumbReady: Bool = false
    /// 最近一次在 UI 中显示的顺序号（用于调试）
    var displayIndex: Int = 0

    init(url: URL, size: Int64, mtime: Date, isDataless: Bool = false) {
        self.url = url
        self.name = url.lastPathComponent
        self.ext = url.pathExtension.lowercased()
        self.size = size
        self.mtime = mtime
        self.kind = MediaKind.from(extension: ext)
        self.isDataless = isDataless
        self.currentURL = url
    }

    /// 解码用地址：源文件不存在但已标记时，回退到「筛选」里的副本
    var decodeURL: URL {
        if FileManager.default.fileExists(atPath: currentURL.path) { return currentURL }
        if let record, FileManager.default.fileExists(atPath: record.copyPath) { return record.copyURL }
        return currentURL
    }

    var isRaw: Bool { kind.isRaw }

    var captureDate: Date? {
        metadata?.captureDate
    }

    var displayCaptureDate: String? {
        metadata?.captureDateText
    }

    var canMark: Bool { FileTools.isReadableFile(url) || sourceMissing == false }

    /// 拍摄时间排序用的日期：优先 EXIF，其次文件修改时间
    var sortDate: Date { metadata?.captureDate ?? quickCaptureDate ?? mtime }

    /// 应用 EXIF 旋转后的原图显示尺寸（宽高在 90/270° 方向时互换）
    var nativeDisplaySize: CGSize? {
        guard let meta = metadata, let width = meta.pixelWidth, let height = meta.pixelHeight,
              width > 0, height > 0 else { return nil }
        let swap = [5, 6, 7, 8].contains(Int(meta.orientation.rawValue))
        return swap ? CGSize(width: height, height: width) : CGSize(width: width, height: height)
    }
}
