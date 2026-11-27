// 影·筛 / PhotoCull — 大图查看区：适应窗口 / 100% / 自由缩放 / 拖拽平移
// 缩放百分比统一以原图全分辨率为基准；预览图 → 原图切换时显示尺寸不变。
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
    var contextMenuProvider: (() -> NSMenu?)?

    private(set) var image: CGImage?
    private(set) var imageIdentity: String = ""
    private var nativeSize: CGSize = .zero
    private var zoomMode: ZoomMode = .fit
    /// 以原图像素为基准的缩放：1.0 = 100%（1 原图像素 = 1 point）
    private var zoom: CGFloat = 1
    private var pan: CGPoint = .zero
    private var placeholderText: String?
    private var fullRequested = false

    private var mouseDownPoint: NSPoint?
    private var lastDragPoint: NSPoint?
    private var isPanning = false
    private var trackingArea: NSTrackingArea?

    private let minZoom: CGFloat = 0.02
    private let maxZoom: CGFloat = 12.0

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

    /// 新选中的图片。nativeSize 是去掉 EXIF 旋转后的原图尺寸。
    func setImage(_ image: CGImage?, identity: String, nativeSize: CGSize = .zero,
                  resetZoom: Bool = true, placeholder: String? = nil) {
        self.image = image
        self.imageIdentity = identity
        if nativeSize.width > 1, nativeSize.height > 1 {
            self.nativeSize = nativeSize
        } else if let image {
            self.nativeSize = CGSize(width: image.width, height: image.height)
        }
        self.placeholderText = placeholder ?? (image == nil ? "正在加载…" : nil)
        fullRequested = false
        if resetZoom {
            zoomMode = .fit
            pan = .zero
        }
        needsDisplay = true
        notifyZoom()
    }

    /// 100% 原图解码完成。显示尺寸由 nativeSize*zoom 决定，因此不会跳变。
    func setFullImage(_ image: CGImage, identity: String) {
        guard identity == imageIdentity else { return }
        self.image = image
        placeholderText = nil
        fullRequested = true
        needsDisplay = true
    }

    func updateNativeSize(_ size: CGSize) {
        guard size.width > 1, size.height > 1, size != nativeSize else { return }
        nativeSize = size
        if zoomMode == .fit {
            pan = .zero
        } else {
            pan = clampedPan(pan, zoom: currentZoom)
        }
        needsDisplay = true
        notifyZoom()
    }

    func setPlaceholder(_ text: String?) {
        placeholderText = image == nil ? text : nil
        needsDisplay = true
    }

    func clear() {
        image = nil
        imageIdentity = ""
        nativeSize = .zero
        placeholderText = "拖入文件夹，或点击左上角「打开文件夹」"
        zoomMode = .fit
        pan = .zero
        needsDisplay = true
    }

    var zoomPercentage: Int {
        Int((currentZoom * 100).rounded())
    }

    private var currentZoom: CGFloat {
        switch zoomMode {
        case .fit: return fitZoom()
        case .actual: return 1.0
        case .manual: return max(minZoom, min(zoom, maxZoom))
        }
    }

    private func fitZoom() -> CGFloat {
        guard nativeSize.width > 1, nativeSize.height > 1 else { return 1 }
        let padding: CGFloat = 24
        let availableWidth = max(40, bounds.width - padding * 2)
        let availableHeight = max(40, bounds.height - padding * 2)
        let value = min(availableWidth / nativeSize.width, availableHeight / nativeSize.height)
        return min(max(value, minZoom), 1.0)
    }

    private func displaySize(for zoom: CGFloat) -> CGSize {
        CGSize(width: nativeSize.width * zoom, height: nativeSize.height * zoom)
    }

    // MARK: - 缩放控制

    func zoomToFit() {
        zoomMode = .fit
        pan = .zero
        needsDisplay = true
        notifyZoom()
    }

    /// Enter / 双击：放大到 100% 原图。anchor 为 nil 时以画面中心为锚点。
    func zoomToActual(anchor: NSPoint? = nil) {
        guard image != nil, nativeSize.width > 0 else { return }
        let point = anchor ?? NSPoint(x: bounds.midX, y: bounds.midY)
        applyZoom(1.0, anchor: point, mode: .actual)
        maybeRequestFull()
    }

    func zoomIn() {
        applyZoom(currentZoom * 1.25, anchor: NSPoint(x: bounds.midX, y: bounds.midY), mode: .manual)
    }

    func zoomOut() {
        applyZoom(currentZoom / 1.25, anchor: NSPoint(x: bounds.midX, y: bounds.midY), mode: .manual)
    }

    /// ↑：以画面中心为固定锚点放大 10%
    func zoomInStep() {
        applyZoom(currentZoom * 1.10, anchor: NSPoint(x: bounds.midX, y: bounds.midY), mode: .manual)
    }

    /// ↓：以画面中心为固定锚点缩小 10%
    func zoomOutStep() {
        applyZoom(currentZoom / 1.10, anchor: NSPoint(x: bounds.midX, y: bounds.midY), mode: .manual)
    }

    /// 双击大图 / 触控板双指双击：100% 与适应窗口切换
    private func toggleZoomMode(anchor: NSPoint?) {
        guard image != nil else { return }
        let atActual = abs(currentZoom - 1.0) < 0.01
        if atActual {
            zoomToFit()
        } else {
            zoomToActual(anchor: anchor)
        }
    }

    private func applyZoom(_ target: CGFloat, anchor: NSPoint, mode: ZoomMode) {
        guard image != nil, nativeSize.width > 0, nativeSize.height > 0 else { return }
        let oldZoom = currentZoom
        let newZoom = max(minZoom, min(maxZoom, target))
        guard abs(newZoom - oldZoom) > 0.0001 || mode != zoomMode else { return }

        let oldOrigin = origin(for: oldZoom, pan: pan)
        let imagePoint = CGPoint(x: (anchor.x - oldOrigin.x) / oldZoom,
                                 y: (anchor.y - oldOrigin.y) / oldZoom)
        let newSize = displaySize(for: newZoom)
        let baseOrigin = CGPoint(x: (bounds.width - newSize.width) / 2,
                                 y: (bounds.height - newSize.height) / 2)
        var newPan = CGPoint(x: anchor.x - imagePoint.x * newZoom - baseOrigin.x,
                             y: anchor.y - imagePoint.y * newZoom - baseOrigin.y)
        newPan = clampedPan(newPan, zoom: newZoom)
        zoom = newZoom
        zoomMode = mode
        pan = newPan
        needsDisplay = true
        notifyZoom()
        maybeRequestFull()
    }

    private func notifyZoom() {
        onZoomChanged?(currentZoom)
    }

    /// 放大超过阈值后请求全尺寸原图。预览缩放 ≥1.2 或显示尺寸 ≥95% 原图都触发。
    private func maybeRequestFull() {
        guard image != nil, nativeSize.width > 0, !fullRequested else { return }
        let imageWidth = max(1, CGFloat(image!.width))
        let previewScale = currentZoom * nativeSize.width / imageWidth
        if currentZoom >= 0.95 || previewScale >= 1.2 {
            fullRequested = true
            onZoomReachedFull?()
        }
    }

    // MARK: - 布局计算

    private func origin(for zoom: CGFloat, pan: CGPoint) -> CGPoint {
        let size = displaySize(for: zoom)
        let clamped = clampedPan(pan, zoom: zoom)
        return CGPoint(x: (bounds.width - size.width) / 2 + clamped.x,
                       y: (bounds.height - size.height) / 2 + clamped.y)
    }

    private func clampedPan(_ pan: CGPoint, zoom: CGFloat) -> CGPoint {
        let size = displaySize(for: zoom)
        var result = pan
        if size.width <= bounds.width {
            result.x = 0
        } else {
            let limit = (size.width - bounds.width) / 2
            result.x = max(-limit, min(limit, result.x))
        }
        if size.height <= bounds.height {
            result.y = 0
        } else {
            let limit = (size.height - bounds.height) / 2
            result.y = max(-limit, min(limit, result.y))
        }
        return result
    }

    // MARK: - 绘制

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        context.saveGState()
        defer { context.restoreGState() }
        context.clip(to: bounds)
        context.setFillColor(backgroundColor.cgColor)
        context.fill(bounds)

        guard let image else {
            drawPlaceholder(in: context)
            return
        }

        let z = currentZoom
        let size = displaySize(for: z)
        let point = origin(for: z, pan: pan)
        context.interpolationQuality = (z >= 1.0 || size.width > bounds.width) ? .none : .high
        context.draw(image, in: CGRect(x: point.x, y: point.y, width: size.width, height: size.height))
        drawZoomBadge(in: context)
    }

    private func drawZoomBadge(in context: CGContext) {
        let text: String
        switch zoomMode {
        case .fit: text = "适应窗口 · \(zoomPercentage)%"
        default: text = "\(zoomPercentage)%"
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
        if zoomMode == .fit {
            pan = .zero
        } else {
            pan = clampedPan(pan, zoom: currentZoom)
        }
        needsDisplay = true
    }

    // MARK: - 鼠标 / 滚轮

    override func mouseDown(with event: NSEvent) {
        if event.clickCount >= 2, event.clickCount % 2 == 0 {
            toggleZoomMode(anchor: convert(event.locationInWindow, from: nil))
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
            if hypot(point.x - start.x, point.y - start.y) < 3 { return }
            isPanning = true
            NSCursor.closedHand.push()
        }
        if let last = lastDragPoint {
            var newPan = pan
            newPan.x += point.x - last.x
            newPan.y += point.y - last.y
            pan = clampedPan(newPan, zoom: currentZoom)
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

    private func panBy(dx: CGFloat, dy: CGFloat) {
        guard image != nil else { return }
        var next = pan
        next.x += dx
        next.y += dy
        next = clampedPan(next, zoom: currentZoom)
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
            let delta = precise ? -dy : dy
            let factor = exp(delta * (precise ? 0.012 : 0.12))
            applyZoom(currentZoom * factor, anchor: anchor, mode: .manual)
            return
        }
        if flags.contains(.command) {
            let amount = precise ? dy : dy * 28
            panBy(dx: amount, dy: 0)
            return
        }
        if precise {
            panBy(dx: dx, dy: -dy)
        } else {
            panBy(dx: dx * 20, dy: dy * 20)
        }
    }

    override func magnify(with event: NSEvent) {
        guard image != nil else { return }
        applyZoom(currentZoom * (1 + event.magnification),
                  anchor: convert(event.locationInWindow, from: nil), mode: .manual)
    }

    override func smartMagnify(with event: NSEvent) {
        toggleZoomMode(anchor: convert(event.locationInWindow, from: nil))
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        return contextMenuProvider?()
    }
}
