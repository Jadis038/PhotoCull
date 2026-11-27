// 影·筛 / PhotoCull — 下方横向缩略图条（虚拟化）
import AppKit
import ImageIO

/// 竖向滚轮映射为横向滚动，方便鼠标用户浏览缩略图条
final class HorizontalWheelScrollView: NSScrollView {
    var onUserScroll: (() -> Void)?
    var stepModeEnabled = false
    var onStepScroll: ((Int) -> Void)?

    override func scrollWheel(with event: NSEvent) {
        onUserScroll?()
        let dx = event.scrollingDeltaX
        let dy = event.scrollingDeltaY
        // 帧针模式下的普通鼠标滚轮：一格 = 相邻一张，不做像素滚动
        if stepModeEnabled, !event.hasPreciseScrollingDeltas {
            let delta = abs(dy) > 0.001 ? dy : dx
            guard abs(delta) > 0.001 else { return }
            onStepScroll?(delta < 0 ? 1 : -1)
            return
        }
        if abs(dx) > abs(dy) {
            super.scrollWheel(with: event)
            return
        }
        guard let document = documentView, abs(dy) > 0.001 else { return }
        let maxX = max(0, document.frame.width - contentView.bounds.width)
        let factor: CGFloat = event.hasPreciseScrollingDeltas ? 1.0 : 18.0
        let target = contentView.bounds.origin.x - dy * factor
        let x = min(maxX, max(0, target))
        if abs(x - contentView.bounds.origin.x) > 0.01 {
            contentView.scroll(to: NSPoint(x: x, y: 0))
            reflectScrolledClipView(contentView)
        }
    }
}

final class ThumbnailStripView: NSView {
    static let cellIdentifier = NSUserInterfaceItemIdentifier("PhotoThumbCell")

    private let scrollView = HorizontalWheelScrollView()
    private let collectionView = NSCollectionView()
    private let flowLayout = NSCollectionViewFlowLayout()
    private let emptyLabel = NSTextField(labelWithString: "打开一个文件夹开始筛选")

    private var items: [PhotoItem] = []
    private var selectedIndex: Int = -1
    private let needleView = NeedleView()
    private(set) var needleEnabled = false
    private var suppressNeedleUntil = Date.distantPast
    private var needleDrivenSelection = false

