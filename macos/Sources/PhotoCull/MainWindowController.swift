// 影·筛 / PhotoCull — 主窗口与交互
import AppKit
import ImageIO

final class RootDropView: NSView {
    weak var controller: MainWindowController?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        registerForDraggedTypes([.fileURL])
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        guard let urls = draggedURLs(sender), !urls.isEmpty else { return [] }
        return .copy
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard let urls = draggedURLs(sender), let first = urls.first else { return false }
        controller?.handleDrop(first: first)
        return true
    }

    private func draggedURLs(_ sender: NSDraggingInfo) -> [URL]? {
        sender.draggingPasteboard.readObjects(forClasses: [NSURL.self],
                                             options: [.urlReadingFileURLsOnly: true]) as? [URL]
    }
}

final class MainWindowController: NSWindowController, NSWindowDelegate {
    let session = ViewerSession()

    private let rootView = RootDropView()
    private let topBar = NSView()
    private let canvas = ImageCanvasView()
    private let strip = ThumbnailStripView()
    private let sidebar = InfoSidebarView()

    private let openButton = NSButton(title: "", target: nil, action: nil)
    private let folderLabel = NSTextField(labelWithString: "未打开文件夹")
    private let sortPopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let orderButton = NSButton(title: "", target: nil, action: nil)
    private let modeControl = NSSegmentedControl(labels: ["复制", "移动"], trackingMode: .selectOne, target: nil, action: nil)
    private let undoButton = NSButton(title: "", target: nil, action: nil)
    private let counterLabel = NSTextField(labelWithString: "已筛 0 / 共 0")
    private let settingsButton = NSButton(title: "", target: nil, action: nil)
    private let needleCheckbox = NSButton(checkboxWithTitle: "帧针模式", target: nil, action: nil)
    private let shortcutsButton = NSButton(title: "", target: nil, action: nil)
    private var shortcutsWindowController: ShortcutsWindowController?

    var canvasImageForTesting: CGImage? { canvas.image }

    private var settingsWindowController: SettingsWindowController?
    private var keyMonitor: Any?
    private var notificationTokens: [NSObjectProtocol] = []
    private var displayedItem: PhotoItem?
    private var fullRequestedPath: String?
    private var thumbDone = 0
    private var thumbTotal = 0
    private var queueCount = 0
    private var lastAlertDate = Date.distantPast

    init() {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1360, height: 900),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = AppInfo.name
        window.minSize = NSSize(width: 1000, height: 800)
        window.isReleasedWhenClosed = false
        window.center()
        super.init(window: window)
        window.delegate = self
        window.tabbingMode = .disallowed

        setupContent()
        setupSession()
        setupShortcuts()
        syncSettingsUI()
        canvas.clear()
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    deinit {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        notificationTokens.forEach { NotificationCenter.default.removeObserver($0) }
    }

    // MARK: - 界面搭建

