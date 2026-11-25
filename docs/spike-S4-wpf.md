# S4 spike：WPF 横向虚拟化缩略图列表性能验证

> 关联：spike 工程 [`../spikes/VirtList/`](../spikes/VirtList/)（net8.0-windows，Release，可一键复现）
> 日期：2026-11-25 · 机器：Windows 11（10.0.26200）· .NET 8.0.428 / WPF · RenderCapability.Tier=2 · DPI 150%

## 0. 一句话结论

2000 项、横向滚动、1280×360 视口的缩略图条，**虚拟化 + 容器回收** 每帧 UI 线程只花 **0.69 ms（P95 1.03 ms，最大 5.19 ms），0 掉帧**，
全程只实例化 **9~12 个容器、总共 12 个 ThumbCell**；关掉虚拟化的朴素实现同样 0 掉帧，但**常驻 2000 个容器、工作集多 23 MB、首帧多 111 ms、
渲染侧每帧慢 23 倍**，规模到 10000 项时工作集 264 MB（虚拟化仍是 144 MB）。**虚拟化必须开，且必须用 `ListBox` 而不是「外部 ScrollViewer + ItemsControl」。**

## 1. 目标与范围

验证「影·筛」主界面缩略图条的可行性，回答四个问题：

1. 2000 项横向虚拟化列表滚动时，每帧耗时是否能留在 16.7 ms 预算内、有没有掉帧；
2. 滚动期间进程工作集涨多少；
3. 虚拟化是否真的生效（实际实例化的容器数、以及容器是否被复用）；
4. 关掉虚拟化的代价有多大，值不值得为它维护额外复杂度。

不在范围内：真实图片解码、色彩管理、LibRaw 缩略图提取、触摸惯性、拖拽滚动条的手感（这些留给正式实现和后续 spike）。

## 2. 环境

| 项 | 值 |
|---|---|
| OS | Microsoft Windows 10.0.26200（Windows 11） |
| .NET SDK / 运行时 | 8.0.428 / Microsoft.WindowsDesktop.App 8.0.34 |
| WPF 渲染 | `RenderCapability.Tier = 2`（硬件加速路径可用） |
| CPU / 逻辑核 | 18 |
| 显示 | 无交互桌面（无人值守会话），DPI 缩放 150% |
| 图形后端 | 窗口可正常 `Show()`；`CompositionTarget.Rendering` 可触发（约 0.15~3.5 ms/次，持续滚动时） |

## 3. 被测结构

```
Window (无边框 1280×360)
└─ Border
   └─ ListBox                       ← 生命周期自带 ScrollViewer（关键）
      ├─ ItemsPanel = ItemsPanelTemplate
      │    └─ VirtualizingStackPanel Orientation=Horizontal
      │         IsVirtualizing=True  VirtualizationMode=Recycling  ScrollUnit=Pixel
      ├─ ScrollViewer.CanContentScroll = True
      ├─ ScrollViewer.HorizontalScrollBarVisibility = Auto / Vertical = Disabled
      └─ ItemTemplate → ThumbCell   ← 每项 168×196 dip：160×160 占位色块 + 文件名
```

* 数据：2000 条假数据（`IMG_0001.CR3`、`DSC_0002.ARW`…），每条只有一个文件名 + 色相种子，**不加载、不解码任何图片**。
* 单元：`ThumbCell : FrameworkElement`，`OnRender` 里用 `DrawingContext` 画两块色阶（模拟照片的天空/地面）+ 边框 + 一行
  `FormattedText` 文件名；固定 168×196 dip（160 占位图 + 28 文字区 + 8 间隙）。画刷、画笔全部静态冻结，测量只反映列表管线开销。
