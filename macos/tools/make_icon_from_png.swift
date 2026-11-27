// 从用户提供的 PNG/JPG 生成 macOS 圆角应用图标
import AppKit
import CoreGraphics
import Foundation

guard CommandLine.arguments.count >= 3 else {
    print("用法: make_icon_from_png <输入> <输出 1024.png>")
    exit(1)
}
let input = URL(fileURLWithPath: CommandLine.arguments[1])
let output = URL(fileURLWithPath: CommandLine.arguments[2])

guard let source = CGImageSourceCreateWithURL(input as CFURL, nil),
      let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
    print("无法读取输入图片")
    exit(1)
}

let size = 1024
let colorSpace = CGColorSpaceCreateDeviceRGB()
guard let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8,
                              bytesPerRow: 0, space: colorSpace,
                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
    exit(1)
}
context.clear(CGRect(x: 0, y: 0, width: size, height: size))

// macOS 圆角矩形（squircle 近似）
let inset: CGFloat = 8
let rect = CGRect(x: inset, y: inset, width: CGFloat(size) - inset * 2, height: CGFloat(size) - inset * 2)
let radius: CGFloat = 224
let path = CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)
context.addPath(path)
context.clip()

// 等比铺满（允许轻微裁切）
let imageAspect = CGFloat(image.width) / CGFloat(image.height)
let targetAspect: CGFloat = 1
var drawRect = rect
if imageAspect > targetAspect {
    let width = rect.height * imageAspect
    drawRect = CGRect(x: rect.midX - width / 2, y: rect.minY, width: width, height: rect.height)
} else {
    let height = rect.width / imageAspect
    drawRect = CGRect(x: rect.minX, y: rect.midY - height / 2, width: rect.width, height: height)
}
context.interpolationQuality = .high
context.draw(image, in: drawRect)

// 轻微内描边，增加 Dock 中的轮廓感
context.resetClip()
context.addPath(path)
context.setStrokeColor(CGColor(red: 1, green: 1, blue: 1, alpha: 0.10))
context.setLineWidth(3)
context.strokePath()

guard let result = context.makeImage() else { exit(1) }
guard let destination = CGImageDestinationCreateWithURL(output as CFURL, "public.png" as CFString, 1, nil) else { exit(1) }
CGImageDestinationAddImage(destination, result, nil)
guard CGImageDestinationFinalize(destination) else { exit(1) }
print(output.path)
