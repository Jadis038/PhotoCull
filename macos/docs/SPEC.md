# 影·筛 / PhotoCull（macOS）技术规格

版本：1.0.2 ｜ 平台：macOS 12+，Apple Silicon ｜ 语言：Swift 5 ｜ UI：AppKit ｜ 构建：SwiftPM + Command Line Tools

---

## 1. 目标与边界

### 目标
2000~3000 张 RAW 的一次筛选工作流：打开目录 → 快速扫过 → 逐张标记 → 自动累积到「筛选」→ 随时撤销。核心指标是**缩略图出现速度**和**标记不阻塞**。

### 非目标（明确不做）
修图调色、导出转换、批量重命名、星级评分、人脸识别、云盘上传、多文件夹同时打开、删除原文件。

### 数据安全底线
- 原目录文件永不删除；
- 标记默认复制，移动模式可切换；
- 取消标记只处理「筛选」副本，走废纸篓；
- 同名冲突改名不覆盖；先写 `.part` 再原子改名；失败回滚。

---

## 2. 技术选型与关键决策

| 决策 | 选择 | 理由 |
|---|---|---|
| UI 框架 | **AppKit（纯代码）** | 无 Xcode/Storyboard 依赖；大图/缩略图需要精细控制；SwiftUI 在本项目没有收益 |
| 语言/构建 | Swift 5 + SwiftPM | CLT 即可构建；`.app` 手工组装 |
| RAW 解码 | **macOS ImageIO 系统 RAW 管线** | 无 LibRaw 运行时/许可证负担；系统支持 NEF/CR2/CR3/ARW/RAF/ORF/RW2/PEF/DNG/SRW 等 |
| 缩略图 | **自研内嵌 JPEG 抽取器**（TIFF IFD / RAF 头 / CR3 BMFF） | 比系统 RAW 解码快 1~2 个数量级；缩略图只求快 |
| 大图预览 | 2048px（ImageIO，方向已校正） | Retina 窗口下明显优于 1024；解码仅 ~60ms |
| 100% 原图 | ImageIO 输出原始最大边长；优先相机内嵌全尺寸 JPEG | 画质优先，且避免部分机型 demosaic 花屏 |
| 状态索引 | SQLite3（WAL），按文件夹路径哈希分库 | 原子、可重建、不污染照片目录 |
| 缓存索引 | SQLite3（WAL）+ 文件 LRU | 5GB 上限；可清空；崩溃后复用 |
| 并发 | GCD 串行操作队列 + 自定义优先级解码调度器 | 标记严格 FIFO；解码可抢占/去重 |
| 打包 | 手工 `.app` + `.dmg` + ad-hoc 签名 | 不依赖 Xcode；无需 UAC/管理员 |

### 为什么不使用 Avalonia/.NET（Windows 方案）
- 需要随包分发 .NET 运行时；启动与体积都不占优；
- WPF/Win32 专有调用（回收站、文件关联、单实例）在 macOS 都要重写；
- 系统 ImageIO 已覆盖几乎全部目标格式，LibRaw 在 macOS 上不是必需项。

---

## 3. 模块划分

```
AppDelegate / main.swift
  └─ MainWindowController（窗口、顶栏、快捷键、拖放、右键菜单）
       ├─ ImageCanvasView（大图：适应/100%/滚轮缩放/拖拽/双击适应）
       ├─ ThumbnailStripView（横向 NSCollectionView，虚拟化）
       ├─ InfoSidebarView（元数据 + 计数 + 进度 + 队列）
       └─ SettingsWindowController（复制/移动、并发、缓存、会话）
ViewerSession（会话编排：打开、排序、选择、标记、撤销、预热、回调）
  ├─ Scanner（第一层枚举 / 白名单 / 自然排序）
  ├─ StateStore（每个文件夹一个 SQLite）
  ├─ MarkEngine（复制/移动/取消/回收站/撤销；单写队列）
  ├─ DecodeScheduler（优先级队列、去重、并发、取消）
  │    ├─ ImageDecoder（ImageIO）
  │    ├─ FastThumbnail（RAW 内嵌 JPEG 抽取）
  │    └─ ThumbnailCache（内存 NSCache + 磁盘 JPEG + LRU）
  └─ MetadataReader（EXIF/TIFF）
CLI：SelfTest / Bench / FormatMatrix / UITest
```

依赖方向严格单向：UI → Session → Engine/Pipeline → 平台服务；核心引擎（扫描、标记、索引、解码）不依赖 UI，可在 CLI 无界面运行。

---

## 4. 打开与扫描