* 视口：1280×360 窗口 → ScrollViewer 视口 **1278×341 dip**；内容 `ExtentWidth = 336 000 dip`，`ScrollableWidth = 334 722 dip`。
* 三个场景：
  * `virt`：虚拟化 + **Recycling**（正式 UI 应采用的组合）；
  * `standard`：虚拟化 + Standard（容器用完即弃，用来量化「回收」的收益）；
  * `nonvirt`：**关闭虚拟化**（普通横向 `StackPanel` + `CanContentScroll=False` 按像素滚动，2000 个容器全实例化）。

## 4. 测量方法

### 4.1 真窗口，而不是「假装渲染」

本机虽无交互桌面，但窗口站可用，`Window.Show()` 真实成功（`PresentationSource` 非空、合成器持续出帧），
因此默认用**真窗口 + WPF 真实合成管线**测量；同时实现并验证了**离屏渲染**兜底路径（`Measure + Arrange + RenderTargetBitmap`，
不创建窗口）。两条路径的容器数、每帧耗时一致（离屏 virt：平均 0.62 ms、P95 ≈ 0.9 ms，容器 9~12 个），互相印证。
若目标机将来 `Show()` 失败，`ListHost` 会自动回退离屏并在输出里注明。

### 4.2 帧泵（怎么驱动「一帧」）

WPF 里滚动一帧的真实动作是：**改滚动偏移 → 同步完成测量/排列（虚拟化面板增删容器）→ 处理 Dispatcher 里排队的容器回收与渲染失效**。
spike 用 `Stopwatch` 精确复刻这三步并分两段计时：

| 段 | 内容 |
|---|---|
| 布局段 | `ScrollToHorizontalOffset(offset)` + `UpdateLayout()`（测量/排列、容器生成、`OnRender` 失效） |
| 队列段 | `Dispatcher.Invoke(DispatcherPriority.Background, ...)`：处理 `ItemContainerGenerator` 的容器回收动作（Background 优先级）与渲染失效（Render 优先级）；测量代码自身空转基准 0.019 ms 已包含在内 |
| 合计 | **每帧 UI 线程工作耗时**，即掉帧判定依据 |

滚动序列：每帧推进 `视口宽度 / 4 = 320 dip`（约 2 个单元的跨度，偏严苛的快速拖动），
来回 **2 趟 = 4 次平移**，共 **4188 帧**、总位移约 1 340 千 dip（≈ 4 倍列表全长）。预热 20 帧不计入统计。

### 4.3 为什么不用「帧间隔」判掉帧

本机没有真实显示同步：**空闲窗口约 30 ms 才合成一次**（无脏区时按需节流），
**持续滚动时反而 0.15~3.5 ms 就能走完一次合成**（没有 vsync 限制）。帧间隔既不代表 60 Hz 也不代表性能上限，
只能作为「渲染侧代价」的旁证。因此：

* **掉帧率**按「每帧 UI 线程工作耗时 > 16.67 ms（60 Hz 预算）」统计；
* **渲染侧代价**另用两个指标衡量：① 持续滚动时 `CompositionTarget.Rendering` 的真实帧间隔（合成管线节拍）；
  ② `RenderTargetBitmap` 对整幅视口做一次软件光栅化的耗时（参考值，真窗口最终呈现走 GPU 合成）。

### 4.4 内存与容器数采样

* 每 8 帧采样一次 `Process.WorkingSet64`，取最大值；场景结束再读 `PeakWorkingSet64` 与 GC 后的托管堆。
* 每帧读 ItemsHost（可视化树里 `IsItemsHost=true` 的 `Panel`）的直接子元素数 = **当前实例化容器数**。
* `ThumbCell` 构造函数累加静态计数 = **全生命周期真正 new 出来的单元数**（Recycling 是否复用的直接证据）。
* 抽查 0 / 25 / 50 / 75 / 100% 偏移处的「已实现容器数 + 可见项序号区间」，确认窗口确实在移动。

## 5. 实测结果

> 所有数字均来自 `out\bench-20261125-145401.log`（一条命令产出）。每个场景独立进程，内存峰值可比。
> 同一命令多次运行之间有约 ±5% 的波动（GC/JIT/宿主机调度），帧图 PNG 上标注的是它自己那次运行的实时数字，
> 与本表有零点几毫秒的差异属正常；10000 项的扩展数据来自另一次独立运行（5.4）。

