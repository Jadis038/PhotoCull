// 影·筛 / PhotoCull — 右侧信息栏与状态区
import AppKit

final class InfoSidebarView: NSVisualEffectView {
    private let headerLabel = NSTextField(labelWithString: "照片信息")
    private var valueLabels: [String: NSTextField] = [:]
    private var titleLabels: [String: NSTextField] = [:]

    private let countsLabel = NSTextField(labelWithString: "已筛 0 / 共 0")
    private let remainingLabel = NSTextField(labelWithString: "待筛 0 张")
    private let thumbProgress = NSProgressIndicator()
    private let thumbStatusLabel = NSTextField(labelWithString: "")
    private let queueLabel = NSTextField(labelWithString: "")
    private let statusLabel = NSTextField(labelWithString: "")
    private let creditLabel = NSTextField(labelWithString: "制作：Галин\n软件图标：Ho Wing Own")
    private let cancelButton = NSButton(title: "取消复制队列", target: nil, action: nil)
    private let separator = NSBox()

    var onCancelQueue: (() -> Void)?

    private let fieldOrder: [(key: String, title: String, lines: Int)] = [
        ("name", "文件名", 2),
        ("state", "状态", 1),
        ("camera", "机型", 1),
        ("lens", "镜头", 2),
        ("focal", "焦距", 1),
        ("aperture", "光圈", 1),
        ("shutter", "快门", 1),
        ("iso", "ISO", 1),
        ("capture", "拍摄时间", 1),
        ("resolution", "分辨率", 1),
        ("size", "文件大小", 1),
        ("color", "色彩空间", 1),
        ("format", "格式", 1)
    ]

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setup()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        setup()
    }

    override var isFlipped: Bool { true }

    private func setup() {
        material = .sidebar
        blendingMode = .behindWindow
        state = .followsWindowActiveState

        headerLabel.font = NSFont.systemFont(ofSize: 12, weight: .semibold)
        headerLabel.textColor = NSColor.secondaryLabelColor
        addSubview(headerLabel)

        for field in fieldOrder {
            let title = NSTextField(labelWithString: field.title)
            title.font = NSFont.systemFont(ofSize: 10, weight: .medium)
            title.textColor = NSColor.tertiaryLabelColor
            addSubview(title)
            titleLabels[field.key] = title

            let value = NSTextField(labelWithString: "—")
            value.font = NSFont.systemFont(ofSize: 12)
            value.textColor = NSColor.labelColor
            value.maximumNumberOfLines = field.lines
            value.lineBreakMode = field.lines > 1 ? .byTruncatingMiddle : .byTruncatingTail
            value.cell?.wraps = field.lines > 1
            addSubview(value)
            valueLabels[field.key] = value
        }

        separator.boxType = .separator
        addSubview(separator)

        countsLabel.font = NSFont.systemFont(ofSize: 12, weight: .medium)
        addSubview(countsLabel)
        remainingLabel.font = NSFont.systemFont(ofSize: 11)
        remainingLabel.textColor = NSColor.secondaryLabelColor
        addSubview(remainingLabel)

        thumbProgress.style = .bar
        thumbProgress.isIndeterminate = false
        thumbProgress.minValue = 0
        thumbProgress.maxValue = 1
        thumbProgress.controlSize = .small
        thumbProgress.isHidden = true
        addSubview(thumbProgress)

        thumbStatusLabel.font = NSFont.systemFont(ofSize: 10)
        thumbStatusLabel.textColor = NSColor.secondaryLabelColor
        addSubview(thumbStatusLabel)

        queueLabel.font = NSFont.systemFont(ofSize: 11, weight: .medium)
        queueLabel.textColor = NSColor.systemOrange
        queueLabel.isHidden = true
        addSubview(queueLabel)

        cancelButton.bezelStyle = .inline
        cancelButton.font = NSFont.systemFont(ofSize: 11)
        cancelButton.isHidden = true
        cancelButton.target = self
        cancelButton.action = #selector(cancelQueueClicked)
        addSubview(cancelButton)

        statusLabel.font = NSFont.systemFont(ofSize: 10)
        statusLabel.textColor = NSColor.secondaryLabelColor
        statusLabel.maximumNumberOfLines = 2
        statusLabel.lineBreakMode = .byTruncatingTail
        addSubview(statusLabel)

        creditLabel.font = NSFont.systemFont(ofSize: 10)
        creditLabel.textColor = NSColor.secondaryLabelColor
        creditLabel.alignment = .right
        creditLabel.maximumNumberOfLines = 2
        creditLabel.lineBreakMode = .byTruncatingTail
        addSubview(creditLabel)
    }

    @objc private func cancelQueueClicked() {
        onCancelQueue?()
    }

    // MARK: - 数据更新

    func update(item: PhotoItem?) {
        guard let item else {
            for (_, label) in valueLabels { label.stringValue = "—" }
            return
        }
        let meta = item.metadata
        setValue("name", item.name)
        switch item.markState {
        case .marked:
            setValue("state", item.sourceMissing ? "已筛选（源文件已不在原位）" : "已移入筛选")
        case .queued:
            setValue("state", "排队中…")
        case .processing:
            setValue("state", (item.record?.mode == .move ? "正在移动…" : "正在复制…"))
        case .error:
            setValue("state", "操作失败")
        case .none:
            setValue("state", "未筛选")
        }
        setValue("camera", MetadataFormat.camera(meta))
        setValue("lens", MetadataFormat.lens(meta))
        setValue("focal", MetadataFormat.focal(meta))
        setValue("aperture", MetadataFormat.aperture(meta))
        setValue("shutter", MetadataFormat.shutter(meta))
        setValue("iso", MetadataFormat.iso(meta))
        setValue("capture", MetadataFormat.captureTime(meta))
        setValue("resolution", MetadataFormat.resolution(meta))
        setValue("size", Fmt.bytes(item.size))
        setValue("color", MetadataFormat.colorSpace(meta))
        setValue("format", MetadataFormat.format(meta))

        valueLabels["name"]?.toolTip = item.currentURL.path
    }

    private func setValue(_ key: String, _ value: String) {
        valueLabels[key]?.stringValue = value.isEmpty ? "—" : value
    }

    func setCounts(marked: Int, total: Int) {
        countsLabel.stringValue = "已筛 \(marked) / 共 \(total)"
        remainingLabel.stringValue = "待筛 \(max(0, total - marked)) 张"
    }

    func setThumbnailProgress(done: Int, total: Int) {
        guard total > 0 else {
            thumbProgress.isHidden = true
            thumbStatusLabel.stringValue = ""
            return
        }
        if done >= total {
            thumbProgress.isHidden = true
            thumbStatusLabel.stringValue = "缩略图已就绪"
        } else {
            thumbProgress.isHidden = false
            thumbProgress.doubleValue = Double(done) / Double(total)
            thumbStatusLabel.stringValue = "正在生成缩略图 \(done) / \(total)"
        }
    }

    func setQueueCount(_ count: Int) {
        if count > 0 {
            queueLabel.stringValue = "复制队列：\(count) 项"
            queueLabel.isHidden = false
            cancelButton.isHidden = false
        } else {
            queueLabel.isHidden = true
            cancelButton.isHidden = true
        }
    }

    func setStatusText(_ text: String?) {
        statusLabel.stringValue = text ?? ""
    }

    // MARK: - 布局

    override func layout() {
        super.layout()
        let inset: CGFloat = 16
        let width = bounds.width - inset * 2
        var y: CGFloat = 14

        headerLabel.frame = CGRect(x: inset, y: y, width: width, height: 16)
        y += 26

        for field in fieldOrder {
            titleLabels[field.key]?.frame = CGRect(x: inset, y: y, width: width, height: 12)
            y += 14
            let valueHeight: CGFloat = field.lines > 1 ? 32 : 16
            valueLabels[field.key]?.frame = CGRect(x: inset, y: y, width: width, height: valueHeight)
            y += valueHeight + 7
        }

        let bottomHeight: CGFloat = 158
        let bottomTop = bounds.height - bottomHeight
        separator.frame = CGRect(x: inset, y: bottomTop - 2, width: width, height: 1)

        countsLabel.frame = CGRect(x: inset, y: bottomTop + 8, width: width, height: 16)
        remainingLabel.frame = CGRect(x: inset, y: bottomTop + 26, width: width, height: 14)

        thumbProgress.frame = CGRect(x: inset, y: bottomTop + 46, width: width, height: 6)
        thumbStatusLabel.frame = CGRect(x: inset, y: bottomTop + 56, width: width, height: 13)

        queueLabel.frame = CGRect(x: inset, y: bottomTop + 76, width: width - 90, height: 15)
        cancelButton.frame = CGRect(x: bounds.width - inset - 84, y: bottomTop + 73, width: 84, height: 20)

        statusLabel.frame = CGRect(x: inset, y: bottomTop + 96, width: width, height: 30)
        creditLabel.frame = CGRect(x: inset, y: bottomTop + 130, width: width, height: 28)
    }
}
