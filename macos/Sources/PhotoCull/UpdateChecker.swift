// 影·筛 / PhotoCull — GitHub 更新检测与安装
import Foundation
import AppKit

struct UpdateRelease {
    let version: String
    let tag: String
    let title: String
    let notes: String
    let assetName: String
    let downloadURL: URL
    let publishedAt: Date?
    let prerelease: Bool
}

enum UpdateError: LocalizedError {
    case invalidResponse
    case badAsset(String)
    case downloadFailed(String)
    case installFailed(String)
    case notWritable(String)
    case versionTooLow(String)

    var errorDescription: String? {
        switch self {
        case .invalidResponse: return "GitHub 返回了无法识别的数据"
        case .badAsset(let message): return "更新包无效：\(message)"
        case .downloadFailed(let message): return "下载失败：\(message)"
        case .installFailed(let message): return "安装失败：\(message)"
        case .notWritable(let path): return "没有权限写入应用目录：\(path)\n请先把「影·筛」拖到「应用程序」文件夹后再更新。"
        case .versionTooLow(let version): return "下载的版本（\(version)）不高于当前版本。"
        }
    }
}

final class UpdateChecker {
    static let shared = UpdateChecker()

    let repository = "Jadis038/PhotoCull"
    let currentVersion = AppInfo.version
    /// --update-auto：无人值守下载/安装（自动化测试用）
    var autoUpdateMode = false

    private let session: URLSession
    private var activeDownloader: UpdateDownloader?