1. 入口：`openFolderPanel`、拖放、`application(_:open:)`（Finder 打开方式）。
2. 若传入的是文件：取父目录；若父目录名为「筛选」，取上一级，并按文件名选中。
3. `Scanner.scan` 在后台队列：
   - `FileManager.contentsOfDirectory`（第一层，`skipsHiddenFiles`）；
   - 只保留白名单图片扩展名；排除 `._*`、`~$*`、`.tmp/.part/.db*`、目录、符号链接；
   - 读取 `fileSize/contentModificationDate/isUbiquitousItem/ubiquitousItemDownloadingStatus`；
   - 记录 iCloud 未下载数量，界面提示；
   - 默认按文件名自然排序（`localizedStandardCompare`，`IMG_2 < IMG_10`）。
4. 排序键：文件名 / 拍摄时间 / 修改时间 / 大小；升降序；拍摄时间按需后台补全（RAW 从内嵌 JPEG EXIF 读，快路径）。

---

## 5. 缩略图快速通道（性能核心）

### 5.1 TIFF 容器（NEF/CR2/ARW/DNG/ORF/RW2/PEF/SRW/ERF/MEF/MOS/MRW/KCR/DCR/IIQ/3FR）
- 直接解析 TIFF 头（II/MM、magic 42、IFD0 偏移）；
- 递归遍历 IFD0、SubIFDs、next IFD（IFD1）；
- 识别：
  - `0x0201 JPEGInterchangeFormat` + `0x0202 length`；
  - `0x0103 Compression == 7` 的 strip（`0x0111`/`0x0117`）；
- 只读取文件头与候选 JPEG 字节，不做 RAW 解码；
- 候选按字节数升序，取小的内嵌预览（缩略图不需要大图）；
- 用 ImageIO 解码该 JPEG（`WithTransform` 应用方向），目标 160px。

实测 NEF 约 **0.5ms/张**。

### 5.2 富士 RAF
读取文件头 `FUJIFILMCCD-RAW`，按固定头字段（偏移 84/88 或 92/96）取 JPEG 预览偏移/长度。

### 5.3 佳能 CR3
ISO BMFF；读取前 12MB，扫描 JPEG SOI/EOI，取最小有效预览（约 9ms）。

### 5.4 兜底
- 上述失败 → ImageIO `CGImageSourceCreateThumbnailAtIndex`：
  - 优先内嵌缩略图；
  - 无内嵌时退回“解码整图再缩放”（AVIF/PNG/TIFF/EXR/JXL 等）；
- 仍失败 → 缩略图显示“无法预览”（如 X3F），不阻塞其他项。

### 5.5 缓存
- 内存：`NSCache`，总成本上限 512MB（预览+缩略图共用，按字节计费）。
- 磁盘：`~/Library/Application Support/影·筛/cache/thumbs/<sha2 前2位>/<hash>.jpg`；
  - key = SHA256(`v2|kind|完整文件路径|文件大小|修改时间毫秒`)；
  - 160px 缩略图与 2048px 预览分别缓存；
  - SQLite 记录（key/kind/path/bytes/created/accessed）；
  - 上限默认 5GB，LRU 清理到 90%；改上限/手动清空在设置中。
- 全尺寸 100% 图不落磁盘缓存，内存持有当前一张。

---

## 6. 解码调度

- 自定义优先级：`current(0) > visible(1) > prefetch(2) > idle(3)`；
- 同一 `key`（文件+档位）只允许一个任务；重复请求只提升优先级；
- 并发默认 4（Apple Silicon），设置可改 2/4/6/8；
- 复制/移动队列运行期间，解码并发降到 1（`setCopyActive`）；
- 任务可取消：切换文件夹 `cancelAll()` 递增 generation，旧结果丢弃；
- 磁盘/内存命中不占解码并发槽；
- 预热：打开后先生成前 40 张，1.2s 后把剩余全部以 idle 优先级排队；缩略图进度显示在信息栏；
- 选择变化时预取前后各 10 张 2048 预览（prefetch 优先级）。

---

## 7. 状态索引与一致性

每个文件夹一个数据库：`state/<sha256(标准化路径)[:24]>.sqlite3`，WAL。

```sql
CREATE TABLE records (
  path TEXT PRIMARY KEY,       -- 源文件绝对路径
  source_size INTEGER,
  source_mtime REAL,
  copy_path TEXT,              -- 「筛选」里的主副本
  copy_size INTEGER,
  copy_mtime REAL,
  mode TEXT,                   -- copy / move
  marked_at REAL,
  companions TEXT              -- JSON: [{sourcePath, copyPath}]
);
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
```