    private func setupContent() {
        guard let window else { return }
        rootView.controller = self
        window.contentView = rootView

        // 顶栏
        topBar.translatesAutoresizingMaskIntoConstraints = false
        topBar.wantsLayer = true
        topBar.layer?.backgroundColor = NSColor.windowBackgroundColor.cgColor
        rootView.addSubview(topBar)

        openButton.bezelStyle = .rounded
        openButton.title = "打开文件夹"
        openButton.image = NSImage(systemSymbolName: "folder", accessibilityDescription: nil)
        openButton.imagePosition = .imageLeading
        openButton.target = self
        openButton.action = #selector(openFolderPressed)

        folderLabel.font = NSFont.systemFont(ofSize: 13, weight: .medium)
        folderLabel.lineBreakMode = .byTruncatingMiddle
        folderLabel.setContentHuggingPriority(.defaultLow, for: .horizontal)
        folderLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        sortPopup.addItems(withTitles: SortKey.allCases.map { $0.label })
        sortPopup.target = self
        sortPopup.action = #selector(sortChanged)
        sortPopup.toolTip = "排序方式"

        orderButton.bezelStyle = .rounded
        orderButton.image = NSImage(systemSymbolName: "arrow.up", accessibilityDescription: "升序")
        orderButton.target = self
        orderButton.action = #selector(orderToggled)
        orderButton.toolTip = "切换升序/降序"

        modeControl.selectedSegment = 0
        modeControl.target = self
        modeControl.action = #selector(modeChanged)
        modeControl.toolTip = "标记方式：复制到「筛选」或移动到「筛选」"

        undoButton.bezelStyle = .rounded
        undoButton.image = NSImage(systemSymbolName: "arrow.uturn.backward", accessibilityDescription: "撤销")
        undoButton.isEnabled = false
        undoButton.target = self
        undoButton.action = #selector(undoPressed)
        undoButton.toolTip = "撤销上一步（⌘Z）"

        counterLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        counterLabel.alignment = .right
        counterLabel.setContentHuggingPriority(.required, for: .horizontal)

        settingsButton.bezelStyle = .rounded
        settingsButton.image = NSImage(systemSymbolName: "gearshape", accessibilityDescription: "设置")
        settingsButton.target = self
        settingsButton.action = #selector(settingsPressed)

        needleCheckbox.state = AppSettings.shared.frameNeedleEnabled ? .on : .off
        needleCheckbox.target = self
        needleCheckbox.action = #selector(needleToggled)
        needleCheckbox.toolTip = "帧针模式：滚动缩略图条，中心帧针对准的图片即当前图"

        shortcutsButton.bezelStyle = .rounded
        shortcutsButton.image = NSImage(systemSymbolName: "keyboard", accessibilityDescription: "快捷键")
        shortcutsButton.toolTip = "快捷键说明"
        shortcutsButton.target = self
        shortcutsButton.action = #selector(shortcutsPressed)

        let spacer = NSView()
        spacer.translatesAutoresizingMaskIntoConstraints = false
        spacer.setContentHuggingPriority(.init(1), for: .horizontal)

        let stack = NSStackView(views: [openButton, folderLabel, spacer, sortPopup, orderButton, modeControl, needleCheckbox, undoButton, counterLabel, shortcutsButton, settingsButton])
        stack.translatesAutoresizingMaskIntoConstraints = false
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 8
        topBar.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: topBar.leadingAnchor, constant: 14),
            stack.trailingAnchor.constraint(equalTo: topBar.trailingAnchor, constant: -14),
            stack.centerYAnchor.constraint(equalTo: topBar.centerYAnchor),
            folderLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 120)
        ])

        let topSeparator = NSBox()
        topSeparator.boxType = .separator
        topSeparator.translatesAutoresizingMaskIntoConstraints = false
        topBar.addSubview(topSeparator)

        // 大图区
        canvas.translatesAutoresizingMaskIntoConstraints = false
        rootView.addSubview(canvas)

        // 缩略图条
        strip.translatesAutoresizingMaskIntoConstraints = false
        rootView.addSubview(strip)

        // 信息栏
        sidebar.translatesAutoresizingMaskIntoConstraints = false
        rootView.addSubview(sidebar)
        sidebar.onCancelQueue = { [weak self] in self?.session.cancelAllOperations() }

        let stripTopLine = NSBox()
        stripTopLine.boxType = .separator
        stripTopLine.translatesAutoresizingMaskIntoConstraints = false
        rootView.addSubview(stripTopLine)

        NSLayoutConstraint.activate([
            topBar.topAnchor.constraint(equalTo: rootView.topAnchor),
            topBar.leadingAnchor.constraint(equalTo: rootView.leadingAnchor),
            topBar.trailingAnchor.constraint(equalTo: rootView.trailingAnchor),
            topBar.heightAnchor.constraint(equalToConstant: 52),

            topSeparator.leadingAnchor.constraint(equalTo: topBar.leadingAnchor),
            topSeparator.trailingAnchor.constraint(equalTo: topBar.trailingAnchor),
            topSeparator.bottomAnchor.constraint(equalTo: topBar.bottomAnchor),
            topSeparator.heightAnchor.constraint(equalToConstant: 1),

            sidebar.topAnchor.constraint(equalTo: topBar.bottomAnchor),
            sidebar.trailingAnchor.constraint(equalTo: rootView.trailingAnchor),
            sidebar.bottomAnchor.constraint(equalTo: rootView.bottomAnchor),
            sidebar.widthAnchor.constraint(equalToConstant: 300),

            strip.leadingAnchor.constraint(equalTo: rootView.leadingAnchor),
            strip.trailingAnchor.constraint(equalTo: sidebar.leadingAnchor),
            strip.bottomAnchor.constraint(equalTo: rootView.bottomAnchor),
            strip.heightAnchor.constraint(equalToConstant: 190),

            stripTopLine.leadingAnchor.constraint(equalTo: strip.leadingAnchor),
            stripTopLine.trailingAnchor.constraint(equalTo: strip.trailingAnchor),
            stripTopLine.topAnchor.constraint(equalTo: strip.topAnchor),
            stripTopLine.heightAnchor.constraint(equalToConstant: 1),

            canvas.topAnchor.constraint(equalTo: topBar.bottomAnchor),
            canvas.leadingAnchor.constraint(equalTo: rootView.leadingAnchor),
            canvas.trailingAnchor.constraint(equalTo: sidebar.leadingAnchor),
            canvas.bottomAnchor.constraint(equalTo: strip.topAnchor)
        ])

        canvas.onZoomReachedFull = { [weak self] in
            self?.requestFullIfNeeded()
        }
        canvas.contextMenuProvider = { [weak self] in
            self?.makeContextMenu(for: self?.displayedItem)
        }

        strip.onSelect = { [weak self] index in
            self?.session.select(index: index)
        }
        strip.onToggleMark = { [weak self] item in
            self?.session.toggleMark(item)
        }
        strip.onNeedleSelect = { [weak self] index in
            self?.session.select(index: index)
        }
        strip.setNeedleEnabled(AppSettings.shared.frameNeedleEnabled)
        strip.onRequestThumbnail = { [weak self] item in
            self?.session.requestThumbnail(for: item)
        }
        strip.cachedImage = { [weak self] item in
            self?.cachedThumbnail(for: item)
        }
        strip.contextMenuProvider = { [weak self] item in
            self?.makeContextMenu(for: item)
        }
    }

    private func cachedThumbnail(for item: PhotoItem) -> CGImage? {
        let key = ThumbnailCache.cacheKey(url: item.decodeURL, size: item.size, mtime: item.mtime, kind: .thumb)
        return session.memoryCache.image(for: key)
    }

    // MARK: - 会话事件

    private func setupSession() {
        session.onFolderOpened = { [weak self] summary in
            guard let self else { return }
            self.folderLabel.stringValue = summary.folder.lastPathComponent
            self.folderLabel.toolTip = summary.folder.path
            self.window?.subtitle = summary.folder.lastPathComponent
            self.strip.setItems(self.session.items)
            self.updateCounter()
            self.updateCounts()
            self.sidebar.update(item: nil)
            if summary.total == 0 {
                self.showAlert(title: "没有可显示的照片",
                               message: "这个文件夹第一层没有支持的图片格式。\n支持 JPG / PNG / TIFF / WebP / HEIC / AVIF / JXL 以及 CR2 / CR3 / NEF / ARW / RAF / ORF / RW2 / PEF / DNG / SRW 等 RAW。")
            } else if summary.dataless > 0 {
                self.sidebar.setStatusText("\(summary.dataless) 个 iCloud 文件尚未下载")
            }
        }

        session.onScanFailed = { [weak self] message in
            self?.showAlert(title: "无法打开文件夹", message: message)
        }

        session.onSelectionChanged = { [weak self] item in
            self?.display(item)
        }

        session.onMetadataReady = { [weak self] item in
            guard let self, let displayed = self.displayedItem, displayed === item else { return }
            self.sidebar.update(item: item)
            if let size = item.nativeDisplaySize {
                self.canvas.updateNativeSize(size)
            }
        }

        session.onPreviewReady = { [weak self] path, image in
            guard let self, let displayed = self.displayedItem, displayed.url.path == path else { return }
            if let image {
                self.canvas.setImage(image, identity: path,
                                     nativeSize: displayed.nativeDisplaySize ?? .zero,
                                     resetZoom: true)
                self.sidebar.setStatusText(nil)
            } else {
                self.canvas.setPlaceholder("无法打开这个文件（\(displayed.ext.uppercased())）")
                self.sidebar.setStatusText("无法解码该格式")
            }
        }

        session.onFullReady = { [weak self] path, image in
            guard let self, let displayed = self.displayedItem, displayed.url.path == path else { return }
            if let image {
                self.canvas.setFullImage(image, identity: path)
                if displayed.nativeDisplaySize == nil {
                    self.canvas.updateNativeSize(CGSize(width: image.width, height: image.height))
                }
                self.sidebar.setStatusText("原图已加载")
            } else {
                self.sidebar.setStatusText("原图解码失败，已保留预览")
            }
        }

        session.onThumbnailReady = { [weak self] path, image in
            self?.strip.updateThumbnail(path: path, image: image)
        }

        session.onItemAuxChanged = { [weak self] item in
            guard let self else { return }
            self.strip.updateAuxState(for: item)
            if let displayed = self.displayedItem, displayed === item {
                self.sidebar.update(item: item)
            }
        }

        session.onItemMarkChanged = { [weak self] item in
            guard let self else { return }
            self.strip.updateMarkState(for: item)
            if let displayed = self.displayedItem, displayed === item {
                self.sidebar.update(item: item)
            }
            self.updateCounter()
        }

        session.onCountsChanged = { [weak self] _, _ in
            self?.updateCounts()
        }

        session.onThumbnailProgress = { [weak self] done, total in
            guard let self else { return }
            self.thumbDone = done
            self.thumbTotal = total
            self.sidebar.setThumbnailProgress(done: done, total: total)
        }

        session.onStatusText = { [weak self] text in
            self?.sidebar.setStatusText(text)
        }

        session.onError = { [weak self] message in
            self?.showAlert(title: "操作失败", message: message)
        }

        session.onUndoAvailabilityChanged = { [weak self] canUndo in
            self?.undoButton.isEnabled = canUndo
        }

        session.onQueueCountChanged = { [weak self] count in
            guard let self else { return }
            self.queueCount = count
            self.sidebar.setQueueCount(count)
            self.updateCounter()
        }

        session.onFoldersSwitched = { [weak self] in
            guard let self else { return }
            self.strip.setItems(self.session.items)
        }

        notificationTokens.append(NotificationCenter.default.addObserver(
            forName: .photoCullSettingsChanged, object: nil, queue: .main
        ) { [weak self] _ in
            self?.syncSettingsUI()
            self?.session.applySettings()
        })
    }

    private func display(_ item: PhotoItem) {
        displayedItem = item
        fullRequestedPath = nil
        sidebar.update(item: item)
        canvas.setImage(nil, identity: item.url.path,
                        nativeSize: item.nativeDisplaySize ?? .zero,
                        resetZoom: true, placeholder: "正在加载 \(item.name)…")
        if let index = session.items.firstIndex(where: { $0 === item }) {
            strip.setSelected(index: index, scroll: true)
        }
        sidebar.setStatusText(nil)
    }

    private func updateCounter() {
        let marked = session.markedCount
        let total = session.items.count
        var text = "已筛 \(marked) / 共 \(total)"
        if queueCount > 0 { text += " · 队列 \(queueCount)" }
        counterLabel.stringValue = text
    }

    private func updateCounts() {
        sidebar.setCounts(marked: session.markedCount, total: session.items.count)
        updateCounter()
    }

    private func requestFullIfNeeded() {
        guard let item = displayedItem else { return }
        guard fullRequestedPath != item.url.path else { return }
        fullRequestedPath = item.url.path
        sidebar.setStatusText("正在加载原图…")
        session.requestFull(for: item)
    }

    // MARK: - 操作

    @objc func openFolderPressed() {
        openFolderPanel()
    }

    func openFolderPanel() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        panel.prompt = "打开"
        panel.message = "选择包含照片的文件夹，或选择单张图片"
        if panel.runModal() == .OK, let url = panel.url {
            requestOpen(url)
        }
    }

    func openPath(_ url: URL) {
        requestOpen(url)
    }

    func handleDrop(first: URL) {
        requestOpen(first)
    }

    private func requestOpen(_ url: URL) {
        if (session.ensureEngine()?.queueCount ?? 0) > 0 {
            let alert = NSAlert()
            alert.messageText = "还有复制任务正在执行"
            alert.informativeText = "切换文件夹会取消正在排队的任务。已经复制完成的文件不会受影响。"
            alert.addButton(withTitle: "继续打开并取消任务")
            alert.addButton(withTitle: "取消")
            if alert.runModal() != .alertFirstButtonReturn { return }
            session.cancelAllOperations()
        }
        session.open(url: url)
    }

    @objc private func sortChanged() {
        let index = sortPopup.indexOfSelectedItem
        guard index >= 0, index < SortKey.allCases.count else { return }
        session.setSort(key: SortKey.allCases[index], order: AppSettings.shared.sortOrder)
    }

    @objc private func orderToggled() {
        let newOrder: SortOrder = AppSettings.shared.sortOrder == .ascending ? .descending : .ascending
        AppSettings.shared.sortOrder = newOrder
        session.setSort(key: AppSettings.shared.sortKey, order: newOrder)
        syncSettingsUI()
    }

    @objc private func modeChanged() {
        AppSettings.shared.operationMode = modeControl.selectedSegment == 0 ? .copy : .move
        NotificationCenter.default.post(name: .photoCullSettingsChanged, object: nil)
    }

    @objc private func undoPressed() {
        session.undo()
    }

    @objc func settingsPressed() {
        if settingsWindowController == nil {
            settingsWindowController = SettingsWindowController(session: session)
        }
        settingsWindowController?.showWindow(nil)
        settingsWindowController?.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc func refreshAction() {
        session.refresh()
    }

    @objc func undoAction() {
        session.undo()
    }

    @objc func fitAction() {
        canvas.zoomToFit()
    }

    @objc func actualAction() {
        canvas.zoomToActual()
    }

    @objc func zoomInAction() {
        canvas.zoomIn()
    }

    @objc func zoomOutAction() {
        canvas.zoomOut()
    }

    @objc func cancelAllAction() {
        session.cancelAllOperations()
    }

    private func syncSettingsUI() {
        modeControl.selectedSegment = AppSettings.shared.operationMode == .copy ? 0 : 1
        let sortKeys = SortKey.allCases
        if let index = sortKeys.firstIndex(of: AppSettings.shared.sortKey) {
            sortPopup.selectItem(at: index)
        }
        orderButton.image = NSImage(systemSymbolName: AppSettings.shared.sortOrder == .ascending ? "arrow.up" : "arrow.down",
                                    accessibilityDescription: AppSettings.shared.sortOrder.label)
    }

    // MARK: - 快捷键

    private func setupShortcuts() {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            self?.handleKey(event) ?? event
        }
    }

    private func handleKey(_ event: NSEvent) -> NSEvent? {
        guard let window, window.isKeyWindow, window.attachedSheet == nil else { return event }
        if let responder = window.firstResponder, responder is NSTextView { return event }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let characters = event.charactersIgnoringModifiers?.lowercased() ?? ""

        if flags.contains(.option) || flags.contains(.command) {
            return event
        }
        if flags.contains(.control) {
            if characters == "z" {
                session.undo()
                return nil
            }
            if characters == "o" {
                openFolderPanel()
                return nil
            }
            return event
        }

        switch event.keyCode {
        case 123: // ←
            session.select(offset: -1)
            return nil
        case 124: // →
            session.select(offset: 1)
            return nil
        case 126: // ↑
            canvas.zoomInStep()
            return nil
        case 125: // ↓
            canvas.zoomOutStep()
            return nil
        case 36, 76: // Enter / 小键盘 Enter
            canvas.zoomToActual(anchor: nil)
            return nil
        case 115: // Home
            if !session.items.isEmpty { session.select(index: 0) }
            return nil
        case 119: // End
            if !session.items.isEmpty { session.select(index: session.items.count - 1) }
            return nil
        case 96: // F5
            session.refresh()
            return nil
        case 103: // F11
            window.toggleFullScreen(nil)
            return nil
        case 49: // 空格
            toggleCurrentMark()
            return nil
        default:
            break
        }

        if characters == "x" {
            toggleCurrentMark()
            return nil
        }
        if characters == "s" {
            if let item = session.currentItem { session.toggleLock(item) }
            return nil
        }
        if characters == "z" {
            canvas.zoomToFit()
            return nil
        }
        if characters == "0" {
            if let item = session.currentItem { session.setStars(item, 0) }
            return nil
        }
        if let digit = Int(characters), (1...5).contains(digit) {
            if let item = session.currentItem { session.setStars(item, digit) }
            return nil
        }
        if characters == "+" || characters == "=" {
            canvas.zoomIn()
            return nil
        }
        if characters == "-" {
            canvas.zoomOut()
            return nil
        }
        return event
    }

    @objc private func needleToggled() {
        let enabled = needleCheckbox.state == .on
        AppSettings.shared.frameNeedleEnabled = enabled
        strip.setNeedleEnabled(enabled)
    }

    @objc func shortcutsPressed() {
        if shortcutsWindowController == nil {
            shortcutsWindowController = ShortcutsWindowController()
        }
        shortcutsWindowController?.showWindow(nil)
        shortcutsWindowController?.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func toggleCurrentMark() {
        guard let item = session.currentItem else { return }
        session.toggleMark(item)
    }

    // MARK: - 右键菜单

    private func makeContextMenu(for item: PhotoItem?) -> NSMenu {
        let menu = NSMenu()
        if let item {
            let markTitle: String
            switch item.markState {
            case .marked: markTitle = "取消筛选"
            case .queued, .processing: markTitle = "取消排队的操作"
            default: markTitle = "移入筛选"
            }
            let markItem = NSMenuItem(title: markTitle, action: #selector(contextToggleMark(_:)), keyEquivalent: "")
            markItem.target = self
            markItem.representedObject = item
            menu.addItem(markItem)

            let reveal = NSMenuItem(title: "在访达中显示", action: #selector(contextReveal(_:)), keyEquivalent: "")
            reveal.target = self
            reveal.representedObject = item
            menu.addItem(reveal)
            menu.addItem(.separator())
        }
        let fit = NSMenuItem(title: "适应窗口", action: #selector(fitAction), keyEquivalent: "0")
        fit.target = self
        menu.addItem(fit)
        let actual = NSMenuItem(title: "100% 原图", action: #selector(actualAction), keyEquivalent: "1")
        actual.target = self
        menu.addItem(actual)
        menu.addItem(.separator())
        let refresh = NSMenuItem(title: "刷新文件夹", action: #selector(refreshAction), keyEquivalent: "r")
        refresh.target = self
        menu.addItem(refresh)
        return menu
    }

    @objc private func contextToggleMark(_ sender: NSMenuItem) {
        guard let item = sender.representedObject as? PhotoItem else { return }
        session.toggleMark(item)
    }

    @objc private func contextReveal(_ sender: NSMenuItem) {
        guard let item = sender.representedObject as? PhotoItem else { return }
        FileTools.revealInFinder(item.decodeURL)
    }

    // MARK: - 关于

    func showAbout() {
        let alert = NSAlert()
        alert.messageText = "\(AppInfo.name)  \(AppInfo.version)"
        alert.informativeText = """
        开源照片筛选工具 · 复制到「筛选」而不动原文件

        作者：\(AppInfo.author)
        \(AppInfo.aiCredit)
        主代码许可：MIT
        RAW 解码：macOS ImageIO 系统框架
        图标：Ho Wing Own（YSLGOG）
        界面图标：系统 SF Symbols

        数据目录：~/Library/Application Support/影·筛
        点击左上角红色按钮会直接退出应用。
        """
        alert.addButton(withTitle: "好")
        if let icon = NSApp.applicationIconImage {
            alert.icon = icon
        }
        alert.runModal()
    }

    // MARK: - 提示

    private func showAlert(title: String, message: String) {
        let now = Date()
        if now.timeIntervalSince(lastAlertDate) < 0.5 { return }
        lastAlertDate = now
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: "好")
        if let window, window.isVisible {
            alert.beginSheetModal(for: window, completionHandler: nil)
        } else {
            alert.runModal()
        }
    }

    // MARK: - 窗口

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        true
    }

    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow, closing == window else { return }
        // 用户要求：红色按钮直接退出，而不是只关窗口
        session.cancelAllOperations()
        NSApp.terminate(nil)
    }

    func windowDidBecomeKey(_ notification: Notification) {
        // 首次打开窗口时让大图区成为第一响应者，方向键可用
        if window?.firstResponder === window {
            window?.makeFirstResponder(canvas)
        }
    }
}