    var onNeedleSelect: ((Int) -> Void)?
    var onSelect: ((Int) -> Void)?
    var onToggleMark: ((PhotoItem) -> Void)?
    var onRequestThumbnail: ((PhotoItem) -> Void)?
    var cachedImage: ((PhotoItem) -> CGImage?)?
    var contextMenuProvider: ((PhotoItem) -> NSMenu?)?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setup()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        setup()
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }

    private func setup() {
        wantsLayer = true
        layer?.backgroundColor = NSColor(calibratedWhite: 0.105, alpha: 1).cgColor

        scrollView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.hasHorizontalScroller = true
        scrollView.hasVerticalScroller = false
        scrollView.autohidesScrollers = true
        scrollView.drawsBackground = false
        scrollView.scrollerStyle = .overlay
        addSubview(scrollView)

        flowLayout.scrollDirection = .horizontal
        flowLayout.itemSize = NSSize(width: 176, height: 158)
        flowLayout.minimumLineSpacing = 8
        flowLayout.minimumInteritemSpacing = 8
        flowLayout.sectionInset = NSEdgeInsets(top: 8, left: 10, bottom: 8, right: 10)

        collectionView.collectionViewLayout = flowLayout
        collectionView.isSelectable = false
        collectionView.backgroundColors = [.clear]
        collectionView.dataSource = self
        collectionView.register(ThumbnailCollectionItem.self, forItemWithIdentifier: Self.cellIdentifier)

        scrollView.documentView = collectionView
        scrollView.contentView.postsBoundsChangedNotifications = true
        NotificationCenter.default.addObserver(self, selector: #selector(scrollBoundsChanged),
                                               name: NSView.boundsDidChangeNotification,
                                               object: scrollView.contentView)
        scrollView.onUserScroll = { [weak self] in
            guard let self else { return }
            self.suppressNeedleUntil = .distantPast
        }
        scrollView.onStepScroll = { [weak self] direction in
            self?.stepBy(direction)
        }

        needleView.translatesAutoresizingMaskIntoConstraints = false
        needleView.isHidden = true
        addSubview(needleView)
        NSLayoutConstraint.activate([
            needleView.leadingAnchor.constraint(equalTo: leadingAnchor),
            needleView.trailingAnchor.constraint(equalTo: trailingAnchor),
            needleView.topAnchor.constraint(equalTo: topAnchor),
            needleView.bottomAnchor.constraint(equalTo: bottomAnchor)
        ])
        NSLayoutConstraint.activate([
            scrollView.leadingAnchor.constraint(equalTo: leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: trailingAnchor),
            scrollView.topAnchor.constraint(equalTo: topAnchor),
            scrollView.bottomAnchor.constraint(equalTo: bottomAnchor)
        ])

        emptyLabel.translatesAutoresizingMaskIntoConstraints = false
        emptyLabel.alignment = .center
        emptyLabel.textColor = NSColor.secondaryLabelColor
        emptyLabel.font = NSFont.systemFont(ofSize: 13)
        addSubview(emptyLabel)
        NSLayoutConstraint.activate([
            emptyLabel.centerXAnchor.constraint(equalTo: centerXAnchor),
            emptyLabel.centerYAnchor.constraint(equalTo: centerYAnchor)
        ])
    }

    func setItems(_ items: [PhotoItem]) {
        self.items = items
        selectedIndex = -1
        emptyLabel.isHidden = !items.isEmpty
        updateLayoutInsets()
        updateCollectionFrame()
        collectionView.reloadData()
        scrollView.contentView.scroll(to: NSPoint(x: 0, y: 0))
        scrollView.reflectScrolledClipView(scrollView.contentView)
        needleView.isHidden = !needleEnabled || items.isEmpty
        if needleEnabled {
            setNeedleCenter()
        }
    }

    private var viewportWidth: CGFloat {
        let value = scrollView.contentView.bounds.width
        return max(1, value > 1 ? value : bounds.width)
    }

    /// 帧针模式下左右留出半屏空白，保证第一张和最后一张也能滚到正中央
    private var sideInset: CGFloat {
        guard needleEnabled else { return 10 }
        return max(10, (viewportWidth - 176) / 2)
    }

    private func updateLayoutInsets() {
        let inset = NSEdgeInsets(top: 8, left: sideInset, bottom: 8, right: sideInset)
        if abs(flowLayout.sectionInset.left - inset.left) > 0.5 {
            flowLayout.sectionInset = inset
            flowLayout.invalidateLayout()
        }
    }

    private func updateCollectionFrame() {
        let height = max(bounds.height, 174)
        let side = sideInset
        let content = side * 2 + CGFloat(items.count) * 176 + CGFloat(max(0, items.count - 1)) * 8
        let width = max(viewportWidth, content)
        collectionView.frame = NSRect(x: 0, y: 0, width: width, height: height)
    }

    override func layout() {
        super.layout()
        updateLayoutInsets()
        updateCollectionFrame()
        collectionView.layoutSubtreeIfNeeded()
        if needleEnabled {
            if selectedIndex >= 0 {
                syncNeedleToItem(selectedIndex)
            } else {
                setNeedleCenter()
            }
        }
    }

    func setSelected(index: Int, scroll: Bool) {
        guard index >= 0, index < items.count else { return }
        let fromNeedle = needleDrivenSelection
        needleDrivenSelection = false
        selectedIndex = index
        refreshSelectionForVisibleCells()
        guard needleEnabled else {
            if scroll { revealIndexIfNeeded(index) }
            return
        }
        if fromNeedle {
            // 帧针驱动的选择：滚动位置和针的位置都已经就位，绝不能再滚一次
            return
        }
        if scroll {
            syncNeedleToItem(index)
        }
    }

    // MARK: - 帧针模式

    func setNeedleEnabled(_ enabled: Bool) {
        needleEnabled = enabled
        scrollView.stepModeEnabled = enabled
        needleView.isHidden = !enabled || items.isEmpty
        updateLayoutInsets()
        updateCollectionFrame()
        collectionView.layoutSubtreeIfNeeded()
        if enabled {
            if selectedIndex >= 0, selectedIndex < items.count {
                syncNeedleToItem(selectedIndex)
            } else {
                setNeedleCenter()
            }
        } else if selectedIndex >= 0 {
            revealIndexIfNeeded(selectedIndex)
        }
    }

    private var itemStride: CGFloat { 184 }
    private var itemCenterOffset: CGFloat { 88 }

    private func itemCenterX(_ index: Int) -> CGFloat {
        sideInset + CGFloat(index) * itemStride + itemCenterOffset
    }

    private func indexForCenter(_ center: CGFloat) -> Int {
        guard !items.isEmpty else { return 0 }
        let raw = (center - sideInset - itemCenterOffset) / itemStride
        return min(items.count - 1, max(0, Int(raw.rounded())))
    }

    private func setNeedleCenter() {
        needleView.needleX = bounds.midX
    }

    /// 程序化选图：把该项带到帧针下；能居中就居中，否则贴图并暂停判定
    private func syncNeedleToItem(_ index: Int) {
        guard needleEnabled, index >= 0, index < items.count else { return }
        collectionView.layoutSubtreeIfNeeded()
        let viewport = scrollView.contentView.bounds.width
        let maxOffset = max(0, collectionView.frame.width - viewport)
        let desired = itemCenterX(index) - viewport / 2
        let offset = min(maxOffset, max(0, desired))
        suppressNeedleUntil = Date().addingTimeInterval(0.22)
        scrollView.contentView.scroll(to: NSPoint(x: offset, y: 0))
        scrollView.reflectScrolledClipView(scrollView.contentView)
        setNeedleCenter()
        refreshSelectionForVisibleCells()
    }

    @objc private func scrollBoundsChanged() {
        guard needleEnabled, !items.isEmpty else { return }
        if Date() < suppressNeedleUntil { return }
        let visible = scrollView.contentView.bounds
        let center = visible.midX
        let candidate = indexForCenter(center)
        setNeedleCenter()
        guard candidate != selectedIndex else { return }
        needleDrivenSelection = true
        onNeedleSelect?(candidate)
    }

    /// 普通鼠标滚轮：一格切相邻一张，帧针保持在正中
    private func stepBy(_ direction: Int) {
        guard needleEnabled, !items.isEmpty, direction != 0 else { return }
        let current = selectedIndex >= 0 ? selectedIndex : 0
        let target = min(items.count - 1, max(0, current + direction))
        guard target != selectedIndex else {
            setNeedleCenter()
            return
        }
        selectedIndex = target
        syncNeedleToItem(target)
        needleDrivenSelection = true
        onNeedleSelect?(target)
    }

    /// 仅当选中项不在可视范围内时，最小幅度滚动把它露出来；
    /// 绝不把选中项强制放到最左边，保留用户当前的滚动位置。
    private func revealIndexIfNeeded(_ index: Int, attempt: Int = 0) {
        DispatchQueue.main.async { [weak self] in
            guard let self, index >= 0, index < self.items.count else { return }
            self.collectionView.layoutSubtreeIfNeeded()
            let indexPath = IndexPath(item: index, section: 0)
            guard let attributes = self.collectionView.layoutAttributesForItem(at: indexPath) else {
                if attempt < 3 {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) {
                        self.revealIndexIfNeeded(index, attempt: attempt + 1)
                    }
                }
                return
            }
            let visible = self.scrollView.contentView.bounds
            let frame = attributes.frame
            let margin: CGFloat = 10
            var targetX = visible.origin.x
            if frame.minX < visible.minX + margin {
                targetX = frame.minX - margin
            } else if frame.maxX > visible.maxX - margin {
                targetX = frame.maxX - visible.maxX + margin
            } else {
                self.refreshSelectionForVisibleCells()
                return
            }
            let maxX = max(0, self.collectionView.frame.width - visible.width)
            targetX = min(maxX, max(0, targetX))
            if abs(targetX - visible.origin.x) > 0.5 {
                self.scrollView.contentView.scroll(to: NSPoint(x: targetX, y: 0))
                self.scrollView.reflectScrolledClipView(self.scrollView.contentView)
            }
            self.refreshSelectionForVisibleCells()
        }
    }

    private func refreshSelectionForVisibleCells() {
        for indexPath in collectionView.indexPathsForVisibleItems() {
            guard let cell = collectionView.item(at: indexPath) as? ThumbnailCollectionItem else { continue }
            cell.cellView.isSelectedItem = indexPath.item == selectedIndex
        }
    }

    func updateThumbnail(path: String, image: CGImage?) {
        guard let index = items.firstIndex(where: { $0.url.path == path }) else { return }
        if let cell = collectionView.item(at: IndexPath(item: index, section: 0)) as? ThumbnailCollectionItem {
            cell.cellView.setImage(image)
        }
    }

    func updateMarkState(for item: PhotoItem) {
        guard let index = items.firstIndex(where: { $0.url.path == item.url.path }) else { return }
        if let cell = collectionView.item(at: IndexPath(item: index, section: 0)) as? ThumbnailCollectionItem {
            cell.cellView.updateMarkState()
        }
    }

    func updateAuxState(for item: PhotoItem) {
        guard let index = items.firstIndex(where: { $0.url.path == item.url.path }) else { return }
        if let cell = collectionView.item(at: IndexPath(item: index, section: 0)) as? ThumbnailCollectionItem {
            cell.cellView.updateAuxBadges()
        }
    }

    func refreshVisibleItems() {
        for cell in collectionView.visibleItems() {
            guard let cell = cell as? ThumbnailCollectionItem else { continue }
            cell.cellView.refresh()
        }
    }

    func selectVisibleIndex(at point: NSPoint) -> Int? {
        guard let indexPath = collectionView.indexPathForItem(at: point) else { return nil }
        return indexPath.item
    }

    func cellIndex(for view: NSView) -> Int? {
        guard let item = items.firstIndex(where: { $0.url.path == (view as? ThumbnailItemView)?.itemPath }) else { return nil }
        return item
    }
}

