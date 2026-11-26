// 影·筛 / PhotoCull — 大图查看区：适应窗口 / 100% / 自由缩放 / 拖拽平移
import AppKit
import ImageIO

final class ImageCanvasView: NSView {
    private enum ZoomMode {
        case fit
        case actual
        case manual
    }

    var onZoomChanged: ((CGFloat) -> Void)?
    var onZoomReachedFull: (() -> Void)?

    private(set) var image: CGImage?
    private(set) var imageIdentity: String = ""
    private var zoomMode: ZoomMode = .fit
    private var manualScale: CGFloat = 1
    private var pan: CGPoint = .zero
    private var placeholderText: String?
    private var doubleClickAction: (() -> Void)?
    private var fullRequested = false

    var contextMenuProvider: (() -> NSMenu?)?

    private var mouseDownPoint: NSPoint?
    private var lastDragPoint: NSPoint?
    private var isPanning = false
    private var trackingArea: NSTrackingArea?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.backgroundColor = backgroundColor.cgColor
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        wantsLayer = true
    }

    override var isFlipped: Bool { false }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override var mouseDownCanMoveWindow: Bool { false }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(rect: bounds,
                                  options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect],
                                  owner: self, userInfo: nil)
        addTrackingArea(area)
        trackingArea = area
    }

    override func mouseEntered(with event: NSEvent) {
        if isPanning { return }
        NSCursor.openHand.set()
    }

    override func mouseExited(with event: NSEvent) {
        NSCursor.arrow.set()
    }

    private var backgroundColor: NSColor {
        NSColor(calibratedWhite: 0.075, alpha: 1.0)
    }

    // MARK: - 外部接口

    /// 新选中的图片。resetZoom 为真时回到适应窗口。
    func setImage(_ image: CGImage?, identity: String, resetZoom: Bool = true, placeholder: String? = nil) {
        self.image = image
        self.imageIdentity = identity
        self.placeholderText = placeholder ?? (image == nil ? "正在加载…" : nil)
        fullRequested = false
        if resetZoom {
            zoomMode = .fit
            pan = .zero
        }
        needsDisplay = true
        notifyZoom()
    }

    /// 100% 原图解码完成，替换显示但保持当前缩放/位置
    func setFullImage(_ image: CGImage, identity: String) {
        guard identity == imageIdentity else { return }
        self.image = image
        placeholderText = nil
        fullRequested = true
        needsDisplay = true
    }

    func setPlaceholder(_ text: String?) {
        placeholderText = image == nil ? text : nil
        needsDisplay = true
    }

    func clear() {
        image = nil
        imageIdentity = ""
        placeholderText = "拖入文件夹，或点击左上角「打开文件夹」"
        zoomMode = .fit
        pan = .zero
        needsDisplay = true
    }

    // MARK: - 缩放控制

    func zoomToFit() {
        zoomMode = .fit
        pan = .zero
        needsDisplay = true
        notifyZoom()
    }

    func zoomToActual() {
        zoomMode = .actual
        pan = .zero
        needsDisplay = true
        notifyZoom()
        if !fullRequested {
            onZoomReachedFull?()
        }
    }

    func zoomIn() {
        zoom(to: displayScale * 1.25, anchor: NSPoint(x: bounds.midX, y: bounds.midY))
    }

    func zoomOut() {
        zoom(to: displayScale / 1.25, anchor: NSPoint(x: bounds.midX, y: bounds.midY))
    }

    var zoomPercentage: Int {
        Int((displayScale * 100).rounded())
    }

    private var displayScale: CGFloat {
        guard image != nil else { return 1 }
        switch zoomMode {
        case .fit: return fitScale()
        case .actual: return 1.0
        case .manual: return max(0.02, min(manualScale, 8.0))
        }
    }

    private func fitScale() -> CGFloat {
        guard let image else { return 1 }
        let padding: CGFloat = 24
        let availableWidth = max(40, bounds.width - padding * 2)
        let availableHeight = max(40, bounds.height - padding * 2)
        let scale = min(availableWidth / CGFloat(image.width), availableHeight / CGFloat(image.height))
        // 小图不做放大，保持 100% 以内
        return min(max(scale, 0.01), 1.0)
    }

    private func zoom(to targetScale: CGFloat, anchor: NSPoint) {
        guard image != nil else { return }
        let oldScale = displayScale
        let newScale = max(0.02, min(targetScale, 8.0))
        guard abs(newScale - oldScale) > 0.0001 else { return }

        let oldOrigin = origin(for: oldScale, pan: pan)
        let imagePoint = CGPoint(x: (anchor.x - oldOrigin.x) / oldScale,
                                 y: (anchor.y - oldOrigin.y) / oldScale)
        let baseOrigin = CGPoint(x: (bounds.width - CGFloat(image!.width) * newScale) / 2,
                                 y: (bounds.height - CGFloat(image!.height) * newScale) / 2)
        var newPan = CGPoint(x: anchor.x - imagePoint.x * newScale - baseOrigin.x,
                             y: anchor.y - imagePoint.y * newScale - baseOrigin.y)
        newPan = clampedPan(newPan, scale: newScale)
        manualScale = newScale
        zoomMode = .manual
        pan = newPan
        needsDisplay = true
        notifyZoom()
        if newScale >= 0.9 && !fullRequested {
            onZoomReachedFull?()
        }
    }

    private func notifyZoom() {
        onZoomChanged?(displayScale)
    }

    // MARK: - 布局计算

    private func origin(for scale: CGFloat, pan: CGPoint) -> CGPoint {
        guard let image else { return .zero }
        let width = CGFloat(image.width) * scale
        let height = CGFloat(image.height) * scale
        let clamped = clampedPan(pan, scale: scale)
        return CGPoint(x: (bounds.width - width) / 2 + clamped.x,
                       y: (bounds.height - height) / 2 + clamped.y)
    }

    /// 图比窗口小时居中；图比窗口大时限制边缘不进入窗口内部
    private func clampedPan(_ pan: CGPoint, scale: CGFloat) -> CGPoint {
        guard let image else { return .zero }
        let width = CGFloat(image.width) * scale
        let height = CGFloat(image.height) * scale
        var result = pan
        if width <= bounds.width {
            result.x = 0
        } else {
            let limit = (width - bounds.width) / 2
            result.x = max(-limit, min(limit, result.x))
        }
        if height <= bounds.height {
            result.y = 0
        } else {
            let limit = (height - bounds.height) / 2
            result.y = max(-limit, min(limit, result.y))
        }
        return result
    }

    // MARK: - 绘制

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        context.saveGState()
        defer { context.restoreGState() }
        // 大图缩放到 100% 时可能远超视图范围，必须显式裁剪，避免覆盖顶栏
        context.clip(to: bounds)
        context.setFillColor(backgroundColor.cgColor)
        context.fill(bounds)

        guard let image else {
            drawPlaceholder(in: context)
            return
        }

        let scale = displayScale
        let width = CGFloat(image.width) * scale
        let height = CGFloat(image.height) * scale
        let origin = origin(for: scale, pan: pan)
        context.interpolationQuality = scale > 1.01 ? .none : .high
        context.draw(image, in: CGRect(x: origin.x, y: origin.y, width: width, height: height))

        drawZoomBadge(in: context)
    }

    private func drawZoomBadge(in context: CGContext) {
        let text: String
        switch zoomMode {
        case .fit: text = "适应窗口"
        case .actual: text = "100%"
        case .manual: text = "\(zoomPercentage)%"
        }
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .medium),
            .foregroundColor: NSColor.white.withAlphaComponent(0.8)
        ]
        let size = (text as NSString).size(withAttributes: attributes)
        let rect = CGRect(x: 14, y: 12, width: size.width + 16, height: size.height + 6)
        let path = NSBezierPath(roundedRect: rect, xRadius: 5, yRadius: 5)
        NSColor.black.withAlphaComponent(0.45).setFill()
        path.fill()
        (text as NSString).draw(at: NSPoint(x: rect.minX + 8, y: rect.minY + 3), withAttributes: attributes)
    }

    private func drawPlaceholder(in context: CGContext) {
        let text = placeholderText ?? "拖入文件夹，或点击左上角「打开文件夹」"
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 15, weight: .regular),
            .foregroundColor: NSColor.white.withAlphaComponent(0.45),
            .paragraphStyle: paragraph
        ]
        let width = bounds.width - 80
        let height = (text as NSString).boundingRect(
            with: NSSize(width: width, height: 1000),
            options: [.usesLineFragmentOrigin, .usesFontLeading],
            attributes: attributes
        ).height + 8
        let rect = CGRect(x: 40, y: (bounds.height - height) / 2, width: width, height: height)
        (text as NSString).draw(in: rect, withAttributes: attributes)
    }

    override func layout() {
        super.layout()
        needsDisplay = true
    }

    // MARK: - 鼠标 / 滚轮

    override func mouseDown(with event: NSEvent) {
        // 连续双击时 clickCount 会累加为 3/4/5/6；每两次点击切换一次，
        // 不要只认 clickCount == 2，否则第二组双击要等系统双击间隔过期才生效。
        if event.clickCount >= 2, event.clickCount % 2 == 0 {
            toggleZoomMode()
            return
        }
        let point = convert(event.locationInWindow, from: nil)
        mouseDownPoint = point
        lastDragPoint = point
        isPanning = false
        window?.makeFirstResponder(self)
    }

    override func mouseDragged(with event: NSEvent) {
        guard image != nil, let start = mouseDownPoint else { return }
        let point = convert(event.locationInWindow, from: nil)
        if !isPanning {
            // 超过 3px 才开始平移，避免吞掉单击/双击
            if hypot(point.x - start.x, point.y - start.y) < 3 { return }
            isPanning = true
            NSCursor.closedHand.push()
        }
        if let last = lastDragPoint {
            pan.x += point.x - last.x
            pan.y += point.y - last.y
            pan = clampedPan(pan, scale: displayScale)
        }
        lastDragPoint = point
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        if isPanning { NSCursor.pop() }
        isPanning = false
        mouseDownPoint = nil
        lastDragPoint = nil
        NSCursor.openHand.set()
    }

    /// 鼠标在画布上双击 / 触控板双指双击：在 100% 与适应窗口之间切换
    private func toggleZoomMode() {
        guard image != nil else { return }
        if zoomMode == .fit {
            zoomToActual()
        } else {
            zoomToFit()
        }
    }

    private func panBy(dx: CGFloat, dy: CGFloat) {
        guard image != nil else { return }
        var next = pan
        next.x += dx
        next.y += dy
        next = clampedPan(next, scale: displayScale)
        guard next != pan else { return }
        pan = next
        needsDisplay = true
    }

    override func scrollWheel(with event: NSEvent) {
        guard image != nil else { return }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let precise = event.hasPreciseScrollingDeltas
        let dx = event.scrollingDeltaX
        let dy = event.scrollingDeltaY
        guard abs(dx) > 0.001 || abs(dy) > 0.001 else { return }
        let anchor = convert(event.locationInWindow, from: nil)

        if flags.contains(.option) {
            // Option + 滚轮：缩放。触控板的精确 Y 增量方向与画面相反，取反后
            // 双指向上 → 放大，符合直觉；鼠标滚轮保持原方向。
            let delta = precise ? -dy : dy
            let factor = exp(delta * (precise ? 0.012 : 0.12))
            zoom(to: displayScale * factor, anchor: anchor)
            return
        }
        if flags.contains(.command) {
            // Command + 滚轮：水平平移
            let amount = precise ? dy : dy * 28
            panBy(dx: amount, dy: 0)
            return
        }
        // 无修饰键：触控板双指 / 鼠标滚轮平移。
        // 触控板精确滚动的 Y 方向与视图 Y 轴相反：双指往右上滑时，
        // dy 为负；这里取反使图片跟随手指往右上移动。X 轴保持正向。
        if precise {
            panBy(dx: dx, dy: -dy)
        } else {
            panBy(dx: dx * 20, dy: dy * 20)
        }
    }

    override func magnify(with event: NSEvent) {
        guard image != nil else { return }
        let scale = displayScale * (1 + event.magnification)
        zoom(to: scale, anchor: convert(event.locationInWindow, from: nil))
    }

    /// 触控板“智能缩放”（双指双击）
    override func smartMagnify(with event: NSEvent) {
        toggleZoomMode()
    }

    override func keyDown(with event: NSEvent) {
        super.keyDown(with: event)
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        return contextMenuProvider?()
    }
}
