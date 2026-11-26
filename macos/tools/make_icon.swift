// 生成 影·筛 macOS 应用图标（原生绘制，不依赖外部素材）
import AppKit
import Foundation

let outputPath = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Resources/AppIcon-1024.png"
let size = 1024
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
                           bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                           isPlanar: false, colorSpaceName: .deviceRGB,
                           bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

let full = NSRect(x: 0, y: 0, width: size, height: size)
NSColor.clear.setFill()
full.fill()

// 背景圆角方块
let inset: CGFloat = 42
let backgroundRect = full.insetBy(dx: inset, dy: inset)
let backgroundPath = NSBezierPath(roundedRect: backgroundRect, xRadius: 220, yRadius: 220)
let gradient = NSGradient(colors: [
    NSColor(calibratedRed: 0.07, green: 0.10, blue: 0.13, alpha: 1),
    NSColor(calibratedRed: 0.13, green: 0.20, blue: 0.24, alpha: 1)
])!
gradient.draw(in: backgroundPath, angle: -70)

// 细高光边
NSColor.white.withAlphaComponent(0.08).setStroke()
backgroundPath.lineWidth = 2
backgroundPath.stroke()

// 光圈叶片
let center = NSPoint(x: 512, y: 548)
let bladeColors = [
    NSColor(calibratedRed: 0.36, green: 0.48, blue: 0.55, alpha: 1),
    NSColor(calibratedRed: 0.28, green: 0.39, blue: 0.46, alpha: 1),
    NSColor(calibratedRed: 0.44, green: 0.56, blue: 0.62, alpha: 1),
    NSColor(calibratedRed: 0.31, green: 0.42, blue: 0.49, alpha: 1)
]
let outerRadius: CGFloat = 330
let innerRadius: CGFloat = 142
for index in 0..<8 {
    let start = CGFloat(index) * 45 + 3
    let end = start + 39
    let path = NSBezierPath()
    path.appendArc(withCenter: center, radius: outerRadius, startAngle: start, endAngle: end)
    path.appendArc(withCenter: center, radius: innerRadius, startAngle: end, endAngle: start, clockwise: true)
    path.close()
    bladeColors[index % bladeColors.count].setFill()
    path.fill()
    NSColor.black.withAlphaComponent(0.18).setStroke()
    path.lineWidth = 2
    path.stroke()
}

// 外圈
let outerRing = NSBezierPath(ovalIn: NSRect(x: center.x - outerRadius - 10, y: center.y - outerRadius - 10,
                                            width: (outerRadius + 10) * 2, height: (outerRadius + 10) * 2))
NSColor(calibratedRed: 0.55, green: 0.67, blue: 0.72, alpha: 0.8).setStroke()
outerRing.lineWidth = 8
outerRing.stroke()

// 中心孔
let hole = NSBezierPath(ovalIn: NSRect(x: center.x - innerRadius, y: center.y - innerRadius,
                                       width: innerRadius * 2, height: innerRadius * 2))
NSColor(calibratedRed: 0.04, green: 0.06, blue: 0.08, alpha: 1).setFill()
hole.fill()

// 绿色对勾角标
let badgeCenter = NSPoint(x: 742, y: 250)
let badgeRadius: CGFloat = 148
let badgeRect = NSRect(x: badgeCenter.x - badgeRadius, y: badgeCenter.y - badgeRadius,
                       width: badgeRadius * 2, height: badgeRadius * 2)
let shadow = NSShadow()
shadow.shadowColor = NSColor.black.withAlphaComponent(0.45)
shadow.shadowBlurRadius = 24
shadow.shadowOffset = NSSize(width: 0, height: -10)
shadow.set()
NSColor(calibratedRed: 0.15, green: 0.70, blue: 0.40, alpha: 1).setFill()
NSBezierPath(ovalIn: badgeRect).fill()
NSGraphicsContext.current?.restoreGraphicsState()

let badgeBorder = NSBezierPath(ovalIn: badgeRect)
NSColor(calibratedRed: 0.10, green: 0.52, blue: 0.28, alpha: 1).setStroke()
badgeBorder.lineWidth = 10
badgeBorder.stroke()

let check = NSBezierPath()
check.lineWidth = 46
check.lineCapStyle = .round
check.lineJoinStyle = .round
check.move(to: NSPoint(x: badgeCenter.x - 68, y: badgeCenter.y + 4))
check.line(to: NSPoint(x: badgeCenter.x - 16, y: badgeCenter.y - 48))
check.line(to: NSPoint(x: badgeCenter.x + 74, y: badgeCenter.y + 58))
NSColor.white.setStroke()
check.stroke()

NSGraphicsContext.restoreGraphicsState()

let png = rep.representation(using: .png, properties: [:])!
try png.write(to: URL(fileURLWithPath: outputPath))
print(outputPath)