    init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 180
        configuration.httpAdditionalHeaders = ["User-Agent": "PhotoCull-macOS/\(AppInfo.version)"]
        session = URLSession(configuration: configuration)
    }

    // MARK: - 检查

    /// 只接受带 macOS DMG 的 GitHub Release。Windows 的 MSI release 会被忽略。
    /// macOS 的 release 以 prerelease 形式发布，避免影响 Windows 端的 /releases/latest；
    /// 因此这里默认包含 prerelease。
    func checkForUpdate(includePrerelease: Bool = true,
                        completion: @escaping (Result<UpdateRelease?, Error>) -> Void) {
        guard let url = URL(string: "https://api.github.com/repos/\(repository)/releases?per_page=30") else {
            completion(.failure(UpdateError.invalidResponse))
            return
        }
        var request = URLRequest(url: url)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("PhotoCull-macOS/\(currentVersion)", forHTTPHeaderField: "User-Agent")

        session.dataTask(with: request) { [weak self] data, response, error in
            guard let self else { return }
            if let error {
                DispatchQueue.main.async { completion(.failure(error)) }
                return
            }
            guard let http = response as? HTTPURLResponse,
                  (200..<300).contains(http.statusCode),
                  let data else {
                DispatchQueue.main.async { completion(.failure(UpdateError.invalidResponse)) }
                return
            }
            do {
                let release = try self.parseReleases(data, includePrerelease: includePrerelease)
                DispatchQueue.main.async { completion(.success(release)) }
            } catch {
                DispatchQueue.main.async { completion(.failure(error)) }
            }
        }.resume()
    }

    private func parseReleases(_ data: Data, includePrerelease: Bool) throws -> UpdateRelease? {
        guard let list = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else {
            throw UpdateError.invalidResponse
        }

        var candidates: [UpdateRelease] = []
        for item in list {
            if item["draft"] as? Bool == true { continue }
            let prerelease = item["prerelease"] as? Bool ?? false
            if prerelease && !includePrerelease { continue }

            let tag = item["tag_name"] as? String ?? ""
            let assets = item["assets"] as? [[String: Any]] ?? []
            let dmgs = assets.filter { asset in
                let name = (asset["name"] as? String ?? "").lowercased()
                return name.hasSuffix(".dmg")
            }
            guard !dmgs.isEmpty else { continue }
            let macDMGs = dmgs.filter { (($0["name"] as? String ?? "").lowercased()).contains("macos") }
            guard tag.lowercased().contains("macos") || !macDMGs.isEmpty else { continue }

            let asset = macDMGs.first(where: { (($0["name"] as? String ?? "").lowercased()).contains("arm64") })
                ?? macDMGs.first
                ?? dmgs.first
            guard let asset,
                  let name = asset["name"] as? String,
                  let urlString = asset["browser_download_url"] as? String,
                  let downloadURL = URL(string: urlString) else { continue }

            guard let version = version(fromTag: tag) ?? version(fromName: name) else { continue }
            guard Self.compare(version, currentVersion) == .orderedDescending else { continue }

            let notes = (item["body"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            var published: Date?
            if let dateString = item["published_at"] as? String {
                published = ISO8601DateFormatter().date(from: dateString)
            }
            candidates.append(UpdateRelease(
                version: version,
                tag: tag,
                title: item["name"] as? String ?? tag,
                notes: notes.isEmpty ? "（该版本没有提供更新说明）" : notes,
                assetName: name,
                downloadURL: downloadURL,
                publishedAt: published,
                prerelease: prerelease
            ))
        }

        candidates.sort { Self.compare($0.version, $1.version) == .orderedDescending }
        return candidates.first
    }

    private func version(fromTag tag: String) -> String? {
        let numbers = tag.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
        guard numbers.count >= 2 else { return nil }
        return numbers.prefix(3).map(String.init).joined(separator: ".")
    }

    private func version(fromName name: String) -> String? {
        let numbers = name.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
        guard numbers.count >= 2 else { return nil }
        return numbers.prefix(3).map(String.init).joined(separator: ".")
    }

    static func versionParts(_ version: String) -> [Int] {
        version.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
    }

    static func compare(_ lhs: String, _ rhs: String) -> ComparisonResult {
        var a = versionParts(lhs)
        var b = versionParts(rhs)
        while a.count < 3 { a.append(0) }
        while b.count < 3 { b.append(0) }
        for index in 0..<max(a.count, b.count) {
            let av = index < a.count ? a[index] : 0
            let bv = index < b.count ? b[index] : 0
            if av < bv { return .orderedAscending }
            if av > bv { return .orderedDescending }
        }
        return .orderedSame
    }

    // MARK: - 下载

    func download(_ release: UpdateRelease,
                  progress: @escaping (Double) -> Void,
                  completion: @escaping (Result<URL, Error>) -> Void) {
        guard activeDownloader == nil else {
            completion(.failure(UpdateError.downloadFailed("已有下载任务在进行")))
            return
        }
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhotoCullUpdate-\(UUID().uuidString)", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        } catch {
            completion(.failure(error))
            return
        }
        let destination = directory.appendingPathComponent(release.assetName)
        let downloader = UpdateDownloader()
        activeDownloader = downloader
        downloader.start(url: release.downloadURL, destination: destination, progress: progress) { [weak self] result in
            self?.activeDownloader = nil
            DispatchQueue.main.async { completion(result) }
        }
    }

    // MARK: - 安装

    func installDownloadedUpdate(dmgURL: URL,
                                 completion: @escaping (Result<Void, Error>) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            do {
                try self.performInstall(dmgURL: dmgURL)
                DispatchQueue.main.async { completion(.success(())) }
            } catch {
                DispatchQueue.main.async { completion(.failure(error)) }
            }
        }
    }

    private func performInstall(dmgURL: URL) throws {
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory
            .appendingPathComponent("PhotoCullInstall-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: root, withIntermediateDirectories: true)
        let mount = root.appendingPathComponent("mnt", isDirectory: true)
        try fileManager.createDirectory(at: mount, withIntermediateDirectories: true)

        var attached = false
        defer {
            if attached { _ = try? Self.run("/usr/bin/hdiutil", ["detach", mount.path, "-quiet"]) }
        }
        _ = try Self.run("/usr/bin/hdiutil", [
            "attach", "-nobrowse", "-readonly", "-quiet", "-mountpoint", mount.path, dmgURL.path
        ])
        attached = true

        let contents = try fileManager.contentsOfDirectory(at: mount, includingPropertiesForKeys: nil)
        guard let newApp = contents.first(where: { $0.pathExtension.lowercased() == "app" }) else {
            throw UpdateError.badAsset("DMG 里没有找到 .app")
        }
        let staged = root.appendingPathComponent("new.app", isDirectory: true)
        _ = try Self.run("/usr/bin/ditto", [newApp.path, staged.path])

        let newVersion = (Bundle(url: staged)?.infoDictionary?["CFBundleShortVersionString"] as? String) ?? ""
        guard !newVersion.isEmpty,
              Self.compare(newVersion, currentVersion) == .orderedDescending else {
            throw UpdateError.versionTooLow(newVersion)
        }

        let destination = Bundle.main.bundleURL
        guard destination.pathExtension.lowercased() == "app" else {
            throw UpdateError.installFailed("当前不是以 .app 形式运行")
        }
        let parent = destination.deletingLastPathComponent()
        guard fileManager.isWritableFile(atPath: parent.path) else {
            throw UpdateError.notWritable(parent.path)
        }

        let oldPath = destination.path + ".old"
        let scriptURL = root.appendingPathComponent("install.sh")
        let script = """
        #!/bin/sh
        sleep 1
        OLD=\(Self.shellQuote(oldPath))
        DEST=\(Self.shellQuote(destination.path))
        STAGED=\(Self.shellQuote(staged.path))
        rm -rf "$OLD"
        if /bin/mv "$DEST" "$OLD"; then
            if /usr/bin/ditto "$STAGED" "$DEST"; then
                /usr/bin/xattr -cr "$DEST" 2>/dev/null
                rm -rf "$OLD"
                /usr/bin/open "$DEST"
            else
                rm -rf "$DEST"
                /bin/mv "$OLD" "$DEST"
                /usr/bin/open "$DEST"
            fi
        else
            /usr/bin/open "$DEST"
        fi
        """
        try script.write(to: scriptURL, atomically: true, encoding: .utf8)
        try fileManager.setAttributes([.posixPermissions: 0o755], ofItemAtPath: scriptURL.path)

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = [scriptURL.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
    }

    // MARK: - 工具

    @discardableResult
    static func run(_ executable: String, _ arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        process.waitUntilExit()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        let output = String(data: data, encoding: .utf8) ?? ""
        guard process.terminationStatus == 0 else {
            throw UpdateError.installFailed("\(executable) 执行失败：\(output.trimmingCharacters(in: .whitespacesAndNewlines))")
        }
        return output
    }

    static func shellQuote(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    // MARK: - 命令行

    /// --update-check：无界面检查并打印结果
    static func runCLI() -> Int32 {
        let checker = UpdateChecker()
        var result: Result<UpdateRelease?, Error>?
        var done = false
        checker.checkForUpdate { value in
            result = value
            done = true
        }
        let deadline = Date().addingTimeInterval(30)
        while !done && Date() < deadline {
            RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.05))
        }
        guard let result else {
            print("更新检查超时")
            return 1
        }
        switch result {
        case .success(let release?):
            print("当前版本：\(checker.currentVersion)")
            print("发现新版本：\(release.version)")
            print("安装包：\(release.assetName)")
            print("下载：\(release.downloadURL.absoluteString)")
            return 0
        case .success(nil):
            print("当前已是最新版本：\(checker.currentVersion)")
            return 0
        case .failure(let error):
            print("更新检查失败：\(error.localizedDescription)")
            return 1
        }
    }
}

