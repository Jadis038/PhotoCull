# 影·筛（YingShai）设计规格 SPEC

> 版本 1.0.0 ｜ 2026-11 ｜ 本文件是实现的唯一权威说明；代码注释与提交信息均为中文。

## 1. 目标与非目标

**目标**：在 Windows 10 21H2+/11 x64 上，用极低的交互成本筛掉一次外拍/一段素材里的废片。
一次打开 2000 张 45MP RAW（约 100GB），1 秒内看到列表，2 秒内出首屏缩略图，切图无感，
标记即时且绝不丢数据。中文界面，单文件 MSI，免管理员。

**非目标**（明确不做）：修图调色、导出转换、批量重命名、星级评分、人脸识别、云盘上传、
多文件夹并行打开、删除原文件。

**已定决策**（不再询问）：
1. 只做 x64；2. 主代码 MIT，RAW 用 LibRaw（LGPL-2.1/CDDL-1.0，动态链接）；3. 标记默认复制；
4. 取消标记只删「筛选」里的副本（回收站），原目录文件永不删除。

## 2. 进程与线程模型

单进程。UI 线程只做 WPF；所有 IO/解码/复制都在后台线程：

| 线程/组件 | 数量 | 职责 |
|---|---|---|
| UI 线程 | 1 | 渲染、输入、ViewModel 更新（通过 `SynchronizationContext` 回投） |
| 解码线程池 | 4（SSD）/2（机械/网络）/1（复制进行中） | 优先级队列取任务，解码缩略图/预览 |
| 全解线程 | 1（`SemaphoreSlim(1)`） | 100% 查看时的完整 demosaic |
| 单写线程 | 1 | 复制/移动/删除，严格串行，每个任务独立成败 |
| 单实例管道线程 | 1 | 命名管道接收第二个进程传来的命令行 |
| 缩略图预取 | 复用解码池 | 可见区 > 前后预取 > 全量预热 |

**硬规则**：UI 线程不做任何文件枚举、不 `File.ReadAllBytes`、不做解码；引擎不引用任何 `Window`/控件。

## 3. 目录结构

```
yingshai/
├─ src/YingShai.Core/            引擎（无 UI 依赖）
│  ├─ AppPaths.cs                路径约定（%LOCALAPPDATA%\影·筛，支持 YING_SHAI_DATA 覆盖）
│  ├─ Model/                     ImageItem / MarkRecord / PhotoMetadata
│  ├─ Scanning/                  DirectoryScanner（FindFirstFileExW）、NaturalComparer
│  ├─ Imaging/                   LibRawInterop、LibRawImage、WicImage、ExifReader、ImageDecoder
│  ├─ Cache/                     ThumbnailCache（内存 LRU + 磁盘 + SQLite 索引 + LRU 清理）
│  ├─ Jobs/                      DecodeScheduler（优先级/去重/并发/重排）
│  ├─ Ops/                       FileOps（CopyFileEx/SHFileOperation）、OperationQueue、MarkService
│  ├─ Index/                     StateStore（SQLite WAL：marks / cache / folders）
│  ├─ Session/                   ViewerSession（一个文件夹一个会话）
│  └─ Cli/                       CliHost、SelfTest、Bench、Matrix
├─ src/YingShai.App/             WPF 界面（Program / Ui/AppController / MainWindow / MainViewModel / SingleInstance）
├─ src/YingShai.Cli/             开发期命令行壳（发布版由「影·筛.exe」直接提供）
├─ installer/                    WiX v4 打包（Product.wxs、make-files-wxs.ps1、build.ps1、verify.ps1）
├─ native/libraw/                raw_r.dll + jpeg8/lcms2/zlib1 + 许可证全文
├─ docs/                         SPEC、测试矩阵、基准、spike 报告、使用说明
├─ tests/                        验收脚本（跑自检/基准/矩阵/UI 自检）
└─ spikes/                       4 个 spike 的原始工程（LibRawProbe、WixHello、VirtList）
```

## 4. 扫描与列表

- 用 `FindFirstFileExW(FindExInfoBasic, FindExSearchNameMatch)` 单次枚举，**零解码**；
  只为每个文件取名字/大小/时间/属性，不为每个文件单独 `stat`。
- 只列第一层；排除子目录、隐藏/系统、`~$*`、`.tmp`、`._*`，以及
  `FILE_ATTRIBUTE_OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS` 的云盘占位文件（跳过并在状态区提示）。