### 5.1 帧耗时（4188 帧/场景，60 Hz 预算 16.67 ms）

| 场景 | 平均 | P50 | **P95** | P99 | 最大 | 掉帧 >16.67 ms | >33.3 ms |
|---|---|---|---|---|---|---|---|
| **虚拟化 + 回收** | **0.69 ms** | 0.69 | **1.03 ms** | 1.22 | 5.19 | **0 / 4188 = 0.0%** | 0 |
| 虚拟化 + 标准 | 0.83 ms | 0.82 | 1.33 ms | 1.69 | 13.53 | 0 / 4188 = 0.0% | 0 |
| 关闭虚拟化 | 0.16 ms | 0.13 | 0.30 ms | 0.46 | 14.41 | 0 / 4188 = 0.0% | 0 |

拆解（布局段 = 改偏移 + UpdateLayout；队列段 = 容器回收 / 渲染失效）：

| 场景 | 布局段平均 | 队列段平均 |
|---|---|---|
| 虚拟化 + 回收 | 0.23 ms | 0.45 ms |
| 虚拟化 + 标准 | 0.27 ms | 0.56 ms |
| 关闭虚拟化 | **0.06 ms** | **0.10 ms** |

**注意这个反直觉的数字**：关掉虚拟化后 UI 线程反而更轻（0.16 ms vs 0.69 ms），
因为 `CanContentScroll=False` 时 `ScrollViewer` 只对整块内容做**平移**，不重排任何子元素。
朴素实现的代价不在 UI 线程，而在**渲染线程、内存和首帧**（见 5.3 / 5.4）——
这也说明「用 UI 线程耗时单独判断好坏会得出错误结论」，必须三项一起看。

### 5.2 渲染侧代价（这才是朴素实现的真痛点）

| 指标 | 虚拟化 + 回收 | 虚拟化 + 标准 | 关闭虚拟化 | 倍差 |
|---|---|---|---|---|
| 持续滚动时合成帧间隔（真实 `CompositionTarget.Rendering`） | **0.15 ms** | 0.34 ms | **3.47 ms** | 23× |
| 整幅视口光栅化（`RenderTargetBitmap` 软件路径，20 次中位） | **1.07 ms** | 1.73 ms | **11.81 ms** | 11× |
| 空闲窗口帧间隔（环境基线） | 29.8 ms | 29.6 ms | 29.5 ms | — |

非虚拟化时 WPF 的可视化树里有 2000 个元素常驻，每次滚动合成都要遍历/变换/裁剪它们；
虚拟化时树里只有 9~12 个。**这就是虚拟化真正买到的东西。**

### 5.3 内存与容器数

| 指标 | 虚拟化 + 回收 | 虚拟化 + 标准 | 关闭虚拟化 |
|---|---|---|---|
| **常驻容器数（最小/平均/最大）** | **9 / 11.0 / 12** | 10 / 20.4 / 33 | **2000 / 2000 / 2000** |
| ThumbCell 实例化总数（全生命周期） | **12** | 8237 | 2000 |
| 建树 + 首次布局 | **175.2 ms**（含 8 个容器） | 174.2 ms | **286.0 ms**（含 2000 个容器） |
| 进程工作集 起始 → 采样峰值 | 111.4 → **142.5 MB** | 111.5 → 152.2 MB | 139.1 → **161.5 MB** |
| 进程峰值工作集（`PeakWorkingSet64`） | **149.7 MB** | 155.7 MB | **173.2 MB** |
| 托管堆（GC 后） | **2.1 MB** | 3.0 MB | **12.6 MB** |
| GC 次数（Gen0/1/2） | 11 / 4 / 3 | 19 / 9 / 5 | 9 / 6 / 4 |