// MARK: - 下载器

private final class UpdateDownloader: NSObject, URLSessionDownloadDelegate {
    private var destination: URL?
    private var progress: ((Double) -> Void)?
    private var completion: ((Result<URL, Error>) -> Void)?
    private var session: URLSession?
    private var finished = false

    func start(url: URL,
               destination: URL,
               progress: @escaping (Double) -> Void,
               completion: @escaping (Result<URL, Error>) -> Void) {
        self.destination = destination
        self.progress = progress
        self.completion = completion
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 60
        configuration.timeoutIntervalForResource = 300
        session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        session?.downloadTask(with: url).resume()
    }

    func urlSession(_ session: URLSession,
                    downloadTask: URLSessionDownloadTask,
                    didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64,
                    totalBytesExpectedToWrite: Int64) {
        guard totalBytesExpectedToWrite > 0 else { return }
        let value = Double(totalBytesWritten) / Double(totalBytesExpectedToWrite)
        DispatchQueue.main.async { self.progress?(min(1, max(0, value))) }
    }

    func urlSession(_ session: URLSession,
                    downloadTask: URLSessionDownloadTask,
                    didFinishDownloadingTo location: URL) {
        guard let destination else {
            finish(.failure(UpdateError.downloadFailed("缺少目标路径")))
            return
        }
        do {
            let directory = destination.deletingLastPathComponent()
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: destination.path) {
                try FileManager.default.removeItem(at: destination)
            }
            try FileManager.default.moveItem(at: location, to: destination)
            finish(.success(destination))
        } catch {
            finish(.failure(error))
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error { finish(.failure(error)) }
    }

    private func finish(_ result: Result<URL, Error>) {
        guard !finished else { return }
        finished = true
        completion?(result)
        completion = nil
        progress = nil
        session?.invalidateAndCancel()
        session = nil
    }
}
