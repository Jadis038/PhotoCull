// 影·筛 / PhotoCull — 应用入口、菜单与单实例窗口
import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    private(set) var mainController: MainWindowController?
    private var pendingOpenURLs: [URL] = []
    var uiTestFolder: URL?
    var uiTestExitCode: Int32 = 0
    private var updateWindowController: UpdateWindowController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        InputSourceHelper.selectEnglishInputSource()
        NSApp.mainMenu = makeMainMenu()

        let controller = MainWindowController()
        mainController = controller
        defer { scheduleUpdateCheckIfNeeded() }
        controller.showWindow(nil)
        controller.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        startUITestIfNeeded(controller: controller)
        if uiTestFolder != nil { return }

        if let first = pendingOpenURLs.first {
            controller.openPath(first)
            pendingOpenURLs.removeAll()
            return
        }

        let fileArguments = CommandLine.arguments.dropFirst().filter { !$0.hasPrefix("-") }
        if let first = fileArguments.first {
            controller.openPath(URL(fileURLWithPath: first))
            return
        }

        let settings = AppSettings.shared
        if settings.restoreLastSession,
           !settings.lastFolderPath.isEmpty,
           FileTools.isDirectory(URL(fileURLWithPath: settings.lastFolderPath)) {
            controller.openPath(URL(fileURLWithPath: settings.lastFolderPath))
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    // MARK: - 更新检测

    private func scheduleUpdateCheckIfNeeded() {
        guard uiTestFolder == nil else { return }
        guard UpdateChecker.shared.autoUpdateMode || AppSettings.shared.checkForUpdates else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.0) { [weak self] in
            self?.runUpdateCheck()
        }
    }

    private func runUpdateCheck() {
        UpdateChecker.shared.checkForUpdate { [weak self] result in
            guard let self else { return }
            switch result {
            case .success(let release?):
                if UpdateChecker.shared.autoUpdateMode {
                    self.performAutoUpdate(release)
                } else {
                    self.showUpdate(release)
                }
            case .success(nil):
                if UpdateChecker.shared.autoUpdateMode {
                    print("当前已是最新版本：\(AppInfo.version)")
                    NSApp.terminate(nil)
                }
            case .failure(let error):
                Log.info("更新检查失败：\(error.localizedDescription)")
                if UpdateChecker.shared.autoUpdateMode {
                    print("更新检查失败：\(error.localizedDescription)")
                    NSApp.terminate(nil)
                }
            }
        }
    }

    private func showUpdate(_ release: UpdateRelease) {
        if updateWindowController == nil {
            updateWindowController = UpdateWindowController()
        }
        updateWindowController?.present(release: release)
    }

    private func performAutoUpdate(_ release: UpdateRelease) {
        print("发现新版本：\(release.version)（当前 \(AppInfo.version)）")
        print("正在下载 \(release.assetName)…")
        UpdateChecker.shared.download(release, progress: { value in
            print(String(format: "下载进度：%.0f%%", value * 100))
        }, completion: { result in
            switch result {
            case .success(let dmgURL):
                print("下载完成，正在安装…")
                UpdateChecker.shared.installDownloadedUpdate(dmgURL: dmgURL) { installResult in
                    switch installResult {
                    case .success:
                        print("安装程序已启动，应用即将退出…")
                        NSApp.terminate(nil)
                    case .failure(let error):
                        print("安装失败：\(error.localizedDescription)")
                        NSApp.terminate(nil)
                    }
                }
            case .failure(let error):
                print("下载失败：\(error.localizedDescription)")
                NSApp.terminate(nil)
            }
        })
    }

    private func startUITestIfNeeded(controller: MainWindowController) {
        guard let folder = uiTestFolder else { return }
        controller.openPath(folder)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) {
            UITest.run(controller: controller) { code in
                self.uiTestExitCode = code
                NSApp.terminate(nil)
            }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag {
            mainController?.showWindow(nil)
            mainController?.window?.makeKeyAndOrderFront(nil)
        }
        return true
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        guard let first = urls.first else { return }
        if let controller = mainController {
            controller.openPath(first)
        } else {
            pendingOpenURLs.append(first)
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        mainController?.session.cancelAllOperations()
        mainController?.session.cleanupTemporaryFiles()
        mainController?.session.scheduler.cancelAll()
    }

    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool {
        true
    }

    // MARK: - 菜单

    private func makeMainMenu() -> NSMenu {
        let mainMenu = NSMenu()

        // 应用菜单
        let appMenuItem = NSMenuItem()
        let appMenu = NSMenu(title: AppInfo.name)
        appMenu.addItem(menuItem("关于 影·筛", #selector(aboutAction), ""))
        appMenu.addItem(.separator())
        appMenu.addItem(menuItem("设置…", #selector(settingsAction), ","))
        appMenu.addItem(.separator())
        let hideItem = NSMenuItem(title: "隐藏 影·筛", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        hideItem.target = NSApp
        appMenu.addItem(hideItem)
        let hideOthers = NSMenuItem(title: "隐藏其他", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        hideOthers.target = NSApp
        appMenu.addItem(hideOthers)
        let showAll = NSMenuItem(title: "显示全部", action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: "")
        showAll.target = NSApp
        appMenu.addItem(showAll)
        appMenu.addItem(.separator())
        let quitItem = NSMenuItem(title: "退出 影·筛", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        quitItem.target = NSApp
        appMenu.addItem(quitItem)
        appMenuItem.submenu = appMenu
        mainMenu.addItem(appMenuItem)

        // 文件
        let fileMenuItem = NSMenuItem()
        let fileMenu = NSMenu(title: "文件")
        fileMenu.addItem(menuItem("打开文件夹…", #selector(openFolderAction), "o"))
        fileMenu.addItem(menuItem("刷新文件夹", #selector(refreshAction), "r"))
        fileMenu.addItem(.separator())
        let closeItem = NSMenuItem(title: "关闭窗口", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        closeItem.target = nil
        fileMenu.addItem(closeItem)
        fileMenuItem.submenu = fileMenu
        mainMenu.addItem(fileMenuItem)

        // 编辑
        let editMenuItem = NSMenuItem()
        let editMenu = NSMenu(title: "编辑")
        editMenu.addItem(menuItem("撤销", #selector(undoAction), "z"))
        editMenu.addItem(menuItem("取消全部复制/移动任务", #selector(cancelAllAction), "."))
        editMenuItem.submenu = editMenu
        mainMenu.addItem(editMenuItem)

        // 显示
        let viewMenuItem = NSMenuItem()
        let viewMenu = NSMenu(title: "显示")
        viewMenu.addItem(menuItem("适应窗口", #selector(fitAction), "0"))
        viewMenu.addItem(menuItem("100% 原图", #selector(actualAction), "1"))
        viewMenu.addItem(menuItem("放大", #selector(zoomInAction), "="))
        viewMenu.addItem(menuItem("缩小", #selector(zoomOutAction), "-"))
        viewMenuItem.submenu = viewMenu
        mainMenu.addItem(viewMenuItem)

        // 帮助
        let helpMenuItem = NSMenuItem()
        let helpMenu = NSMenu(title: "帮助")
        helpMenu.addItem(menuItem("快捷键说明", #selector(helpAction), ""))
        helpMenuItem.submenu = helpMenu
        mainMenu.addItem(helpMenuItem)

        return mainMenu
    }

    private func menuItem(_ title: String, _ selector: Selector, _ key: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: selector, keyEquivalent: key)
        item.target = self
        return item
    }

    // MARK: - 菜单动作

    @objc private func aboutAction() {
        mainController?.showAbout()
    }

    @objc private func settingsAction() {
        mainController?.settingsPressed()
    }

    @objc private func openFolderAction() {
        mainController?.openFolderPanel()
    }

    @objc private func refreshAction() {
        mainController?.refreshAction()
    }

    @objc private func undoAction() {
        mainController?.undoAction()
    }

    @objc private func cancelAllAction() {
        mainController?.cancelAllAction()
    }

    @objc private func fitAction() {
        mainController?.fitAction()
    }

    @objc private func actualAction() {
        mainController?.actualAction()
    }

    @objc private func zoomInAction() {
        mainController?.zoomInAction()
    }

    @objc private func zoomOutAction() {
        mainController?.zoomOutAction()
    }

    @objc private func helpAction() {
        mainController?.shortcutsPressed()
    }
}