启动/切换文件夹/刷新时：
1. 读取 records；
2. 副本不存在 → 删除记录，取消勾选；
3. 源文件在扫描结果中 → 恢复勾选；
4. 源文件不存在但副本存在（移动模式/外部删除）→ 生成“回收条目”加入列表，显示“源文件已不在原位”，副本不删；
5. 数据库缺失/损坏但有「筛选」目录 → 按文件名小写匹配重建记录；
6. 清理异常退出的 `.part`。

标记成功立即写 DB；取消立即删 DB。所有 DB 读写都在标记串行队列或会话后台队列，UI 线程零 IO。

---

## 8. 标记事务

### 8.1 入队
- UI 双击/X/回车 → `ViewerSession.toggleMark`：
  - 未标记 → 状态 `.queued`，立即入 MarkEngine 串行队列；
  - 已标记 → 状态 `.processing`，入队取消操作；
  - 排队/处理中 → 取消该 token。
- 队列严格 FIFO（串行 DispatchQueue），连点顺序 = 落盘顺序。

### 8.2 复制（默认）
1. 校验源存在、可读；
2. 确保 `筛选/` 存在（首次标记才创建）；
3. 计算同组目标名（主文件 + 伴生文件），统一冲突后缀 `(1)`、`(2)`…，绝不覆盖；
4. 检查目标卷剩余空间（`volumeAvailableCapacityForImportantUsage`），不足直接失败；
5. 逐个文件：复制到 `目标.part` → 原子 rename 到目标；
6. 失败：删除本次已创建的副本与 `.part`，回滚状态，弹出可读错误；
7. 成功：写索引记录，回调 UI 变绿勾、计数。

### 8.3 移动
同卷 `FileManager.moveItem` 瞬时完成（「筛选」是源目录子目录，天然同卷）。失败回滚已移动文件。

### 8.4 伴生文件
同主文件名（大小写不敏感）的：`.xmp`、`.aae`、`.thm`、`.jpg/.jpeg`、`.heic/.heif/.hif`。主文件本身除外。

### 8.5 取消
- 复制模式：对每个副本调用 `FileManager.trashItem`（废纸篓，可恢复），记录实际 trash URL；任一失败则把已回收的移回并报错；
- 移动模式：把副本移回源路径；同名冲突自动 `(1)` 改名；失败回滚；
- 每条记录按自己的 `mode` 执行，不随当前开关改变。

### 8.6 撤销（⌘Z，栈上限 2000）
| 原操作 | 撤销动作 |
|---|---|
| 复制标记 | 把副本/伴生副本丢进废纸篓，删除记录 |
| 移动标记 | 把副本移回源路径，删除记录 |
| 复制取消 | 从废纸篓移回 `筛选`，恢复记录 |
| 移动取消 | 把文件移回 `筛选`，恢复记录 |

撤销与标记共用同一串行队列，避免竞态。空栈时菜单/按钮禁用。

---

## 9. 界面结构

```
┌──────────────────────────────────────────────────────────────┐
│ 顶栏：打开文件夹 | 文件夹名 | 排序 | 升降序 | 复制/移动 | 撤销 | 已筛 n/共 m | 设置 │
├───────────────────────────────────────────────┬──────────────┤
│                                               │ 照片信息      │
│                大图（适应/100%/缩放/平移）      │ 文件名/状态   │
│                                               │ 机型/镜头/…   │
│                                               │              │
├───────────────────────────────────────────────┤ 已筛/待筛    │
│ 横向缩略图条（虚拟化，160px，绿勾+降饱和）       │ 进度/队列     │
└───────────────────────────────────────────────┴──────────────┘
```

- 大图：深色背景；滚轮/捏合以光标为中心缩放；拖动超过 3px 才进入平移；双击恢复适应窗口；显式裁剪到视图 bounds（避免 100% 溢出覆盖顶栏）。
- 缩略图条：`NSCollectionViewFlowLayout` 横向；只实例化可见项；单击选中并显示，双击标记/取消；已标记项降饱和 + 绿色对勾；条内容宽度按数量计算，支持滚动到选中项。
- 信息栏：`NSVisualEffectView(.sidebar)`；缺字段显示 “—”。
- 状态区：右下角显示已筛/待筛、缩略图进度、复制队列与“取消复制队列”按钮。
- 布局约束：窗口最小 1000×680；顶栏 52pt；缩略图条 190pt；右侧栏 300pt。

---

## 10. 应用生命周期

