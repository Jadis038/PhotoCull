// 影·筛 / PhotoCull — 无界面自检（扫描、排序、复制、冲突、伴生文件、取消、移动、撤销、索引）
import Foundation
import ImageIO
import CoreGraphics
import AppKit
import Darwin

enum SelfTest {
    static func run() -> Int32 {
        let tag = "YS\(UUID().uuidString.prefix(6))"
        let fileManager = FileManager.default
        let tempBase = fileManager.temporaryDirectory.resolvingSymlinksInPath()
        let root = tempBase.appendingPathComponent("\(tag)-selftest", isDirectory: true)
        let dataRoot = tempBase.appendingPathComponent("\(tag)-data", isDirectory: true)
        try? fileManager.createDirectory(at: root, withIntermediateDirectories: true)
        try? fileManager.createDirectory(at: dataRoot, withIntermediateDirectories: true)
        setenv("YING_SHAI_DATA", dataRoot.path, 1)
        AppInfo.ensureDirectories()

        var failures = 0
        var passes = 0

        func check(_ name: String, _ condition: Bool, _ detail: String = "") {
            if condition {
                passes += 1
                print("PASS | \(name)\(detail.isEmpty ? "" : " | \(detail)")")
            } else {
                failures += 1
                print("FAIL | \(name)\(detail.isEmpty ? "" : " | \(detail)")")
            }
        }

        defer {
            cleanupTrash(containing: tag)
            try? fileManager.removeItem(at: root)
            try? fileManager.removeItem(at: dataRoot)
            print("\n自检结束：\(passes) 通过 / \(failures) 失败")
        }

        // 1. 自然排序
        check("自然排序 IMG_2 < IMG_10", NaturalSort.less("IMG_2.JPG", "IMG_10.JPG"))
        check("自然排序 中文数字", NaturalSort.less("照片 2.jpg", "照片 10.jpg"))
        check("自然排序 两个数字位", NaturalSort.less("IMG_9.JPG", "IMG_10.JPG"))

        // 2. 扫描白名单
        do {
            let folder = root.appendingPathComponent("扫描测试", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            try writeJPEG(folder.appendingPathComponent("IMG_0001-\(tag).JPG"))
            try writeJPEG(folder.appendingPathComponent("IMG_0010-\(tag).jpg"))
            try writeFake(folder.appendingPathComponent("IMG_0002-\(tag).NEF"), bytes: 128)
            try "not an image".data(using: .utf8)!.write(to: folder.appendingPathComponent("notes.txt"))
            try writeJPEG(folder.appendingPathComponent(".hidden-\(tag).JPG"))
            try writeJPEG(folder.appendingPathComponent("~$temp-\(tag).JPG"))
            try writeJPEG(folder.appendingPathComponent("._apple-\(tag).JPG"))
            let sub = folder.appendingPathComponent("子目录", isDirectory: true)
            try fileManager.createDirectory(at: sub, withIntermediateDirectories: true)
            try writeJPEG(sub.appendingPathComponent("child-\(tag).JPG"))
            let filter = folder.appendingPathComponent("筛选", isDirectory: true)
            try fileManager.createDirectory(at: filter, withIntermediateDirectories: true)
            try writeJPEG(filter.appendingPathComponent("copy-\(tag).JPG"))

            let scan = try Scanner.scan(folder: folder)
            let names = scan.items.map { $0.name }
            check("扫描只列第一层图片", scan.items.count == 3, "实际 \(scan.items.count)：\(names)")
            check("扫描排除隐藏/临时/子目录/筛选", !names.contains { $0.hasPrefix(".") || $0.hasPrefix("~$") || $0.hasPrefix("._") || $0 == "notes.txt" })
            check("扫描自然排序", names == ["IMG_0001-\(tag).JPG", "IMG_0002-\(tag).NEF", "IMG_0010-\(tag).jpg"], names.joined(separator: ", "))
        } catch {
            check("扫描测试执行", false, error.localizedDescription)
        }

        // 3. 复制标记 + 4. 取消（回收站）+ 撤销
        do {
            let folder = root.appendingPathComponent("复制 标记", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            let source = folder.appendingPathComponent("IMG_0100-\(tag).JPG")
            try writeJPEG(source)
            let items = try Scanner.scan(folder: folder).items
            guard let item = items.first else { throw AppError("扫描失败") }
            let harness = try EngineHarness(folder: folder)

            harness.engine.enqueueMark(item: item, mode: .copy)
            _ = waitUntil(20) { harness.states[item.url.path] == .marked }
            let copy = folder.appendingPathComponent("筛选/IMG_0100-\(tag).JPG")
            check("复制标记成功", harness.states[item.url.path] == .marked, harness.errors.joined(separator: ";"))
            check("筛选目录自动创建", FileTools.isDirectory(folder.appendingPathComponent("筛选")))
            check("副本存在且大小一致", fileManager.fileExists(atPath: copy.path) && FileTools.fileSize(copy) == item.size)
            check("无 .part 残留", !hasPartFiles(in: folder.appendingPathComponent("筛选")))
            let record = harness.store.loadAll()[item.url.path]
            check("索引记录写入", record != nil && record?.copyPath == copy.path)

            guard let record else { throw AppError("记录缺失") }
            harness.engine.enqueueUnmark(item: item, record: record)
            _ = waitUntil(20) { harness.states[item.url.path] == MarkState.none }
            check("取消标记：副本离开筛选", !fileManager.fileExists(atPath: copy.path))
            check("取消标记：原文件不动", fileManager.fileExists(atPath: item.currentURL.path))
            check("取消标记：索引删除", harness.store.loadAll()[item.url.path] == nil)
            if case .undoUnmarkCopy(let savedRecord, _)? = harness.undoActions.last {
                harness.engine.undo(.undoUnmarkCopy(record: savedRecord, trashURLs: trashMap(from: harness.undoActions.last)))
                _ = waitUntil(20) { harness.states[item.url.path] == .marked }
                check("撤销取消：副本从回收站恢复", fileManager.fileExists(atPath: copy.path))
            } else {
                check("撤销取消：可撤销记录存在", false)
            }
        } catch {
            check("复制/取消测试执行", false, error.localizedDescription)
        }

        // 5. 同名冲突不覆盖
        do {
            let folder = root.appendingPathComponent("冲突测试", isDirectory: true)
            let filter = folder.appendingPathComponent("筛选", isDirectory: true)
            try fileManager.createDirectory(at: filter, withIntermediateDirectories: true)
            try Data("old-copy".utf8).write(to: filter.appendingPathComponent("IMG_0200-\(tag).JPG"))
            let source = folder.appendingPathComponent("IMG_0200-\(tag).JPG")
            try writeJPEG(source)
            let items = try Scanner.scan(folder: folder).items
            guard let item = items.first else { throw AppError("扫描失败") }
            let harness = try EngineHarness(folder: folder)
            harness.engine.enqueueMark(item: item, mode: .copy)
            _ = waitUntil(20) { harness.states[item.url.path] == .marked }
            let renamed = filter.appendingPathComponent("IMG_0200-\(tag) (1).JPG")
            let oldData = try Data(contentsOf: filter.appendingPathComponent("IMG_0200-\(tag).JPG"))
            check("同名冲突自动改名 (1)", fileManager.fileExists(atPath: renamed.path))
            check("原同名副本未被覆盖", String(data: oldData, encoding: .utf8) == "old-copy")
        } catch {
            check("冲突测试执行", false, error.localizedDescription)
        }

        // 6. 伴生文件（RAW + JPG + XMP）
        do {
            let folder = root.appendingPathComponent("伴生文件", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            let base = "IMG_0300-\(tag)"
            try writeFake(folder.appendingPathComponent("\(base).NEF"), bytes: 256)
            try writeJPEG(folder.appendingPathComponent("\(base).JPG"))
            try Data("<xmp/>".utf8).write(to: folder.appendingPathComponent("\(base).xmp"))
            let items = try Scanner.scan(folder: folder).items
            guard let item = items.first(where: { $0.ext == "nef" }) else { throw AppError("找不到 NEF") }
            let harness = try EngineHarness(folder: folder)
            harness.engine.enqueueMark(item: item, mode: .copy)
            _ = waitUntil(20) { harness.states[item.url.path] == .marked }
            let filter = folder.appendingPathComponent("筛选")
            check("伴生文件：RAW 主文件", fileManager.fileExists(atPath: filter.appendingPathComponent("\(base).NEF").path))
            check("伴生文件：同名 JPG", fileManager.fileExists(atPath: filter.appendingPathComponent("\(base).JPG").path))
            check("伴生文件：XMP", fileManager.fileExists(atPath: filter.appendingPathComponent("\(base).xmp").path))
        } catch {
            check("伴生文件测试执行", false, error.localizedDescription)
        }

        // 7. 移动模式标记/取消
        do {
            let folder = root.appendingPathComponent("移动模式", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            let source = folder.appendingPathComponent("IMG_0400-\(tag).JPG")
            try writeJPEG(source)
            let items = try Scanner.scan(folder: folder).items
            guard let item = items.first else { throw AppError("扫描失败") }
            let harness = try EngineHarness(folder: folder)
            harness.engine.enqueueMark(item: item, mode: .move)
            _ = waitUntil(20) { harness.states[item.url.path] == .marked }
            let copy = folder.appendingPathComponent("筛选/IMG_0400-\(tag).JPG")
            check("移动模式：源文件移走", !fileManager.fileExists(atPath: source.path))
            check("移动模式：副本出现", fileManager.fileExists(atPath: copy.path))
            check("移动模式：记录 mode=move", harness.store.loadAll()[item.url.path]?.mode == .move)

            if let record = harness.store.loadAll()[item.url.path] {
                harness.engine.enqueueUnmark(item: item, record: record)
                _ = waitUntil(20) { harness.states[item.url.path] == MarkState.none }
                check("移动模式取消：文件移回原位", fileManager.fileExists(atPath: source.path))
                check("移动模式取消：筛选副本消失", !fileManager.fileExists(atPath: copy.path))
            }
        } catch {
            check("移动模式测试执行", false, error.localizedDescription)
        }

        // 8. 撤销标记（复制）
        do {
            let folder = root.appendingPathComponent("撤销标记", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            let source = folder.appendingPathComponent("IMG_0500-\(tag).JPG")
            try writeJPEG(source)
            let items = try Scanner.scan(folder: folder).items
            guard let item = items.first else { throw AppError("扫描失败") }
            let harness = try EngineHarness(folder: folder)
            harness.engine.enqueueMark(item: item, mode: .copy)
            _ = waitUntil(20) { harness.states[item.url.path] == .marked }
            guard let action = harness.undoActions.last else { throw AppError("没有撤销记录") }
            harness.engine.undo(action)
            _ = waitUntil(20) { harness.states[item.url.path] == MarkState.none }
            let copy = folder.appendingPathComponent("筛选/IMG_0500-\(tag).JPG")
            check("撤销标记：副本被移除", !fileManager.fileExists(atPath: copy.path))
            check("撤销标记：原文件仍在", fileManager.fileExists(atPath: source.path))
            check("撤销标记：索引清除", harness.store.loadAll()[item.url.path] == nil)
        } catch {
            check("撤销测试执行", false, error.localizedDescription)
        }

        // 9. 索引重建（按文件名匹配筛选目录）
        do {
            let folder = root.appendingPathComponent("索引重建", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            let source = folder.appendingPathComponent("IMG_0600-\(tag).JPG")
            try writeJPEG(source)
            let items = try Scanner.scan(folder: folder).items
            guard let item = items.first else { throw AppError("扫描失败") }
            let harness = try EngineHarness(folder: folder)
            harness.engine.enqueueMark(item: item, mode: .copy)
            _ = waitUntil(20) { harness.states[item.url.path] == .marked }
            let dbPath = harness.store.databasePath
            try? fileManager.removeItem(at: dbPath)
            try? fileManager.removeItem(at: URL(fileURLWithPath: dbPath.path + "-wal"))
            try? fileManager.removeItem(at: URL(fileURLWithPath: dbPath.path + "-shm"))
            let fresh = try StateStore(folder: folder)
            let empty = fresh.loadAll().isEmpty
            let rebuilt = fresh.rebuildFromFilterFolder(items: [item])
            check("索引丢失后可按文件名重建", empty && rebuilt.count == 1, "重建 \(rebuilt.count) 条")
            let expectedCopyPath = folder.appendingPathComponent("筛选/IMG_0600-\(tag).JPG").resolvingSymlinksInPath().path
            let actualCopyPath = rebuilt[item.url.path].map { URL(fileURLWithPath: $0.copyPath).resolvingSymlinksInPath().path }
            check("重建记录指向筛选副本", actualCopyPath == expectedCopyPath)
        } catch {
            check("索引重建测试执行", false, error.localizedDescription)
        }

        // 10. 中文、空格、长文件名路径
        do {
            let folder = root.appendingPathComponent("中文 空格 目录 \(tag)", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            let longName = "长名字-" + String(repeating: "长", count: 55) + "-\(tag).JPG"
            let source = folder.appendingPathComponent(longName)
            try writeJPEG(source)
            let items = try Scanner.scan(folder: folder).items
            guard let item = items.first else { throw AppError("扫描失败") }
            let harness = try EngineHarness(folder: folder)
            harness.engine.enqueueMark(item: item, mode: .copy)
            _ = waitUntil(20) { harness.states[item.url.path] == .marked }
            check("中文/空格/长文件名复制成功",
                  fileManager.fileExists(atPath: folder.appendingPathComponent("筛选/\(longName)").path))
        } catch {
            check("长路径测试执行", false, error.localizedDescription)
        }

        // 11. 快速连点 10 张：队列顺序执行、互不影响
        do {
            let folder = root.appendingPathComponent("连点队列", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            for index in 0..<10 {
                try writeJPEG(folder.appendingPathComponent(String(format: "IMG_%04d-%@.JPG", 700 + index, tag)))
            }
            let items = try Scanner.scan(folder: folder).items
            let harness = try EngineHarness(folder: folder)
            for item in items {
                harness.engine.enqueueMark(item: item, mode: .copy)
            }
            let deadline = Date().addingTimeInterval(30)
            while Date() < deadline {
                if harness.states.values.filter({ $0 == .marked }).count == 10 && harness.queueCount == 0 { break }
                RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.02))
            }
            let marked = harness.states.values.filter { $0 == .marked }.count
            let copies = FileTools.listFiles(in: folder.appendingPathComponent("筛选")).filter {
                !$0.lastPathComponent.hasSuffix(".part")
            }
            check("连点 10 张全部完成", marked == 10, "marked=\(marked)")
            check("连点 10 张副本数量正确", copies.count == 10, "copies=\(copies.count)")
            check("连点后无 .part 残留", !hasPartFiles(in: folder.appendingPathComponent("筛选")))
        } catch {
            check("队列测试执行", false, error.localizedDescription)
        }

        // 12. 取消队列清理 .part
        do {
            let folder = root.appendingPathComponent("取消队列", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            for index in 0..<20 {
                try writeJPEG(folder.appendingPathComponent(String(format: "IMG_%04d-%@.JPG", 800 + index, tag)))
            }
            let items = try Scanner.scan(folder: folder).items
            let harness = try EngineHarness(folder: folder)
            for item in items { harness.engine.enqueueMark(item: item, mode: .copy) }
            harness.engine.cancelAll()
            _ = waitUntil(20) { harness.queueCount == 0 && !harness.states.values.contains(MarkState.processing) }
            check("取消队列后无 .part", !hasPartFiles(in: folder.appendingPathComponent("筛选")))
        } catch {
            check("取消队列测试执行", false, error.localizedDescription)
        }

        // 13. 保护锁定 / 星级持久化
        do {
            let folder = root.appendingPathComponent("锁定星级", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            let source = folder.appendingPathComponent("IMG_0900-\(tag).JPG")
            try writeJPEG(source)
            let items = try Scanner.scan(folder: folder).items
            guard let item = items.first else { throw AppError("扫描失败") }
            let store = try StateStore(folder: folder)
            try store.setLock(path: item.url.path, locked: true)
            check("保护状态写入/读取", store.loadLocks().contains(item.url.path))
            try store.setStars(path: item.url.path, stars: 4)
            check("星级写入/读取", store.loadStars()[item.url.path] == 4)
            try store.setLock(path: item.url.path, locked: false)
            try store.setStars(path: item.url.path, stars: 0)
            check("保护/星级可清除", !store.loadLocks().contains(item.url.path) && store.loadStars()[item.url.path] == nil)
        } catch {
            check("锁定星级持久化测试执行", false, error.localizedDescription)
        }

        return failures == 0 ? 0 : 1
    }

    // MARK: - 工具

    private static func waitUntil(_ timeout: TimeInterval, _ condition: () -> Bool) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline { return false }
            RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.02))
        }
        return true
    }

    private static func hasPartFiles(in folder: URL) -> Bool {
        guard FileTools.isDirectory(folder) else { return false }
        return FileTools.listFiles(in: folder).contains { $0.lastPathComponent.hasSuffix(".part") }
    }

    private static func writeJPEG(_ url: URL) throws {
        let width = 64, height = 48
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                      bytesPerRow: 0, space: colorSpace,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw AppError("无法创建测试位图")
        }
        context.setFillColor(NSColor.systemTeal.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.setFillColor(NSColor.systemOrange.cgColor)
        context.fill(CGRect(x: 8, y: 8, width: 20, height: 20))
        guard let image = context.makeImage(),
              let destination = CGImageDestinationCreateWithURL(url as CFURL, "public.jpeg" as CFString, 1, nil) else {
            throw AppError("无法写测试 JPEG")
        }
        CGImageDestinationAddImage(destination, image, [kCGImageDestinationLossyCompressionQuality: 0.8] as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { throw AppError("测试 JPEG 写入失败") }
    }

    private static func writeFake(_ url: URL, bytes: Int) throws {
        try Data(repeating: 0x41, count: bytes).write(to: url)
    }

    private static func trashMap(from action: UndoAction?) -> [String: String] {
        if case .undoUnmarkCopy(_, let map)? = action { return map }
        return [:]
    }

    private static func cleanupTrash(containing tag: String) {
        guard let trash = FileManager.default.urls(for: .trashDirectory, in: .userDomainMask).first else { return }
        guard let files = try? FileManager.default.contentsOfDirectory(at: trash, includingPropertiesForKeys: nil) else { return }
        for file in files where file.lastPathComponent.contains(tag) {
            try? FileManager.default.removeItem(at: file)
        }
    }
}

/// 自检用的标记引擎驱动
private final class EngineHarness {
    let engine: MarkEngine
    let store: StateStore
    var states: [String: MarkState] = [:]
    var errors: [String] = []
    var records: [String: MarkRecord?] = [:]
    var undoActions: [UndoAction] = []
    var queueCount = 0

    init(folder: URL) throws {
        store = try StateStore(folder: folder)
        engine = MarkEngine(folder: folder, stateStore: store)
        engine.onStateChange = { [weak self] path, state in self?.states[path] = state }
        engine.onRecordChanged = { [weak self] path, record in self?.records[path] = record }
        engine.onError = { [weak self] _, message in self?.errors.append(message) }
        engine.onQueueCountChanged = { [weak self] count in self?.queueCount = count }
        engine.onUndoAvailable = { [weak self] action in self?.undoActions.append(action) }
    }
}