extension ThumbnailStripView: NSCollectionViewDataSource {
    func collectionView(_ collectionView: NSCollectionView, numberOfItemsInSection section: Int) -> Int {
        items.count
    }

    func collectionView(_ collectionView: NSCollectionView, itemForRepresentedObjectAt indexPath: IndexPath) -> NSCollectionViewItem {
        guard let cell = collectionView.makeItem(withIdentifier: Self.cellIdentifier, for: indexPath) as? ThumbnailCollectionItem,
              indexPath.item < items.count else {
            return NSCollectionViewItem()
        }
        let item = items[indexPath.item]
        var image = cachedImage?(item)
        if image == nil {
            onRequestThumbnail?(item)
        }
        if image == nil, item.thumbReady {
            image = cachedImage?(item)
        }
        cell.configure(item: item, image: image, selected: indexPath.item == selectedIndex)
        cell.cellView.onClick = { [weak self, weak item] in
            guard let self, let item, let index = self.items.firstIndex(where: { $0 === item }) else { return }
            self.selectedIndex = index
            self.onSelect?(index)
        }
        cell.cellView.onDoubleClick = { [weak self, weak item] in
            guard let self, let item else { return }
            self.onToggleMark?(item)
        }
        cell.cellView.menuProvider = { [weak self, weak item] in
            guard let self, let item else { return nil }
            return self.contextMenuProvider?(item)
        }
        return cell
    }
}

