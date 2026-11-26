# 「影·筛」macOS 版移植任务（完整交接提示词）

> 把本文件整段交给 Mac 端 DSH 即可开工。源码需要先从 Windows 拷过去（见 §2）。

## 1. 背景与目标

「影·筛」（中间是 U+00B7 间隔号，不是 影筛/・/‧）是一个 Windows 图片快速筛选工具，已交付 v1.0.4（.NET 8 + WPF + LibRaw，MIT 开源）。核心用途：一次打开几千张 RAW，下方缩略图条扫图，单击查看、双击/按 X 把选中的图（含伴生文件）复制进同目录「筛选」文件夹，原文件永不动；取消 = 删副本进回收站。

**本次任务：做 macOS 版**，功能与 Windows 版对齐，中文 UI，交付可安装可运行的 .app（不强求签名公证，但要写清 Gatekeeper 说明）。

## 2. 源码获取

Windows 机器 `D:\DSH工作区\yingshai` 是一个 git 仓库（分支 main，最新提交含全部修复）。请用户把整个文件夹拷贝到 Mac（压缩包/U盘/网盘均可），或建立 git remote 后 clone。目录结构：

```
yingshai/
├─ src/YingShai.Core/    引擎（大部分可复用，见 §4 耦合点）
│  ├─ Scanning/   DirectoryScanner（FindFirstFileExW，需替换）、NaturalComparer（直接用）
│  ├─ Imaging/    LibRawInterop/LibRawImage（P/Invoke，dylib 名要改）、WicImage（WIC，需重写）、
│  │              ShellThumbnail（Windows 专用，Mac 换 QuickLook）、ExifReader、ImageDecoder（编排，逻辑保留）
│  ├─ Cache/      ThumbnailCache（内存 LRU + 磁盘 JPEG + SQLite 索引，直接用）
│  ├─ Jobs/       DecodeScheduler（优先级解码队列，直接用）
│  ├─ Ops/        FileOps（CopyFileExW/SHFileOperation 回收站，需替换）、OperationQueue、MarkService（业务逻辑，直接用）
│  ├─ Index/      StateStore（SQLite WAL，直接用；路径改 macOS）
│  ├─ Session/    ViewerSession（会话，直接用）
│  ├─ Settings/   AppSettings（直接用）
│  └─ Cli/        CliHost/SelfTest/Bench/Matrix（直接用）
├─ src/YingShai.App/     WPF 界面（整体重写为 Avalonia，交互逻辑照抄）
├─ src/YingShai.Cli/     开发期命令行壳
├─ installer/            WiX（Windows 版，Mac 用 .app/.dmg 替代）
├─ native/libraw/        raw_r.dll + 许可证全文（LGPL-2.1/CDDL-1.0，Mac 版同样必须随包带许可证）
└─ docs/                 SPEC.md（设计规格）、验收报告.md（含全部踩坑记录，必读！）
```

## 3. Mac 端技术方案

- **UI**：Avalonia UI 11（XAML 风格与 WPF 接近，社区最成熟）。不要用 MAUI（Mac 支持差）。
- **运行时**：**.NET 10 LTS**（不要用 .NET 8——它的支持期到 2026-11，覆盖不了 macOS 27；Avalonia 11 支持 net10.0）。目标框架 `net10.0`，同时出 `-r osx-arm64` 和 `-r osx-x64`，用 `lipo` 合成 universal2 包，Apple Silicon 与 Intel Mac 都能跑。
- **系统兼容要求（用户明确要求）：兼容 macOS 16、26、27**。Apple 在 macOS 15 之后直接跳到 26（2025 年版本），27 是 2026 年版本；没有"16"这个官方版本号——按最保守口径处理：**LSMinimumSystemVersion 设为 11.0（Big Sur）**，天然覆盖所有现代版本，重点在 26 与 27 上实测。注意：
  - .NET 10 官方支持的 macOS 上限会滞后新系统一到两年，**必须在新系统上实测**（托管应用一般兼容，重点验证 P/Invoke 的 libraw dylib、SQLite native、Avalonia Skia 渲染在新系统的行为）；
  - Info.plist：`LSMinimumSystemVersion=11.0`；目标架构 arm64+x64（universal2），避免依赖 Rosetta；
  - 废纸篓用 `NSWorkspace.trashFile`（现代 API，不要用过时的 NSWorkspace performFileOperation）；
  - 分发如果走正式渠道需要 hardened runtime + 公证（notarytool），未公证包要写清 `xattr -cr` 绕过方法；
  - 新系统可能收紧未签名 dylib 的加载：libraw dylib 要带 install_name，测试 `install_name_tool`/rpath 是否需要处理。