- 扩展名白名单（小写）：jpg jpeg jpe png tif tiff webp bmp heic heif avif jxl +
  nef nrw cr2 cr3 crw arw srf sr2 raf orf rw2 pef dng srw raw rwl 3fr fff dcr kdc mef mos mrw erf x3f iiq cap pxn r3d ari braw。
- 排序：文件名自然排序（数字段按数值比较，`IMG_2 < IMG_10`）默认；可切拍摄时间/修改时间/大小，
  可升降序。拍摄时间排序前用「只 open 不解码」的后台扫描补时间（2000 张约 2~4 秒）。
- 列表在后台线程构建，构建完成后一次性投递到 UI 线程（`ItemsReset`）。

## 5. 解码与缓存（性能的全部秘密）

1. **每个文件只打开一次**：`libraw_open_wfile` → 同一次会话里读 `iparams/imgother/lensinfo` 元数据 +
   `unpack_thumb` + `dcraw_make_mem_thumb` 拿内嵌 JPEG（或未压缩 RGB），**不做 demosaic**。
2. 内嵌 JPEG 用 WIC 按 `DecodePixelWidth = 目标尺寸` 解码（DCT 缩放），不整张载入再缩。
3. 解码结果复制成独立 `BitmapSource`（`WicImage.CopyPixels`，96 DPI、冻结、可跨线程）——
   **必须复制**，否则 `BitmapImage` 会一直引用源 `MemoryStream`，2000 张能把内存顶到 3.7GB。
4. 三级缓存：
   - 内存 LRU：键 `hash(path|size|mtime|decoderVersion)` + 尺寸档，预算默认 512MB；
   - 磁盘：`%LOCALAPPDATA%\影·筛\cache\<2位>\<hash>_160.jpg` 与 `_1024.jpg`，索引进 SQLite，
     上限默认 5GB，按 `last_used` LRU 清理（每 30 秒最多检查一次），缓存键含 `decoderVersion`；
   - 源文件解码（最后一级）。
5. 没有内嵌预览的文件（部分手机 DNG）走「半质量 demosaic 兜底」，标记为慢路径并降优先级；
   结果同样进缓存，第二次就快。
6. **缩略图离开当前张 ±200 立即释放内存**（`ImageItem.ClearThumb`），磁盘缓存仍在，滚回来秒出。
7. 100% 查看才做完整解码：后台线程、单并发、`SemaphoreSlim(1)`，期间界面照常操作。
8. 大图预览 1024px、条带缩略图 160px；绝不把 45MP 位图整张塞进 UI。
9. **100% 查看的渲染**：WPF 无法直接渲染"8000px 的元素 + 负向平移"（实测 8191×5463 平移后有效像素 0%），
   也不能放在会给子元素施加布局裁剪的 Grid 里；实现为「Canvas 承载 + 按视口裁剪 CroppedBitmap 显示」，
   平移时重新裁剪（实测 100% 下有效像素 82.6%，平移画面正常变化）。
10. 大图交互遵循 Photoshop 习惯：滚轮上下平移、Ctrl+滚轮左右平移、Alt+滚轮以光标为中心缩放、
    双击在「适应窗口 ↔ 100% 像素 1:1」之间切换。**大图区只在鼠标拖动超过 3px 时才 CaptureMouse**，
    否则捕获不释放会把缩略图条的点击/滚轮整个吞掉（实测踩坑）。

## 6. 任务调度

`DecodeScheduler`：二叉堆优先级队列 + 固定工作线程 + 同键去重 + 动态并发。

- 优先级：`0 当前大图 > 1 可见条带 > 2 前后各 30 张预取 > 3 其余全量预热`；
- 去重：同一 `(文件, 尺寸)` 同时只有一个在跑，重复请求复用同一个 Task；
- 重排：可见性变化时 `Reprioritize` 入队更高优先级副本，旧副本出队时被跳过（惰性失效）；
- 并发：默认 SSD 4 / 机械 2 / 网络 2（设置可改）；复制任务运行时降到 1，避免和写盘抢 IO；
- 可暂停/可取消：队列里的任务可取消，正在跑的一次解码自然完成。

## 7. 标记 / 取消 / 撤销

### 7.1 标记（单张即时）

每次触发 = 一次独立入队（`OperationQueue` 串行执行），任何一张失败只回滚它自己：