// MARK: - 缩略图单元

final class ThumbnailCollectionItem: NSCollectionViewItem {
    var cellView: ThumbnailItemView!

    override func loadView() {
        cellView = ThumbnailItemView(frame: NSRect(x: 0, y: 0, width: 176, height: 158))
        view = cellView
    }

    func configure(item: PhotoItem, image: CGImage?, selected: Bool) {
        cellView.configure(item: item, image: image, selected: selected)
    }
}

final class ThumbnailItemView: NSView {
    private let imageView = NSImageView()
    private let nameLabel = NSTextField(labelWithString: "")
    private let checkBadge = NSImageView()
    private let lockBadge = NSTextField(labelWithString: "🔒")
    private let starBadge = NSTextField(labelWithString: "")
    private let spinner = NSProgressIndicator()
    private let overlay = NSView()
    private let unsupportedLabel = NSTextField(labelWithString: "无法预览")

    private var baseImage: CGImage?
    private var displayedMarkState: MarkState = .none
    var isSelectedItem = false { didSet { updateBorder() } }

    private(set) var itemPath: String = ""
    private weak var item: PhotoItem?

    var onClick: (() -> Void)?
    var onDoubleClick: (() -> Void)?
    var menuProvider: (() -> NSMenu?)?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setup()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        setup()
    }

    override var isFlipped: Bool { false }

    private func setup() {
        wantsLayer = true
        layer?.cornerRadius = 7
        layer?.masksToBounds = true
        layer?.borderWidth = 2
        layer?.borderColor = NSColor.clear.cgColor
        layer?.backgroundColor = NSColor(calibratedWhite: 0.13, alpha: 1).cgColor

        imageView.imageScaling = .scaleProportionallyUpOrDown
        imageView.wantsLayer = true
        imageView.layer?.cornerRadius = 5
        imageView.layer?.masksToBounds = true
        addSubview(imageView)

        overlay.wantsLayer = true
        overlay.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.32).cgColor
        overlay.isHidden = true
        addSubview(overlay)

        nameLabel.font = NSFont.systemFont(ofSize: 11)
        nameLabel.textColor = NSColor.secondaryLabelColor
        nameLabel.alignment = .center
        nameLabel.lineBreakMode = .byTruncatingMiddle
        addSubview(nameLabel)

        checkBadge.image = NSImage(systemSymbolName: "checkmark.circle.fill", accessibilityDescription: "已筛选")
        checkBadge.contentTintColor = NSColor.systemGreen
        checkBadge.symbolConfiguration = NSImage.SymbolConfiguration(pointSize: 20, weight: .bold)
        checkBadge.isHidden = true
        checkBadge.wantsLayer = true
        checkBadge.layer?.shadowColor = NSColor.black.cgColor
        checkBadge.layer?.shadowOpacity = 0.5
        checkBadge.layer?.shadowRadius = 2
        checkBadge.layer?.shadowOffset = CGSize(width: 0, height: -1)
        addSubview(checkBadge)

        lockBadge.font = NSFont.systemFont(ofSize: 11)
        lockBadge.alignment = .center
        lockBadge.isHidden = true
        lockBadge.drawsBackground = true
        lockBadge.backgroundColor = NSColor.black.withAlphaComponent(0.55)
        lockBadge.isBordered = false
        lockBadge.wantsLayer = true
        lockBadge.layer?.cornerRadius = 4
        lockBadge.layer?.shadowColor = NSColor.black.cgColor
        lockBadge.layer?.shadowOpacity = 0.6
        lockBadge.layer?.shadowRadius = 2
        addSubview(lockBadge)

        starBadge.font = NSFont.boldSystemFont(ofSize: 10)
        starBadge.textColor = NSColor.systemYellow
        starBadge.alignment = .center
        starBadge.drawsBackground = true
        starBadge.backgroundColor = NSColor.black.withAlphaComponent(0.55)
        starBadge.isBordered = false
        starBadge.wantsLayer = true
        starBadge.layer?.cornerRadius = 4
        starBadge.isHidden = true
        addSubview(starBadge)

        spinner.style = .spinning
        spinner.controlSize = .small
        spinner.isDisplayedWhenStopped = false
        spinner.isHidden = true
        addSubview(spinner)

        unsupportedLabel.font = NSFont.systemFont(ofSize: 11)
        unsupportedLabel.textColor = NSColor.tertiaryLabelColor
        unsupportedLabel.alignment = .center
        unsupportedLabel.isHidden = true
        addSubview(unsupportedLabel)
    }

    override func layout() {
        super.layout()
        let padding: CGFloat = 4
        let nameHeight: CGFloat = 16
        let imageRect = CGRect(x: padding, y: nameHeight + 4,
                               width: bounds.width - padding * 2,
                               height: bounds.height - nameHeight - 8)
        imageView.frame = imageRect
        overlay.frame = imageRect
        let badgeSize: CGFloat = 22
        checkBadge.frame = CGRect(x: imageRect.maxX - badgeSize - 4,
                                  y: imageRect.maxY - badgeSize - 4,
                                  width: badgeSize, height: badgeSize)
        lockBadge.frame = CGRect(x: imageRect.minX + 4, y: imageRect.maxY - 20, width: 20, height: 16)
        starBadge.frame = CGRect(x: imageRect.minX + 4, y: imageRect.maxY - 39, width: 32, height: 16)
        spinner.frame = CGRect(x: imageRect.midX - 12, y: imageRect.midY - 12, width: 24, height: 24)
        unsupportedLabel.frame = CGRect(x: imageRect.minX, y: imageRect.midY - 8,
                                        width: imageRect.width, height: 18)
        nameLabel.frame = CGRect(x: 6, y: 4, width: bounds.width - 12, height: nameHeight)
        updateBorder()
    }

    private func updateBorder() {
        layer?.borderColor = isSelectedItem
            ? NSColor.controlAccentColor.cgColor
            : NSColor.clear.cgColor
    }

    func configure(item: PhotoItem, image: CGImage?, selected: Bool) {
        self.item = item
        self.itemPath = item.currentURL.path
        nameLabel.stringValue = item.name
        isSelectedItem = selected
        displayedMarkState = item.markState
        setImage(image, force: true)
        updateMarkState()
        updateAuxBadges()
        updateBorder()
        needsLayout = true
    }

    func setImage(_ image: CGImage?, force: Bool = false) {
        if !force, image == nil, baseImage != nil { return }
        baseImage = image
        if image == nil {
            imageView.image = nil
            unsupportedLabel.isHidden = !(item?.thumbReady ?? false)
        } else {
            unsupportedLabel.isHidden = true
            applyDisplayImage()
        }
    }

    private func applyDisplayImage() {
        guard let baseImage else {
            imageView.image = nil
            return
        }
        let source = displayedMarkState == .marked ? (ImageEffects.desaturated(baseImage) ?? baseImage) : baseImage
        imageView.image = NSImage(cgImage: source, size: NSSize(width: source.width, height: source.height))
    }

    func updateMarkState() {
        guard let item else { return }
        displayedMarkState = item.markState
        switch item.markState {
        case .marked:
            checkBadge.isHidden = false
            overlay.isHidden = false
            spinner.stopAnimation(nil)
            spinner.isHidden = true
            nameLabel.textColor = NSColor.systemGreen
        case .queued, .processing:
            checkBadge.isHidden = true
            overlay.isHidden = false
            overlay.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.18).cgColor
            spinner.isHidden = false
            spinner.startAnimation(nil)
            nameLabel.textColor = NSColor.secondaryLabelColor
        case .none, .error:
            checkBadge.isHidden = true
            overlay.isHidden = true
            spinner.stopAnimation(nil)
            spinner.isHidden = true
            nameLabel.textColor = NSColor.secondaryLabelColor
        }
        applyDisplayImage()
    }

    func refresh() {
        setImage(baseImage)
        updateMarkState()
        updateAuxBadges()
        updateBorder()
    }

    func updateAuxBadges() {
        guard let item else { return }
        lockBadge.isHidden = !item.isLocked
        if item.stars > 0 {
            starBadge.stringValue = "★\(item.stars)"
            starBadge.isHidden = false
        } else {
            starBadge.isHidden = true
        }
    }

    override func hitTest(_ point: NSPoint) -> NSView? {
        // point 是父视图坐标系；命中任意子视图时统一交给本视图处理点击
        return super.hitTest(point) != nil ? self : nil
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 1 {
            onClick?()
        } else if event.clickCount % 2 == 0 {
            // 连续双击：2、4、6… 每次都切换标记，避免第二组双击被系统双击间隔吞掉
            onDoubleClick?()
        }
        // 3、5… 是双击后的第三下/第五下，忽略
    }

    override func scrollWheel(with event: NSEvent) {
        if let scrollView = enclosingScrollView {
            scrollView.scrollWheel(with: event)
        } else {
            super.scrollWheel(with: event)
        }
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        return menuProvider?()
    }
}


/// 帧针：2px 竖线 + 顶部三角，固定覆盖在缩略图条上
final class NeedleView: NSView {
    var needleX: CGFloat = 0 { didSet { needsDisplay = true } }

    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        let color = NSColor(srgbRed: 1.0, green: 0.7608, blue: 0.2941, alpha: 1.0)
        color.setStroke()
        color.setFill()
        let x = max(1, min(bounds.width - 1, needleX))
        let line = NSBezierPath()
        line.lineWidth = 2
        line.move(to: NSPoint(x: x, y: 8))
        line.line(to: NSPoint(x: x, y: bounds.height))
        line.stroke()

        let triangle = NSBezierPath()
        triangle.move(to: NSPoint(x: x - 7, y: 0))
        triangle.line(to: NSPoint(x: x + 7, y: 0))
        triangle.line(to: NSPoint(x: x, y: 9))
        triangle.close()
        triangle.fill()
    }
}
