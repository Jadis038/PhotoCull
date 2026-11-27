// 影·筛 / PhotoCull — 快捷键说明窗口
import AppKit

final class ShortcutsWindowController: NSWindowController {
    private let textView = NSTextView()

    init() {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 620),
                              styleMask: [.titled, .closable, .resizable],
                              backing: .buffered, defer: false)
        window.title = "快捷键说明"
        window.isReleasedWhenClosed = false
        window.minSize = NSSize(width: 480, height: 360)
        window.center()
        super.init(window: window)
        buildUI()
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    private func buildUI() {
        guard let content = window?.contentView else { return }
        let scroll = NSScrollView()
        scroll.translatesAutoresizingMaskIntoConstraints = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = false
        scroll.borderType = .noBorder

        textView.isEditable = false
        textView.isSelectable = true
        textView.drawsBackground = false
        textView.textContainerInset = NSSize(width: 18, height: 16)
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.textContainer?.widthTracksTextView = true
        textView.frame = NSRect(x: 0, y: 0, width: 600, height: 600)
        textView.textStorage?.setAttributedString(attributedContent())
        scroll.documentView = textView
        content.addSubview(scroll)
        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: content.topAnchor),
            scroll.bottomAnchor.constraint(equalTo: content.bottomAnchor)
        ])
    }

    private func attributedContent() -> NSAttributedString {
        let text = NSMutableAttributedString()
        let titleAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 15, weight: .semibold),
            .foregroundColor: NSColor.labelColor
        ]
        func append(_ string: String, _ attrs: [NSAttributedString.Key: Any]) {
            text.append(NSAttributedString(string: string, attributes: attrs))
        }

        let paragraph = NSMutableParagraphStyle()
        paragraph.tabStops = [NSTextTab(textAlignment: .left, location: 300, options: [:])]
        paragraph.defaultTabInterval = 300
        let body: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .regular),
            .foregroundColor: NSColor.labelColor,
            .paragraphStyle: paragraph
        ]
        let sections: [(String, [(String, String)])] = [
            ("标记与筛选", [
                ("空格 / X / 双击缩略图", "标记 / 取消标记（画面停留当前图）"),
                ("S", "保护锁定 / 解锁（锁定后禁止取消）"),
                ("1 ~ 5 / 小键盘", "标 1~5 星；同键再按清除"),
                ("0 / 小键盘 0", "清除当前图星标")
            ]),
            ("视图", [
                ("Z", "回到适应窗口"),
                ("Enter", "放大到 100%（像素 1:1）"),
                ("↑ / ↓", "以画面中心为锚点放大 / 缩小约 10%"),
                ("双击大图 / 触控板双指双击", "100% ↔ 适应窗口"),
                ("F11", "全屏 / 还原"),
                ("鼠标滚轮", "上下平移"),
                ("Option + 滚轮", "缩放"),
                ("Command + 滚轮", "左右平移"),
                ("触控板双指滑动 / 捏合", "平移 / 缩放")
            ]),
            ("浏览", [
                ("← / →", "上一张 / 下一张"),
                ("Home / End", "第一张 / 最后一张"),
                ("⌘O / Ctrl+O", "打开文件夹"),
                ("⌘R / F5", "刷新文件夹"),
                ("⌘Z / Ctrl+Z", "撤销上一步"),
                ("帧针模式", "滚动缩略图条，中线对准的图片即当前图")
            ]),
            ("窗口", [
                ("红色关闭按钮", "直接退出应用（不需要 ⌘Q）")
            ])
        ]

        for (index, section) in sections.enumerated() {
            if index > 0 { append("\n\n", body) }
            append(section.0 + "\n", titleAttrs)
            for (key, action) in section.1 {
                append("  \(key)\t\(action)\n", body)
            }
        }
        return text
    }
}