1. 校验源存在可读（`FileShare.ReadWrite` 打开探测；只读源文件无需写权限）；
2. 校验目标盘剩余空间（`GetDiskFreeSpaceEx`，需求 = 主文件 + 伴生文件 + 64MB 余量）；
3. 首次标记时才创建 `筛选\`；
4. `CopyFileEx` 复制到 `原名.ext.part`（带进度回调、可取消）→ `MoveFile` 原子改名；
   同名冲突自动 `原名 (1).ext`，最多重试；
5. 伴生文件：同主名的 `.xmp/.aae/.thm/.dop/.pp3/.pp2`，以及 RAW↔JPG/HEIF 的另一半，逐个同样处理；
6. 写索引（源/副本完整路径、操作类型、副本大小与修改时间、伴生映射、时间戳）；
7. UI：入队即「…」角标，完成变「✓」并降饱和，更新「已筛 n / 共 m」；标记后**停留在当前图**（2026-11 用户反馈后取消自动前进，用 → 或单击继续）。

失败/取消：删除本次新建的所有副本与 `.part`（移动模式则移回原位），角标回滚，弹可读错误
（占用/权限/空间/网络盘断开分别有中文提示）。

### 7.2 取消标记

- 复制模式：把副本与伴生副本 `SHFileOperationW + FOF_ALLOWUNDO` 丢进回收站（可恢复），**原目录文件不动**；
- 移动模式：把副本移回原位（同卷 `MoveFileEx` 秒回；跨卷复制+回收站），同名冲突改名不覆盖；
- 每条 `MarkRecord` 记录自己的 `Kind`；**切换设置不影响旧记录的撤销方式**；
- 若该图还有排队/进行中的标记任务，先取消它（正在复制的会清理 `.part`）。

### 7.3 撤销

`MarkService` 维护撤销栈（上限 2000 步，验收要求连续 300 张可全部撤回）：
标记 ↔ 取消 互为逆操作；撤销动作本身不再入栈。撤销走同一个串行队列。

## 8. 一致性核对

打开文件夹（以及 F5 刷新）时：

1. 读索引记录，逐条核对副本是否存在；
2. 副本不存在 → 在 `筛选` 目录里按文件名重新匹配（用户改名/搬目录）→ 找到就更新记录；
   找不到 → 取消该条勾选；
3. 原文件不存在 → 该条显示「源文件已不存在」，**副本不删**；
4. 该文件夹路径第一次见到且索引为空，但 `筛选` 里已有文件 → 按文件名重建标记（覆盖「整个文件夹被移动」）。

## 9. 设置

`%LOCALAPPDATA%\影·筛\settings.json`：上次文件夹、排序方式/升降序、启动恢复上次会话（默认关）、
复制/移动（默认复制）、磁盘缓存上限（5GB）、内存缓存上限（512MB）、
解码并发（0=自动）、启动预热、窗口尺寸。

## 10. 命令行（无 GUI 可用）

| 命令 | 说明 |
|---|---|
| `影·筛.exe --selftest` | 17 组 / 64 项内置自检（扫描、自然排序、复制、冲突改名、取消删除、索引重建、撤销栈、伴生文件、中文/空格/超长路径、缓存键、300 张批量标记+全部撤销、连点 10 张、队列顺序、复制中取消、只读/占用、外部删除副本、按文件打开选中） |
| `影·筛.exe --bench <文件夹> [--cold] [--nowarm] [--csv x.csv]` | 枚举/首屏/切图/全解/全量预热/命中率/内存，输出表格与 CSV |
| `影·筛.exe --matrix <文件夹> [--full-decode] [--out x.csv]` | 生成测试矩阵 |
| `影·筛.exe --ui-selftest <文件夹> [out.png] [--mark-demo]` | 无桌面环境下创建窗口、打开文件夹、渲染截图（验收 UI） |
| `影·筛.exe --version` / `--help` | 版本/帮助 |

## 11. 打包

WiX v4.0.5，`Scope="perUser"` → LUA 包，免 UAC；安装到 `%LOCALAPPDATA%\Programs\影·筛`；
开始菜单每用户快捷方式；HKCU `Software\Classes\YingShai.Image`（显示名「影·筛 图片」）+
各扩展名 `OpenWithProgids` + `Applications\影·筛.exe`（「打开方式」可见但不抢默认）；
卸载删除程序/快捷方式/注册表，**不删**用户图片、索引、缓存；`/qn` 静默安装、`msiexec /x` 卸载均已实测。
文件清单由 `installer/make-files-wxs.ps1` 扫描发布目录生成，组件 GUID 由相对路径确定性派生（便于升级）。

## 12. 测试策略（无交互桌面）

- 引擎：`--selftest` 全覆盖，退出码即结论；`--bench`/`--matrix` 产出 CSV。
- UI：`--ui-selftest` 创建真实窗口（窗口站在无桌面下仍可用），打开文件夹、等解码、`RenderTargetBitmap`
  出 PNG 并写日志，可用像素/日志判断；不从"手动点一下"取证。
- 打包：`installer/verify.ps1` 装-验-卸 21 项断言。
- 性能：2000 NEF + 2000 JPG（硬链接构造 4000 条）冷缓存跑 `--bench`。

## 13. 已知限制

- X3F（Sigma Foveon）不支持（LibRaw 0.21 打不开样本；WIC 也不支持）；
- HEIC/AVIF 依赖系统编解码扩展，未在样本上实测；
- 拍摄时间排序的首次排序需要后台读一遍时间（无缓存时 2000 张约 2~4 秒）；
- 磁盘缩略图缓存按条目 LRU 清理，极端情况下单批清理可能超过上限才会收敛；
- 无代码签名证书，SmartScreen 会提示未知发布者（README 有说明）。

## 14. 决策记录（最小惊讶原则，未询问用户）

| 决策 | 理由 |
|---|---|
| 标记后停留在当前图，不自动前进 | 2026-11 用户实测 2500 张 Z8 后反馈：双击后预览跳到下一张影响确认；改为停留，→ / 单击手动继续 |
| 单击缩略图即查看 | 同一轮反馈：单击应立即在大图查看；实现上不依赖 SelectedItem 绑定，直接处理 SelectionChanged，并在 PreviewMouseLeftButtonDown 里按命中元素兜底 |
| 双击大图改为「100% ↔ 适应窗口」 | 2026-11 用户要求（原为双击=适应窗口）；100% 以双击点为锚居中 |
| 滚轮改为 Photoshop 手感 | 2026-11 用户要求：Alt 缩放 / Ctrl 左右 / 滚轮上下（原为滚轮缩放） |
| 大图 100% 采用视口裁剪渲染 | WPF 对大元素+负向平移渲染为空，且 Grid 会布局裁剪；改用 Canvas + CroppedBitmap |
| 缩略图条新增「帧针模式」（可切换） | 2026-11 用户要求：剪辑软件式帧针，中线对准哪张就查看哪张；起点/终点无法居中时帧针画到当前图上并暂停判定，滚动即恢复 |
| 空格键改为标记（原为下一张） | 2026-11 用户要求：空格等同双击缩略图，快速确认 |
| 启动时输入法切英文 | 2026-11 用户要求：避免中文输入法打断快捷键；WM_IME_CONTROL 只作用于本窗口，已是英文无副作用 |
| ↑↓ 缩放固定中心 + 无缝换原图 | 修复：换原图补偿方向写反（放大 8 倍且偏移）；缩放百分比统一以原图分辨率为基准 |
| 帧针模式默认开启 | 2026-11 用户要求设为默认（旧配置文件经迁移自动打开） |
| ↑ / ↓ = 固定中心缩放 ±10% | 2026-11 用户要求；超 95% 后台自动换原图细节且视觉不跳 |
| Enter = 放大到 100%（原为全屏） | 2026-11 用户要求；全屏移至 F11 |
| Z = 回到「适配窗口」 | 2026-11 用户要求；Ctrl+Z 仍是撤销 |
| 0 = 清除星标 | 2026-11 用户要求；1~5 标 1~5 星（含小键盘），同键再按清除 |
| S = 保护（锁定） | 锁定后该图不会被取消筛选/删除副本，状态持久化（locks 表） |
| 移动模式跨卷时「复制+回收站删源」而不是硬删 | 可恢复，符合"绝不丢数据" |
| 撤销「取消标记」= 重新复制/移动，而不是从回收站还原 | 不依赖回收站内部结构，跨盘/被清空也成立 |
| 伴生文件按同主名 + 扩展名白名单匹配，一次目录枚举 | 避免多次系统调用导致 2000 张变慢 |
| 拍摄时间排序不做持久化时间索引 | 首次读盘 2~4 秒可接受，避免索引膨胀 |
| `YING_SHAI_DATA` 环境变量可覆盖数据目录 | 便携/自动化测试/沙箱验证需要，普通用户不受影响 |
| 缩略图条用灰阶替代"降饱和" | 视觉达标且零成本，避免 ShaderEffect 软件渲染 |
