// 影·筛 / PhotoCull — macOS 原生照片筛选工具
import AppKit
import Foundation
import Darwin

let arguments = CommandLine.arguments

func value(after flag: String) -> String? {
    guard let index = arguments.firstIndex(of: flag), arguments.indices.contains(index + 1) else { return nil }
    let value = arguments[index + 1]
    return value.hasPrefix("--") ? nil : value
}

func has(_ flag: String) -> Bool { arguments.contains(flag) }

if has("--version") {
    print("\(AppInfo.name) \(AppInfo.version) (\(AppInfo.latinName), macOS arm64)")
    exit(0)
}

if has("--help") || has("-h") {
    print("""
    \(AppInfo.name) \(AppInfo.version) — 照片筛选（macOS Apple Silicon）

    用法：
      影·筛.app/Contents/MacOS/影·筛 [文件夹或图片]     打开图形界面
      --selftest                                     运行引擎自检（无需界面）
      --bench <文件夹> [--limit N] [--csv <路径>]    运行性能基准，输出 CSV
      --matrix <文件夹> [--csv <路径>]               输出格式测试矩阵
      --update-check                                检查 GitHub 上的 macOS 更新
      --update-auto                                 无人值守下载并安装更新（测试用）
      --update-mirror                               更新下载强制走 gh-proxy 镜像（测试用）
      --version                                      显示版本
    """)
    exit(0)
}

if has("--update-check") {
    exit(UpdateChecker.runCLI())
}

if has("--selftest") {
    exit(SelfTest.run())
}

if let folder = value(after: "--bench") {
    let csv = value(after: "--csv")
    let limit = value(after: "--limit").flatMap { Int($0) }
    exit(Bench.run(folder: URL(fileURLWithPath: folder), csvPath: csv, limit: limit))
}

if let folder = value(after: "--matrix") {
    let csv = value(after: "--csv")
    exit(FormatMatrix.run(folder: URL(fileURLWithPath: folder), csvPath: csv))
}

if let folder = value(after: "--thumbprobe") {
    exit(ThumbProbe.run(folder: URL(fileURLWithPath: folder)))
}

let app = NSApplication.shared
let delegate = AppDelegate()
if let folder = value(after: "--uitest") {
    delegate.uiTestFolder = URL(fileURLWithPath: folder)
}
if has("--update-auto") {
    UpdateChecker.shared.autoUpdateMode = true
}
if has("--update-mirror") {
    UpdateChecker.shared.forceMirrorOnly = true
}
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
exit(delegate.uiTestExitCode)