- **LibRaw**：Sdcb.LibRaw 只发布了 win/linux 运行时，**没有 osx 包**。方案：`brew install libraw` 得到 `libraw.dylib`（确认 brew 实际版本号），P/Invoke 的 DllImport 名改为 "libraw"（macOS 按 install_name 解析；若不在默认搜索路径，用 `NativeLibrary.Load("/opt/homebrew/lib/libraw.dylib")` 显式加载，注意 arm64 是 /opt/homebrew、Intel 是 /usr/local）。C API 签名跨平台一致，结构体不用动。
- **打包**：.app bundle（Contents/MacOS/影·筛 + Contents/Resources/图标.icns + Info.plist），可再做 .dmg（create-dmg 或 hdiutil）。未签名时 Gatekeeper 会拦——README 写清 `xattr -cr /Applications/影·筛.app` 或右键打开的绕过方法；有余力再讲 codesign/ad-hoc 签名与公证。
- **数据目录**：`%LOCALAPPDATA%\影·筛` → macOS 用 `~/Library/Application Support/影·筛`（cache/state/logs/settings.json 结构不变）。

## 4. 必须替换的 Windows 专用 API（其余复用）

| Windows 实现 | macOS 替代 |
|---|---|
| DirectoryScanner 的 FindFirstFileExW | `Directory.EnumerateFileSystemEntries` + FileSystemInfo（足够快；保留跳过隐藏/~$/.tmp/云占位的逻辑） |
| FileOps.CopyFileExW（带进度取消） | FileStream 循环读写（64KB~1MB 块，回调进度，支持取消），语义等价 |
| FileOps.RecycleToTrash（SHFileOperation+FOF_ALLOWUNDO） | `NSWorkspace.shared.trashFile`（通过 ObjCRuntime）或进程调 `/usr/bin/osascript`/`trash` CLI；必须进废纸篓可恢复 |
| ShellThumbnail（IShellItemImageFactory） | QuickLook：`QLThumbnailGenerator`（ObjC#绑定）或干脆禁用走 LibRaw（Mac 端先测，不工作就删） |
| WicImage（WIC 解码/缩放/旋转/JPEG 编码） | Avalonia/Skia 位图 + 自己写 EXIF 方向旋转 + JPEG 编码（SkiaSharp 或 Avalonia 内置） |
| WriteableBitmap/CroppedBitmap 视口渲染 | Avalonia 的 WriteableBitmap + CroppedBitmap 同名类；**注意同样要做视口裁剪**（见 §5 坑 7） |
| 注册表 ProgID 文件关联 | Info.plist 的 CFBundleDocumentTypes/CFBundleTypeExtensions + LaunchServices |
| 命名管道单实例（NamedPipeServerStream） | macOS 上 .NET 命名管道是 Unix domain socket，能直接用，换个管道名即可 |
| MSI/WiX | .app + .dmg（create-dmg 或 hdiutil） |
| FindFirstFile 时间字段 | FileSystemInfo.LastWriteTimeUtc |

**环境探测**：解码线程并发默认 6 保持；`YING_SHAI_DATA` 环境变量覆盖逻辑保留（测试用）。

## 5. 必须保留的行为与踩坑教训（docs/验收报告.md 有全部细节，照着避开）

性能路线（照抄，这是项目最难部分）：
1. 列表阶段零解码，只用目录枚举先出清单，后台补缩略图；
2. 每个文件只打开一次 libraw_open_*：同一次拿元数据 + unpack_thumb 内嵌 JPEG；
3. **100% 全解优先用相机内嵌全尺寸 JPEG**（内嵌尺寸 ≥ 原生 90% 就用它）——Nikon Z8 压缩 NEF 用 LibRaw 0.21.1 demosaic 会报 "data corrupted" 且输出花屏，这是实测踩过的坑，内嵌 JPEG 完全绕开且快 8 倍；
4. 三级缓存：内存 LRU（512MB 预算）→ 磁盘 JPEG（SQLite 索引 + LRU 清理，键 = hash(路径|大小|mtime|解码器版本)）→ 源解码；
5. 优先级解码队列：当前大图 > 可见条带 > ±60 张预取 > 全量后台预热；同键去重、可见性变化重排、复制进行时降并发；
6. 缩略图 160px / 预览 1024px；解码用"先读头取尺寸再按目标缩放"，绝不整图载入再缩；
7. 解码结果必须**复制像素成独立位图**（不能让位图引用解码流，2000 张会把内存顶爆）；
8. 位图统一 96 DPI（RAW 内嵌 JPEG 常带 300dpi 密度，不归一化会导致显示尺寸错误）；
9. 缩略图离开可视区 ±200 释放内存。

