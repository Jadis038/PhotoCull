// 影·筛 / PhotoCull — 下方横向缩略图条（虚拟化）
import AppKit
import ImageIO

/// 竖向滚轮映射为横向滚动，方便鼠标用户浏览缩略图条
final class HorizontalWheelScrollView: NSScrollView {
    override func scrollWheel(with event: NSEvent) {
        let dx = event.scrollingDeltaX
        let dy = event.scrollingDeltaY
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
        updateCollectionFrame()
        collectionView.reloadData()
        scrollView.contentView.scroll(to: NSPoint(x: 0, y: 0))
        scrollView.reflectScrolledClipView(scrollView.contentView)
    }

    private func updateCollectionFrame() {
        let height = max(bounds.height, 174)
        let width = max(bounds.width, 10 + CGFloat(items.count) * 176 + CGFloat(max(0, items.count - 1)) * 8 + 10)
        collectionView.frame = NSRect(x: 0, y: 0, width: width, height: height)
    }

    override func layout() {
        super.layout()
        updateCollectionFrame()
    }

    func setSelected(index: Int, scroll: Bool) {
        guard index >= 0, index < items.count else { return }
        selectedIndex = index
        refreshSelectionForVisibleCells()
        if scroll {
            revealIndexIfNeeded(index)
        }
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
        updateBorder()
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
