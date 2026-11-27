// 影·筛 / PhotoCull — 界面级自动化验收（--uitest <文件夹>）
// 在后台线程驱动、主线程执行 UI 操作，避免阻塞 AppKit 事件循环。
import AppKit
import ImageIO

enum UITest {
    private static var failures = 0
    private static var passes = 0

    static func run(controller: MainWindowController, completion: @escaping (Int32) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            let session = controller.session
            print("UI 自动化验收开始")

            func onMain<T>(_ body: @escaping () -> T) -> T {
                if Thread.isMainThread { return body() }
                return DispatchQueue.main.sync(execute: body)
            }

            func check(_ name: String, _ condition: Bool, _ detail: String = "") {
                if condition {
                    passes += 1
                    print("PASS | \(name)\(detail.isEmpty ? "" : " | \(detail)")")
                } else {
                    failures += 1
                    print("FAIL | \(name)\(detail.isEmpty ? "" : " | \(detail)")")
                }
            }

            func waitUntil(_ timeout: TimeInterval, _ condition: @escaping () -> Bool) -> Bool {
                let deadline = Date().addingTimeInterval(timeout)
                while true {
                    if onMain(condition) { return true }
                    if Date() > deadline { return false }
                    Thread.sleep(forTimeInterval: 0.03)
                }
            }

            func waitMain<T>(_ timeout: TimeInterval, _ condition: @escaping () -> T?) -> T? {
                let deadline = Date().addingTimeInterval(timeout)
                while true {
                    if let value = onMain(condition) { return value }
                    if Date() > deadline { return nil }
                    Thread.sleep(forTimeInterval: 0.03)
                }
            }

            _ = waitUntil(20) { !session.items.isEmpty }
            let itemCount = onMain { session.items.count }
            let initialSourceCount = onMain {
                session.items.filter { FileManager.default.fileExists(atPath: $0.currentURL.path) }.count
            }
            check("打开文件夹并加载列表", itemCount > 0, "共 \(itemCount) 张")

            // 逐格式打开真实文件
            let formatOrder: [(ext: String, label: String)] = [
                ("jpg", "JPG"), ("nef", "尼康 NEF"), ("cr2", "佳能 CR2"), ("cr3", "佳能 CR3"),
                ("arw", "索尼 ARW"), ("raf", "富士 RAF"), ("orf", "奥林巴斯 ORF"),
                ("rw2", "松下 RW2"), ("dng", "DNG"), ("png", "PNG"), ("tif", "TIFF"),
                ("avif", "AVIF"), ("jxl", "JPEG XL"), ("exr", "OpenEXR"), ("heic", "HEIC")
            ]

            for entry in formatOrder {
                let found: (index: Int, path: String, name: String)? = onMain {
                    guard let index = session.items.firstIndex(where: { $0.ext == entry.ext }) else { return nil }
                    return (index, session.items[index].url.path, session.items[index].name)
                }
                guard let found else {
                    print("SKIP | \(entry.label)（样本中没有）")
                    continue
                }
                onMain { session.select(index: found.index) }
                let loaded = waitUntil(15) {
                    controller.canvasImageForTesting != nil && session.currentItem?.url.path == found.path
                }
                check("打开 \(entry.label)", loaded, found.name)
                if loaded {
                    let metadataReady = waitUntil(6) {
                        session.items.first(where: { $0.url.path == found.path })?.metadata != nil
                    }
                    if MediaKind.from(extension: entry.ext).isRaw {
                        let model = onMain {
                            session.items.first(where: { $0.url.path == found.path })?.metadata?.cameraModel
                        }
                        check("\(entry.label) 元数据", metadataReady && model != nil, model ?? "nil")
                    }
                }
            }

            guard let jpgIndex = onMain({ session.items.firstIndex(where: { $0.ext == "jpg" }) }) else {
                check("标记测试需要 JPG", false)
                DispatchQueue.main.async { completion(1) }
                return
            }
            onMain { session.select(index: jpgIndex) }
            let jpgPath = onMain { session.items[jpgIndex].url.path }
            let filterFolder = onMain { session.folderURL!.appendingPathComponent("筛选") }
            let copyName = onMain { session.items[jpgIndex].name }

            onMain { session.toggleMark(session.items[jpgIndex]) }
            let marked = waitUntil(20) {
                session.items.first(where: { $0.url.path == jpgPath })?.markState == .marked && session.markEngine?.queueCount == 0
            }
            check("界面标记完成", marked)
            check("标记后副本存在", FileManager.default.fileExists(atPath: filterFolder.appendingPathComponent(copyName).path))
            check("计数更新", onMain { session.markedCount } >= 1)

            onMain { if let item = session.items.first(where: { $0.url.path == jpgPath }) { session.toggleMark(item) } }
            let unmarked = waitUntil(20) {
                session.items.first(where: { $0.url.path == jpgPath })?.markState == MarkState.none
            }
            check("界面取消完成", unmarked)
            check("取消后副本消失", !FileManager.default.fileExists(atPath: filterFolder.appendingPathComponent(copyName).path))
            check("取消后原文件仍在", FileManager.default.fileExists(atPath: jpgPath))

            onMain { if let item = session.items.first(where: { $0.url.path == jpgPath }) { session.toggleMark(item) } }
            _ = waitUntil(20) { session.items.first(where: { $0.url.path == jpgPath })?.markState == .marked }
            onMain { session.undo() }
            let undone = waitUntil(20) { session.items.first(where: { $0.url.path == jpgPath })?.markState == MarkState.none }
            check("界面撤销标记", undone && !FileManager.default.fileExists(atPath: filterFolder.appendingPathComponent(copyName).path))

            // 移动模式
            onMain { AppSettings.shared.operationMode = .move }
            onMain { if let item = session.items.first(where: { $0.url.path == jpgPath }) { session.toggleMark(item) } }
            _ = waitUntil(20) { session.items.first(where: { $0.url.path == jpgPath })?.markState == .marked }
            let moved = onMain { session.items.first(where: { $0.url.path == jpgPath })?.record?.mode == .move }
            check("移动模式：原文件移走", moved && !FileManager.default.fileExists(atPath: jpgPath))
            check("移动模式：副本出现", FileManager.default.fileExists(atPath: filterFolder.appendingPathComponent(copyName).path))

            onMain { if let item = session.items.first(where: { $0.url.path == jpgPath }) { session.toggleMark(item) } }
            _ = waitUntil(20) { session.items.first(where: { $0.url.path == jpgPath })?.markState == MarkState.none }
            check("移动模式取消：原文件移回", FileManager.default.fileExists(atPath: jpgPath))
            onMain { AppSettings.shared.operationMode = .copy }

            // 排序
            onMain { session.setSort(key: .size, order: .descending) }
            _ = waitUntil(3) { onMain { session.items.count } > 0 }
            let sorted = onMain { zip(session.items, session.items.dropFirst()).allSatisfy { $0.size >= $1.size } }
            check("按大小降序排序", sorted)
            onMain { session.setSort(key: .name, order: .ascending) }

            // 设置窗口
            onMain { controller.settingsPressed() }
            let settingsOpened = waitMain(5) { NSApp.windows.first(where: { $0.title == "设置" && $0.isVisible }) }
            check("设置窗口可打开", settingsOpened != nil)
            onMain { settingsOpened?.close() }

            // 收尾：确认源文件完整、筛选目录为空
            _ = waitUntil(10) { onMain { session.markEngine?.queueCount ?? 0 } == 0 }
            let finalSourceCount = onMain {
                session.items.filter { FileManager.default.fileExists(atPath: $0.currentURL.path) }.count
            }
            check("测试后源文件完整", finalSourceCount == initialSourceCount,
                  "初始 \(initialSourceCount) / 结束 \(finalSourceCount)")
            let leftoverCopies = (try? FileManager.default.contentsOfDirectory(atPath: filterFolder.path)) ?? []
            check("测试后筛选目录无残留", leftoverCopies.isEmpty, "\(leftoverCopies)")

            print("\nUI 验收结束：\(passes) 通过 / \(failures) 失败")
            let code: Int32 = failures == 0 ? 0 : 1
            DispatchQueue.main.async { completion(code) }
        }
    }
}