UI 交互（Windows 版用户已确认的手感，Mac 版要一致）：
- **单击缩略图 = 上方立即查看**；双击缩略图 = 标记/取消；
- 标记/取消后**停留在当前图**（不自动前进）；
- 大图：滚轮上下平移、Ctrl/Cmd+滚轮左右平移、Alt/Option+滚轮以光标为中心缩放、双击在「适应窗口 ↔ 100% 像素 1:1」之间切换（以双击点为锚）；左键拖动平移（**只有拖动超 3px 才捕获鼠标**，否则会吞掉缩略图条的点击——实测踩坑）；
- 100% 大图要用**视口裁剪**方式渲染（超大位图直接摆进窗口 + 负平移会渲染成空白/花屏，Windows 上实测如此，Avalonia 大概率同样）；
- 状态栏：后台任务进度（"正在生成缩略图 x/y"）、待筛数、版本号；
- 信息栏最底部：作者：Галин · 软件图标：Ho Wing Own · 影·筛 v版本。

业务规则（MarkService 逻辑直接复用，别改语义）：
- 复制流程：校验源可读 → 校验磁盘空间（+64MB 余量）→ 首次标记才建「筛选」→ 复制成 `.part` → 原子改名 → 同名冲突改 `(1)` 绝不覆盖 → 伴生文件（同主名 .xmp/.aae/.thm 及 RAW↔JPG 双拍）一起 → 写索引；
- 取消：复制模式删副本进**废纸篓**（原文件绝不动）；移动模式副本移回原位（冲突改名）；每条记录记住自己的 op 类型，切设置不影响旧记录；
- Ctrl+Z 撤销栈（≥50 步）；单写队列并发 1，每张独立成败；
- 一致性核对：副本被外部删 → 自动取消勾选；副本被改名 → 按文件名重匹配；源没了 → 显示"源文件已不存在"；
- 快捷键：←→ 切图、X/Enter 标记、Cmd+Z 撤销、Cmd+O 打开、F5 刷新、0 适应、1 100%。

已知不支持（如实标注，不要假装支持）：X3F（Foveon 专有，LibRaw 构建不含解码）、三星专有压缩 DNG（TIFF 头里 Compression=52546，私有算法无公开实现）。其它 CR2/CR3/NEF/ARW/RAF/ORF/RW2/PEF/DNG/SRW/JPG/PNG/TIFF/HEIC/AVIF/JXL 实测全通。

## 6. 验收（Mac 端必须真实跑）

0. **系统兼容矩阵**：在用户实际的 macOS 16、26、27 上各跑一遍「安装 → 打开 2500 张 Z8 文件夹 → 切图 → 双击 100% → 标记 → 取消」，确认渲染、libraw 加载、废纸篓在新旧系统都正常；任何一个版本出问题都算未完成。
1. `影·筛 --selftest`：把 Windows 版 SelfTest 的 17 组 64 项搬到 Mac（临时目录、回收站行为按 macOS 适配），全绿；
2. `--bench <文件夹> [--cold] [--csv]`：枚举/首屏/切图/100%/预热/命中率/内存，CSV 输出；目标：首屏 <2s、切图 <100ms、100% <3s、2000 张预热 <3min、内存 <1.2GB；
3. `--matrix <文件夹>`：逐文件"能否打开/预览/耗时"矩阵；
4. UI 验收：Avalonia 在 Mac 有真桌面，直接开窗口人工点 + 截图；程序化验收（模拟单击切图、标记停留、双击 100% 断言）参照 Windows 版 AppController 的 --ui-selftest / --real-click-test / --mark-demo 写一遍；
5. 让用户用真实素材（2500 张 Z8 NEF 文件夹）实测：首屏、切图、100% 无花屏、标记、撤销、废纸篓可恢复。

## 7. 工作方式

- 中文注释、中文提交信息、中文 UI；包内 Id 用 ASCII（YingShai），显示名「影·筛」；
- 引擎与 UI 解耦原则不变；SelfTest/Bench/Matrix 无 GUI 可跑；
- 阶段：① Core 平台抽象层改造 + Mac 编译通过 → ② Avalonia 主窗口（大图 + 虚拟化缩略图条 + 信息栏）→ ③ 标记/取消/撤销/废纸篓 → ④ .app 打包 + README（含 Gatekeeper 说明、LibRaw LGPL 许可随包）→ ⑤ 用户实测迭代；
- 不确定的小决策按最小惊讶原则自己定，在 SPEC 里记一笔；影响数据安全（删/覆盖）的先问用户。

## 8. 当前 Windows 版本功能基线（v1.0.4，全部要对齐）

打开方式（按钮/拖拽/文件关联）、第一层图片扫描（排除隐藏/临时/云占位）、自然排序+三种排序、
记忆上次文件夹、上方大图+下方横向缩略图条+右侧信息栏（机型/镜头/焦距/光圈/快门/ISO/拍摄时间/
分辨率/文件大小/色彩空间，缺则「—」）、EXIF 方向、已标记绿勾角标+降饱和且位置永不重排、
标记单张即时入队逐张复制、取消删副本、Cmd+Z 撤销、复制/移动开关、一致性核对与重建、
单实例（管道转发）、`--selftest/--bench/--matrix/--version` CLI。