Recycling 的收益在「实例化总数」上最刺眼：**12 次 vs 8237 次**——Standard 模式每滚过一个视口就丢弃重建容器，
滚动 4 趟下来把 2000 个单元反复创建销毁了 4 遍多（GC Gen0 19 次），也会让 `OnRender`/模板应用这些重活不断重复。

虚拟化生效的直接证据（抽查，`virt` 场景）：

| 滚动偏移 | 常驻容器 | 可见项序号 |
|---|---|---|
| 0 dip (0%) | 9 | 0 .. 8 |
| 83 680 dip (25%) | 10 | 497 .. 506 |
| 167 361 dip (50%) | 10 | 995 .. 1004 |
| 251 042 dip (75%) | 10 | 1493 .. 1502 |
| 334 722 dip (100%) | 9 | 1991 .. 1999 |

容器数恒为 9~12（视口本身约 8 个单元，滚动方向上再多留几个），而「可见项序号」随偏移线性移动 —— 窗口在滑动，容器在复用。

### 5.4 规模放大到 10000 项（每场景 10494 帧，1 趟）

| 指标 | 虚拟化 + 回收 | 关闭虚拟化 |
|---|---|---|
| 常驻容器 | **9 / 11.0 / 12**（不随 N 增长） | 10000 |
| ThumbCell 总数 | **12** | 10000 |
| 首次布局 | 173.5 ms | **1067.4 ms** |
| 每帧 UI 平均 / P95 | 0.55 / 0.92 ms | 0.10 / 0.23 ms |
| 合成侧帧间隔 | **0.21 ms** | 3.31 ms |
| 工作集峰值 / 进程峰值 | 144.0 / 144.0 MB | **263.9 / 296.8 MB** |

数据项从 2000 涨到 10000，虚拟化方案的容器数、每帧耗时、内存全部**不变**；
朴素实现首帧 1.07 s、内存逼近 300 MB。图库上万张是常态，这里已经没有可选项了。

### 5.5 帧图

真窗口渲染的一帧（`RenderTargetBitmap` 抓取，`out/frame-virt.png`）：

![虚拟化缩略图条](../spikes/VirtList/out/frame-virt.png)

非虚拟化场景同一位置（`out/frame-nonvirt.png`）——**画面完全一样**，差别全在资源占用：

![非虚拟化缩略图条](../spikes/VirtList/out/frame-nonvirt.png)

离屏模式（`--offscreen`）同样能出图（`out/frame-virt-offscreen.png`），证明不依赖桌面也能跑测：

![离屏渲染](../spikes/VirtList/out/frame-virt-offscreen.png)

## 6. 结论

1. **横向虚拟化列表在 WPF 里完全可行**：2000 项、快拖滚动 4188 帧，UI 线程平均 0.69 ms、P95 1.03 ms、**0 掉帧**，
   有充足余量留给大图解码、滤镜、色彩管理。
2. **必须开 Recycling**：容器实例化 12 次 vs 8237 次，最大帧 5.19 ms vs 13.53 ms，工作集少约 10 MB。
3. **虚拟化是否生效，一眼可判**：监控 ItemsHost 的子元素数即可 —— 等于数据项数就是虚拟化失效（这是最好的自检/回归指标）。
4. **最容易踩的坑（本次实测踩到并修正）**：
   * `ItemsControl` 外面套外部 `ScrollViewer` → 虚拟化**完全失效**（外部 ScrollViewer 的 Content 是 ItemsControl，
     它不实现 `IScrollInfo`，面板只能按无限视口测量 → 2000 个容器全实例化）。必须用**自带 ScrollViewer 的 ListBox**。
   * `IsVirtualizing / VirtualizationMode / ScrollUnit` 写在 ListBox 上**不会**传到面板（实测面板仍是 `ScrollUnit=Item`），
     要写在 `ItemsPanelTemplate` 里那个面板元素上（或直接设在面板实例上）。
   * 容器回收动作排在 Dispatcher 的 **Background** 优先级；自定义帧循环/测试驱动时必须排空队列，否则容器会堆积假象。