- `applicationShouldTerminateAfterLastWindowClosed = true`；`windowWillClose` 再显式 `NSApp.terminate` —— **红色按钮直接退出**。
- 退出前：取消复制/解码队列，尽力清理 `.part`。
- 单实例：macOS LaunchServices 默认保证；Finder「打开方式」通过 `application(_:open:)` 复用已运行实例。
- 文件关联：Info.plist `CFBundleDocumentTypes`（Viewer / Alternate，扩展名列表覆盖所有支持格式），首次运行后出现在“打开方式”里。
- 设置：复制/移动、解码并发、缓存上限、记住上次文件夹与排序（默认开）、启动恢复会话（默认关）。
- 沙盒：不启用（需要访问用户任意照片目录；不上架 App Store）。

---

## 11. 命令行入口

| 命令 | 用途 |
|---|---|
| `--selftest` | 引擎自检（无 GUI）：扫描、自然排序、复制、冲突、伴生、取消/回收站、移动、撤销、索引重建、10 连点、取消队列 |
| `--bench <文件夹> [--limit N] [--csv 路径]` | 枚举/首屏/缩略图 P50/P95/预览/全解/缓存/内存，写 CSV |
| `--matrix <文件夹> [--csv 路径]` | 逐文件格式矩阵（可打开/类型/像素/方向/耗时/元数据） |
| `--thumbprobe <文件夹>` | 快速缩略图命中率与耗时对比（开发用） |
| `--uitest <文件夹>` | 真实窗口自动化验收（15 种格式打开、标记/取消/撤销/移动/排序/设置） |
| `--version` / `--help` | 版本/帮助 |

所有 CLI 无界面即可运行，便于远程/CI 复跑。

---

## 12. 兼容性与打包

- 编译目标 `arm64-apple-macos12.0`（SwiftPM `platforms: .macOS(.v12)`），用 macOS 26.5 SDK 构建，在 macOS 27.2.1 实测；
- 仓库内无第三方二进制；系统框架 + Swift 运行时；
- `build.sh`：`swift build -c release` → 组装 `影·筛.app` → 拷图标/文档 → `codesign -s -`（ad-hoc）；
- `.dmg` 使用 `hdiutil` 生成，内含应用与“应用程序”快捷方式；
- 未做开发者 ID 签名与公证：首次打开需 `xattr -cr` 或右键打开；正式分发时替换签名即可；
- 图标来自 `YSLGOG.png`（Ho Wing Own），通过 CoreGraphics 裁成 macOS 圆角并生成 iconset。

---

## 13. 自动更新

- `UpdateChecker` 读取 `https://github.com/Jadis038/PhotoCull/releases.atom`（Atom 无匿名 API 限额），只接受 tag 前缀 `macos-` 的 Release；Windows 的 `v1.0.5` 等标签不会命中。
- Atom 不带资产 URL，按固定命名构造 `https://github.com/<repo>/releases/download/<tag>/PhotoCull-<版本>-macOS-arm64.dmg`。
- 直连失败自动使用 `https://gh-proxy.com/` 前缀的镜像重试（下载同样支持回退）。
- macOS Release 以正式版发布；Windows 端 `/releases/latest` 仍返回 `v1.0.5`（macOS tag 不参与版本解析），
  且 Windows 更新器在 API 无 MSI 时会回退根目录 `latest.json`（v1.0.5 不变），因此不影响 Windows 更新。
- 版本比较按语义化数字（tag `macos-v1.0.2` → 1.0.2）；只有高于当前 `AppInfo.version` 才提示。
- 用户确认后下载 DMG 到临时目录 → `hdiutil attach` → `ditto` 出 `.app` → 校验新版本 → 用 `/bin/sh` 脚本在应用退出后替换自身并 `open` 重启；失败时恢复旧 `.app`。
- 设置项“启动时检查 GitHub 更新”默认开启；`--update-check` 无界面检查，`--update-auto` 用于自动化更新测试。
- 应用退出前会把更新窗口和下载任务取消；更新脚本独立于应用进程运行。

## 14. 已知限制

1. **X3F**（Sigma dp / Polaroid x530）：macOS ImageIO 无 Foveon 解码，无法预览；应用会显示“无法打开这个文件（X3F）”，不会崩溃。
2. **Mamiya ZD .MEF**：系统只暴露 144×192 内嵌小图，无法全尺寸。
3. 部分手机 DNG 没有内嵌 JPEG 预览：缩略图走 ImageIO 解码整图，单张 20~70ms（仍可接受）。
4. 未签名/未公证：首次启动有 Gatekeeper 提示。
5. 不提供自动更新（本版本按用户要求未上传 GitHub）。
6. 拍摄时间排序需要为全部文件读取 EXIF；超大目录首次切换会有数秒后台等待（有状态提示）。
