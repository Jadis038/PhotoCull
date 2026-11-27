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
    private let mirrorPrefix = "https://gh-proxy.com/"
    /// --update-auto：无人值守下载/安装（自动化测试用）
    var autoUpdateMode = false
    /// --update-mirror：强制走 gh-proxy 镜像（镜像回退测试用）
    var forceMirrorOnly = false

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
    /// macOS 的 Release 为正式版；Atom 源同时覆盖正式版和临时预发布，便于测试。
    func checkForUpdate(includePrerelease: Bool = true,
                        completion: @escaping (Result<UpdateRelease?, Error>) -> Void) {
        _ = includePrerelease
        let stamp = Int(Date().timeIntervalSince1970 * 1000)
        let primary = URL(string: "https://github.com/\(repository)/releases.atom?t=\(stamp)")!
        let mirror = URL(string: "\(mirrorPrefix)https://github.com/\(repository)/releases.atom")!
        fetchAtom(urls: [primary, mirror], completion: completion)
    }

    private func fetchAtom(urls: [URL],
                           completion: @escaping (Result<UpdateRelease?, Error>) -> Void) {
        guard let url = urls.first else {
            completion(.failure(UpdateError.invalidResponse))
            return
        }
        var request = URLRequest(url: url)
        // Atom 源没有 API 的 60 次/小时匿名额度限制；query 用于绕过 CDN 缓存。
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.setValue("application/atom+xml", forHTTPHeaderField: "Accept")
        request.setValue("PhotoCull-macOS/\(currentVersion)", forHTTPHeaderField: "User-Agent")

        session.dataTask(with: request) { [weak self] data, response, error in
            guard let self else { return }
            let retry = { self.fetchAtom(urls: Array(urls.dropFirst()), completion: completion) }
            if error != nil { retry(); return }
            guard let http = response as? HTTPURLResponse,
                  (200..<300).contains(http.statusCode),
                  let data, !data.isEmpty else {
                retry()
                return
            }
            let parser = AtomReleaseParser()
            guard parser.parse(data) else {
                retry()
                return
            }
            let release = self.release(from: parser.entries)
            DispatchQueue.main.async { completion(.success(release)) }
        }.resume()
    }

    private func release(from entries: [AtomReleaseParser.Entry]) -> UpdateRelease? {
        var best: UpdateRelease?
        for entry in entries {
            guard entry.tag.hasPrefix("macos-") else { continue }
            guard let version = version(fromTag: entry.tag),
                  Self.compare(version, currentVersion) == .orderedDescending else { continue }
            let assetName = "PhotoCull-\(version)-macOS-arm64.dmg"
            guard let downloadURL = URL(string: "https://github.com/\(repository)/releases/download/\(entry.tag)/\(assetName)") else { continue }
            var published: Date?
            if !entry.updated.isEmpty { published = ISO8601DateFormatter().date(from: entry.updated) }
            let release = UpdateRelease(
                version: version,
                tag: entry.tag,
                title: entry.title.isEmpty ? entry.tag : entry.title,
                notes: AtomReleaseParser.plainText(entry.content),
                assetName: assetName,
                downloadURL: downloadURL,
                publishedAt: published,
                prerelease: false
            )
            if best == nil || Self.compare(release.version, best!.version) == .orderedDescending {
                best = release
            }
        }
        return best
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
        let mirrorURL = URL(string: "\(mirrorPrefix)\(release.downloadURL.absoluteString)")
        var urls: [URL] = []
        if !forceMirrorOnly { urls.append(release.downloadURL) }
        if let mirrorURL { urls.append(mirrorURL) }
        let downloader = UpdateDownloader()
        activeDownloader = downloader
        downloader.start(urls: urls, destination: destination, progress: progress) { [weak self] result in
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
    private var urls: [URL] = []
    private var currentIndex = 0
    private var currentTask: URLSessionDownloadTask?
    private var finished = false

    func start(urls: [URL],
               destination: URL,
               progress: @escaping (Double) -> Void,
               completion: @escaping (Result<URL, Error>) -> Void) {
        self.urls = urls
        self.destination = destination
        self.progress = progress
        self.completion = completion
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 60
        configuration.timeoutIntervalForResource = 300
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        startCurrent()
    }

    private func startCurrent() {
        guard !finished else { return }
        guard currentIndex < urls.count else {
            finish(.failure(UpdateError.downloadFailed("直连与镜像均下载失败")))
            return
        }
        let url = urls[currentIndex]
        let task = session?.downloadTask(with: url)
        currentTask = task
        task?.resume()
    }

    private func advanceOrFinish(_ error: Error) {
        currentIndex += 1
        if currentIndex < urls.count {
            DispatchQueue.main.async { self.progress?(0) }
            startCurrent()
        } else {
            finish(.failure(error))
        }
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
        if let http = downloadTask.response as? HTTPURLResponse,
           !(200..<300).contains(http.statusCode) {
            advanceOrFinish(UpdateError.downloadFailed("HTTP \(http.statusCode)"))
            return
        }
        guard let destination else {
            finish(.failure(UpdateError.downloadFailed("缺少目标路径")))
            return
        }
        do {
            let attributes = try FileManager.default.attributesOfItem(atPath: location.path)
            let size = (attributes[.size] as? NSNumber)?.int64Value ?? 0
            guard size > 50_000 else {
                advanceOrFinish(UpdateError.downloadFailed("下载内容过小（\(size) 字节）"))
                return
            }
            try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: destination.path) {
                try FileManager.default.removeItem(at: destination)
            }
            try FileManager.default.moveItem(at: location, to: destination)
            finish(.success(destination))
        } catch {
            advanceOrFinish(error)
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard !finished else { return }
        if let error {
            advanceOrFinish(error)
        }
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

// MARK: - Releases Atom 解析

private final class AtomReleaseParser: NSObject, XMLParserDelegate {
    struct Entry {
        var tag = ""
        var title = ""
        var content = ""
        var updated = ""
    }

    private(set) var entries: [Entry] = []
    private var current: Entry?
    private var currentElement = ""
    private var buffer = ""

    func parse(_ data: Data) -> Bool {
        entries.removeAll()
        current = nil
        let parser = XMLParser(data: data)
        parser.delegate = self
        return parser.parse()
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String,
                namespaceURI: String?, qualifiedName qName: String?,
                attributes attributeDict: [String: String] = [:]) {
        if elementName == "entry" {
            current = Entry()
        }
        guard current != nil else { return }
        currentElement = elementName
        buffer = ""
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        guard current != nil else { return }
        buffer += string
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String,
                namespaceURI: String?, qualifiedName qName: String?) {
        guard var entry = current else { return }
        switch elementName {
        case "id":
            entry.tag = Self.extractTag(buffer)
        case "title":
            entry.title = buffer.trimmingCharacters(in: .whitespacesAndNewlines)
        case "content":
            entry.content = buffer
        case "updated":
            entry.updated = buffer.trimmingCharacters(in: .whitespacesAndNewlines)
        case "entry":
            entries.append(entry)
            current = nil
            return
        default:
            break
        }
        current = entry
    }

    private static func extractTag(_ id: String) -> String {
        let marker = "Repository/"
        guard let range = id.range(of: marker) else { return "" }
        let rest = id[range.upperBound...]
        guard let slash = rest.firstIndex(of: "/") else { return "" }
        return String(rest[rest.index(after: slash)...])
    }

    static func plainText(_ html: String) -> String {
        var text = html
        for tag in ["</p>", "</li>", "</h1>", "</h2>", "</h3>", "<br>", "<br/>", "<br />"] {
            text = text.replacingOccurrences(of: tag, with: "\n")
        }
        text = text.replacingOccurrences(of: "<[^>]+>", with: "", options: .regularExpression)
        let entities = [
            "&amp;": "&", "&lt;": "<", "&gt;": ">", "&quot;": "\"", "&#39;": "'",
            "&nbsp;": " ", "&#x27;": "'"
        ]
        for (key, value) in entities { text = text.replacingOccurrences(of: key, with: value) }
        while text.contains("\n\n\n") { text = text.replacingOccurrences(of: "\n\n\n", with: "\n\n") }
        return text.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