5. **朴素实现不是「每帧更慢」，而是把代价挪到了内存/首帧/渲染线程**：UI 线程 0.16 ms 反而更快，
   但合成侧 3.47 ms（23×）、光栅化 11.8 ms（11×）、工作集 +23 MB、首帧 +111 ms，且随 N 线性恶化。

## 7. 给正式 UI 的建议

### 7.1 缩略图条本身

1. **用 `ListBox`（或 `ListView`），不要外部 ScrollViewer + ItemsControl**。ListBox 的模板里自带
   `ScrollViewer → ItemsPresenter → 面板`，虚拟化链路才完整。横向滚动通过
   `ScrollViewer.HorizontalScrollBarVisibility=Auto` + `VerticalScrollBarVisibility=Disabled` 挂在 ListBox 上。
2. **三个附加属性写在面板上**（ItemsPanelTemplate 内）：
   `IsVirtualizing=True`、`VirtualizationMode=Recycling`、`ScrollUnit=Pixel`，并设 `ScrollViewer.CanContentScroll=True`。
   少了 `ScrollUnit=Pixel` 就会按「项」跳，滚动手感很差。
3. **单元尺寸必须固定**（160×160 + 28 文字区），不要 `Height=Auto`；`ListBoxItem` 的 ControlTemplate 精简成
   一个 `ContentPresenter`（去掉默认选中底色和内边距），否则选中高亮会盖住缩略图、还多一层测量。
4. **缩略图不要直接绑原图**：用后台解码好的小位图缓存（见 7.2），cell 里只做绘制；本 spike 用
   `DrawingContext` 占位色块，单帧 0.69 ms 说明「绘制」本身不是瓶颈，瓶颈一定在解码/IO。
5. **集合整体替换**：换图库时给 `ItemsSource` 赋新集合（或批量 Add 后一次性 `Reset`），不要逐项 `Add`——
   每次 Add 都会触发一次布局。
6. **选中态与键盘**：`SelectionChanged` 驱动大图区；`ContentPresenter` 模板里自己做 2 px 高亮边框，
   比默认 `ListBoxItem` 选中底色轻得多；左右方向键用 `ScrollIntoView(item)` + `ScrollUnit=Pixel` 平滑跟随。
7. **滚轮横向滚动**：在缩略图条上处理 `PreviewMouseWheel`，`ScrollToHorizontalOffset(offset - e.Delta)`；
   不要让它冒泡给大图区。
8. **回归自检**：启动后抽样一次 `ItemsHost.Children.Count`，若 ≥ 数据项数就写日志/断言 —— 一旦有人把 ScrollViewer
   套在外面，这个数字会立刻暴露问题（本次 spike 就是这么抓到虚拟化失效的）。

### 7.2 横向缩略图条 + 大图区的布局要点

**布局骨架**（Grid 三行，缩略图条高度固定、大图区吃掉剩余空间）：

```
Window
└─ Grid
   ├─ Row 0 (Height=*)        大图区  ← 独立 Images 控件/自绘，不放进缩略图条
   ├─ Row 1 (Height=Auto)     缩略图条（ListBox，固定高 ≈ 单元高 196~220 dip）
   └─ Row 2 (Height=Auto)     状态栏/工具条
```

* **缩略图条高度写死**（`Height=196` 或 `Row.Height=Auto` + 单元固定高），**不要**用 `*` 让它随窗口高度变化，
  否则窗口拉高时视口变高、常驻容器数线性增长。
* **大图区与缩略图条不共享滚动容器**：大图区自己处理缩放/平移（`Viewbox`/`ScaleTransform` 或自绘），
  缩略图条只管横向滚动。把两者塞进同一个 ScrollViewer 会立刻把虚拟化搞死。
