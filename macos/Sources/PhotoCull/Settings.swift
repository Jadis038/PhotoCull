// 影·筛 / PhotoCull — 设置（UserDefaults）
import Foundation

final class AppSettings {
    static let shared = AppSettings()

    private let defaults = UserDefaults.standard
    private let queue = DispatchQueue(label: "com.jadis038.photocull.settings")

    private init() {
        defaults.register(defaults: [
            Keys.operationMode: OperationMode.copy.rawValue,
            Keys.sortKey: SortKey.name.rawValue,
            Keys.sortOrder: SortOrder.ascending.rawValue,
            Keys.rememberLastFolder: true,
            Keys.restoreLastSession: false,
            Keys.checkForUpdates: true,
            Keys.frameNeedleEnabled: true,
            Keys.decodeConcurrency: 0,
            Keys.cacheLimitGB: 5.0,
            Keys.lastFolderPath: ""
        ])
        // 1.0.3：帧针模式首次出现时默认开启；旧配置没有版本号，视为旧版。
        if defaults.integer(forKey: Keys.settingsVersion) < 2 {
            defaults.set(true, forKey: Keys.frameNeedleEnabled)
            defaults.set(2, forKey: Keys.settingsVersion)
        }
    }

    private enum Keys {
        static let operationMode = "operationMode"
        static let sortKey = "sortKey"
        static let sortOrder = "sortOrder"
        static let rememberLastFolder = "rememberLastFolder"
        static let restoreLastSession = "restoreLastSession"
        static let checkForUpdates = "checkForUpdates"
        static let frameNeedleEnabled = "frameNeedleEnabled"
        static let settingsVersion = "settingsVersion"
        static let decodeConcurrency = "decodeConcurrency"
        static let cacheLimitGB = "cacheLimitGB"
        static let lastFolderPath = "lastFolderPath"
    }

    var operationMode: OperationMode {
        get { OperationMode(rawValue: defaults.string(forKey: Keys.operationMode) ?? "") ?? .copy }
        set { defaults.set(newValue.rawValue, forKey: Keys.operationMode) }
    }

    var sortKey: SortKey {
        get { SortKey(rawValue: defaults.string(forKey: Keys.sortKey) ?? "") ?? .name }
        set { defaults.set(newValue.rawValue, forKey: Keys.sortKey) }
    }

    var sortOrder: SortOrder {
        get { SortOrder(rawValue: defaults.string(forKey: Keys.sortOrder) ?? "") ?? .ascending }
        set { defaults.set(newValue.rawValue, forKey: Keys.sortOrder) }
    }

    var rememberLastFolder: Bool {
        get { defaults.bool(forKey: Keys.rememberLastFolder) }
        set { defaults.set(newValue, forKey: Keys.rememberLastFolder) }
    }

    var restoreLastSession: Bool {
        get { defaults.bool(forKey: Keys.restoreLastSession) }
        set { defaults.set(newValue, forKey: Keys.restoreLastSession) }
    }

    var frameNeedleEnabled: Bool {
        get { defaults.bool(forKey: Keys.frameNeedleEnabled) }
        set { defaults.set(newValue, forKey: Keys.frameNeedleEnabled) }
    }

    var checkForUpdates: Bool {
        get { defaults.bool(forKey: Keys.checkForUpdates) }
        set { defaults.set(newValue, forKey: Keys.checkForUpdates) }
    }

    /// 0 表示自动（SSD 4 线程 / 其他 2 线程）
    var decodeConcurrency: Int {
        get { defaults.integer(forKey: Keys.decodeConcurrency) }
        set { defaults.set(newValue, forKey: Keys.decodeConcurrency) }
    }

    var cacheLimitGB: Double {
        get { defaults.double(forKey: Keys.cacheLimitGB) }
        set { defaults.set(newValue, forKey: Keys.cacheLimitGB) }
    }

    var lastFolderPath: String {
        get { defaults.string(forKey: Keys.lastFolderPath) ?? "" }
        set { defaults.set(newValue, forKey: Keys.lastFolderPath) }
    }

    /// 自动并发：Apple Silicon 统一内存 + SSD，取 4（少量机械盘可手动调 2）
    var effectiveConcurrency: Int {
        if decodeConcurrency > 0 { return max(1, min(decodeConcurrency, 8)) }
        let cores = ProcessInfo.processInfo.activeProcessorCount
        if cores >= 8 { return 4 }
        return max(2, min(3, cores / 2))
    }

    var cacheLimitBytes: Int64 {
        Int64(max(0.5, cacheLimitGB) * 1_000_000_000)
    }
}

extension Notification.Name {
    static let photoCullSettingsChanged = Notification.Name("com.jadis038.photocull.settingsChanged")
}
