// 影·筛 / PhotoCull — 设置窗口
import AppKit

final class SettingsWindowController: NSWindowController {
    private unowned let session: ViewerSession
    private let modeControl = NSSegmentedControl(labels: ["复制（推荐）", "移动"], trackingMode: .selectOne, target: nil, action: nil)
    private let concurrencyPopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let cachePopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let rememberCheckbox = NSButton(checkboxWithTitle: "记住上次打开的文件夹与排序", target: nil, action: nil)
    private let restoreCheckbox = NSButton(checkboxWithTitle: "启动时自动恢复上次会话", target: nil, action: nil)
    private let updateCheckbox = NSButton(checkboxWithTitle: "启动时检查 GitHub 更新", target: nil, action: nil)
    private let usageLabel = NSTextField(labelWithString: "")
    private let versionLabel = NSTextField(labelWithString: "")

    private let concurrencyValues = [0, 2, 4, 6, 8]
    private let cacheValues = [1.0, 2.0, 5.0, 10.0, 20.0]

    init(session: ViewerSession) {
        self.session = session
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 620),
                              styleMask: [.titled, .closable],
                              backing: .buffered, defer: false)
        window.title = "设置"
        window.isReleasedWhenClosed = false
        window.center()
        super.init(window: window)
        buildUI()
        syncFromSettings()
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func showWindow(_ sender: Any?) {
        super.showWindow(sender)
        refreshUsage()
    }

    private func buildUI() {
        guard let content = window?.contentView else { return }
        let stack = NSStackView()
        stack.translatesAutoresizingMaskIntoConstraints = false
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 14
        stack.edgeInsets = NSEdgeInsets(top: 22, left: 24, bottom: 22, right: 24)
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: content.bottomAnchor)
        ])

        stack.addArrangedSubview(sectionLabel("标记方式"))
        modeControl.widthAnchor.constraint(equalToConstant: 260).isActive = true
        modeControl.target = self
        modeControl.action = #selector(modeChanged)
        stack.addArrangedSubview(modeControl)

        let modeHelp = helpLabel("标记会把照片复制（或移动）到当前文件夹下的「筛选」子文件夹。\n取消勾选会把「筛选」里的副本移入废纸篓，原目录文件绝不会被删除。")
        stack.addArrangedSubview(modeHelp)

        stack.addArrangedSubview(separator())
        stack.addArrangedSubview(sectionLabel("性能"))

        concurrencyPopup.addItems(withTitles: ["自动（推荐）", "2 线程", "4 线程", "6 线程", "8 线程"])
        concurrencyPopup.target = self
        concurrencyPopup.action = #selector(concurrencyChanged)
        stack.addArrangedSubview(row(title: "解码并发", control: concurrencyPopup,
                                     detail: "自动模式在 Apple Silicon 上使用 4 线程；复制任务进行时自动降到 1。"))

        cachePopup.addItems(withTitles: ["1 GB", "2 GB", "5 GB", "10 GB", "20 GB"])
        cachePopup.target = self
        cachePopup.action = #selector(cacheLimitChanged)
        let cacheRow = row(title: "缩略图缓存上限", control: cachePopup,
                           detail: "磁盘缓存按 LRU 清理，可随时手动清空。")
        stack.addArrangedSubview(cacheRow)

        let usageRow = NSStackView(views: [usageLabel, clearCacheButton(), openDataDirButton()])
        usageRow.orientation = .horizontal
        usageRow.spacing = 10
        usageLabel.font = NSFont.systemFont(ofSize: 11)
        usageLabel.textColor = NSColor.secondaryLabelColor
        stack.addArrangedSubview(usageRow)

        stack.addArrangedSubview(separator())
        stack.addArrangedSubview(sectionLabel("会话"))

        rememberCheckbox.target = self
        rememberCheckbox.action = #selector(rememberChanged)
        stack.addArrangedSubview(rememberCheckbox)

        restoreCheckbox.target = self
        restoreCheckbox.action = #selector(restoreChanged)
        stack.addArrangedSubview(restoreCheckbox)

        updateCheckbox.target = self
        updateCheckbox.action = #selector(updateCheckChanged)
        stack.addArrangedSubview(updateCheckbox)

        stack.addArrangedSubview(separator())
        stack.addArrangedSubview(helpLabel("快捷键：空格 / X 标记，Z 适应窗口，Enter 100%，↑/↓ 缩放，S 保护，1–5 星级，←/→ 切图。\n⌘Z 撤销，⌘O 打开文件夹，F11 全屏；点击左上角红色按钮直接退出。"))

        versionLabel.font = NSFont.systemFont(ofSize: 10)
        versionLabel.textColor = NSColor.tertiaryLabelColor
        versionLabel.stringValue = "\(AppInfo.name) \(AppInfo.version) · \(AppInfo.aiCredit)"
        stack.addArrangedSubview(versionLabel)
    }

    private func row(title: String, control: NSView, detail: String) -> NSView {
        let label = NSTextField(labelWithString: title)
        label.font = NSFont.systemFont(ofSize: 12, weight: .medium)
        label.alignment = .right
        label.widthAnchor.constraint(equalToConstant: 110).isActive = true
        let top = NSStackView(views: [label, control])
        top.orientation = .horizontal
        top.alignment = .centerY
        top.spacing = 12

        let detailLabel = helpLabel(detail)
        detailLabel.widthAnchor.constraint(lessThanOrEqualToConstant: 400).isActive = true
        let container = NSStackView(views: [top, detailLabel])
        container.orientation = .vertical
        container.alignment = .leading
        container.spacing = 4
        return container
    }

    private func sectionLabel(_ text: String) -> NSTextField {
        let label = NSTextField(labelWithString: text)
        label.font = NSFont.systemFont(ofSize: 13, weight: .semibold)
        return label
    }

    private func helpLabel(_ text: String) -> NSTextField {
        let label = NSTextField(wrappingLabelWithString: text)
        label.font = NSFont.systemFont(ofSize: 11)
        label.textColor = NSColor.secondaryLabelColor
        label.preferredMaxLayoutWidth = 480
        return label
    }

    private func separator() -> NSBox {
        let box = NSBox()
        box.boxType = .separator
        box.widthAnchor.constraint(equalToConstant: 512).isActive = true
        return box
    }

    private func clearCacheButton() -> NSButton {
        let button = NSButton(title: "清空缓存", target: self, action: #selector(clearCache))
        button.bezelStyle = .rounded
        button.font = NSFont.systemFont(ofSize: 11)
        return button
    }

    private func openDataDirButton() -> NSButton {
        let button = NSButton(title: "打开数据目录", target: self, action: #selector(openDataDir))
        button.bezelStyle = .rounded
        button.font = NSFont.systemFont(ofSize: 11)
        return button
    }

    private func syncFromSettings() {
        let settings = AppSettings.shared
        modeControl.selectedSegment = settings.operationMode == .copy ? 0 : 1
        concurrencyPopup.selectItem(at: concurrencyValues.firstIndex(of: settings.decodeConcurrency) ?? 0)
        let nearest = cacheValues.enumerated().min(by: { abs($0.element - settings.cacheLimitGB) < abs($1.element - settings.cacheLimitGB) })?.offset ?? 2
        cachePopup.selectItem(at: nearest)
        rememberCheckbox.state = settings.rememberLastFolder ? .on : .off
        restoreCheckbox.state = settings.restoreLastSession ? .on : .off
        updateCheckbox.state = settings.checkForUpdates ? .on : .off
        refreshUsage()
    }

    private func refreshUsage() {
        let size = session.diskCache.currentSize()
        usageLabel.stringValue = "当前缓存占用：\(Fmt.bytes(size))"
    }

    @objc private func modeChanged() {
        AppSettings.shared.operationMode = modeControl.selectedSegment == 0 ? .copy : .move
        NotificationCenter.default.post(name: .photoCullSettingsChanged, object: nil)
    }

    @objc private func concurrencyChanged() {
        let index = concurrencyPopup.indexOfSelectedItem
        if index >= 0, index < concurrencyValues.count {
            AppSettings.shared.decodeConcurrency = concurrencyValues[index]
            session.applySettings()
        }
    }

    @objc private func cacheLimitChanged() {
        let index = cachePopup.indexOfSelectedItem
        if index >= 0, index < cacheValues.count {
            AppSettings.shared.cacheLimitGB = cacheValues[index]
            session.applySettings()
            refreshUsage()
        }
    }

    @objc private func rememberChanged() {
        AppSettings.shared.rememberLastFolder = rememberCheckbox.state == .on
    }

    @objc private func restoreChanged() {
        AppSettings.shared.restoreLastSession = restoreCheckbox.state == .on
    }

    @objc private func updateCheckChanged() {
        AppSettings.shared.checkForUpdates = updateCheckbox.state == .on
    }

    @objc private func clearCache() {
        session.clearThumbnailCache()
        refreshUsage()
    }

    @objc private func openDataDir() {
        AppInfo.ensureDirectories()
        NSWorkspace.shared.open(AppInfo.dataDirectory)
    }
}