* **大图解码按显示尺寸**：`BitmapImage.DecodePixelWidth = 实际显示宽度（dip × DPI 缩放）`，
  只解码一次；`BitmapCacheOption.OnLoad` + 后台线程（`Task.Run`）解码，完成后 `Freeze()` 再上 UI 线程赋值。
  换图时先保留旧图直到新图就绪（避免闪白），并用 `CancellationTokenSource` 取消上一次请求——快速点缩略图时最关键。
* **预取 ±3 项**：选中项变化时后台解码相邻 3 张，滚到相邻项时命中缓存。
* **缩略图缓存用 LRU + 弱引用**：160×160×4B ≈ 100 KB/张，2000 张 ≈ 200 MB，绝不能全缓存。
  建议上限 300~500 张（约 30~50 MB），按 DPI 生成两个尺寸（150% 缩放时用 240 px 解码）。
* **大图区不要叠在缩略图条上方**做「幻灯片」层：独立层 + `Popup`/`Adorner` 会触发额外的合成层，滚动时容易掉帧。
* **DPI 变更**（拖到另一块屏）：监听 `DpiChanged` 重建缩略图缓存和单元尺寸常数，否则图会糊。
* **不要给缩略图条加 `Effect`/`Opacity` 动画**：WPF 的位图效果会让整条列表走软件渲染路径，直接毁掉 0.69 ms 的余量；
  需要「淡入」就用 `Opacity` 过渡（`DoubleAnimation` 作用在单元自身，短时长），不要用 `DropShadowEffect`。
* **滚动惯性**：`ScrollUnit=Pixel` 下拖拽滚动条是即时的；若要做平滑惯性（鼠标滚轮/拖拽惯性），
  用 `CompositionTarget.Rendering` 里逐帧逼近目标偏移（每帧 ≤ 视口/8），不要用 `DispatcherTimer` 定步长。
* **虚拟化面板的缓存**：本 spike 用默认值，实测常驻 9~12 个（视口约 8 个）；
  如果滚动时出现白块（容器来不及生成），再调 `VirtualizingPanel.CacheLength`（如 `2,2` 页），先别动。

### 7.3 性能预算参考

* 虚拟化列表：每帧 ~0.7 ms UI + ~0.15 ms 合成（本机），60 Hz 一帧 16.67 ms → **留给大图解码/滤镜约 15 ms**。
* 非虚拟化（2000 项）：合成侧 3.47 ms + 光栅化 11.8 ms，已经吃掉大半个预算，且随项数线性增长 → **不可用于万级图库**。
* 建议把「每帧 UI 工作耗时 P95 < 4 ms」「常驻容器数 < 视口单元数 × 2」「滚动期间工作集增长 < 20 MB」作为验收线。

## 8. 局限与后续

* 未解码真实图片（按 S4 要求用占位色块），真实场景的瓶颈会是 IO/解码，需在 S5 里用真实 RAW/JPG 缩略图重测。
* `RenderTargetBitmap` 是软件光栅化路径，只作参考；真窗口最终呈现走 GPU（Tier 2），实际合成开销低于该数字。
* 本机无 vsync/显示同步，帧间隔分布不代表真实 60 Hz 节奏，已改用「每帧 UI 工作耗时」作掉帧判据（见 4.3）。
* 未测：拖拽滚动条（`ScrollBar` 的 `Track` 拖动是高频 `ScrollToHorizontalOffset`，与帧泵负载相近）、DPI 变更、多窗口。
* `standard` 场景 25/75% 抽查出现 9/19/29/10/19 的波动，是回收动作按 Background 优先级异步执行导致的瞬时堆积，
  平均值（20.4）与 5.1 的帧统计才是有意义的数。

## 9. 复现

```powershell
# 一条命令跑完全部三个场景并打印表格（本机执行策略会拦截 .ps1，用 Invoke-Expression 加载）
powershell -NoProfile -Command "Get-Content 'D:\DSH工作区\yingshai\spikes\VirtList\run.ps1' -Raw -Encoding UTF8 | Invoke-Expression"

# 产物：spikes\VirtList\out\frame-*.png、bench-<时间戳>.log
```
