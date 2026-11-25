# VirtList — 影·筛 S4 spike：横向虚拟化缩略图列表

验证 WPF（net8.0-windows）里「横向（水平滚动）缩略图条」的性能：2000 个数据项、每项 160×160 占位图 + 文件名，
对比 **虚拟化 + 容器回收 / 虚拟化 + 标准模式 / 关闭虚拟化** 三种实现，测帧耗时、掉帧、内存和实际实例化容器数。

> 结论文档（中文、含表格与建议）：[`../../docs/spike-S4-wpf.md`](../../docs/spike-S4-wpf.md)

## 一键运行

```powershell
# 方式 A（推荐）：Invoke-Expression 加载，绕开 .ps1 执行策略
powershell -NoProfile -Command "Get-Content 'D:\DSH工作区\yingshai\spikes\VirtList\run.ps1' -Raw -Encoding UTF8 | Invoke-Expression"

# 方式 B：-File + Bypass（脚本已带 UTF-8 BOM，中文不会乱码）
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\DSH工作区\yingshai\spikes\VirtList\run.ps1"

# 常用参数
#   -Items 2000   数据项数（默认 2000）
#   -Trips 2      来回趟数（默认 2 → 4 次平移）
#   -SkipBuild    不重新构建
#   -SpikeDir <目录>  Invoke-Expression 方式下的兜底定位（一般不用传）
```

脚本会：加载 `tools\env.ps1`（.NET 8.0.428 / NUGET_PACKAGES / TEMP）→ Release 构建 →
用**独立进程**依次跑三个场景（内存峰值才干净可比）→ 打印汇总表格 → 帧图写到 `out\` → 全量日志 `out\bench-<时间戳>.log`。

单场景也可以直接跑：

```powershell
$exe = "D:\DSH工作区\yingshai\spikes\VirtList\bin\Release\net8.0-windows\win-x64\VirtList.exe"
& $exe --scenario virt --items 2000 --trips 2 --png out\frame-virt.png
& $exe --scenario virt --offscreen      # 强制离屏渲染（Measure+Arrange+RenderTargetBitmap）
```

## 目录结构

```
spikes\VirtList\
  VirtList.csproj       net8.0-windows / UseWPF / Release 控制台可执行
  NuGet.config          与仓库根一致（国内镜像优先），脱离父目录也能 restore
  Program.cs            入口（STAThread，不自建 Application.Run，帧循环手动驱动）
  Options.cs            命令行参数与三种场景定义
  ListHost.cs           可视化树搭建（ListBox + 横向 VirtualizingStackPanel + ThumbCell）
  ThumbCell.cs          单个单元：160×160 占位色块（DrawingContext 绘制）+ 文件名，固定 168×196
  Items.cs              2000 条假数据（文件名 + 色相种子，不加载任何图片）
  Bench.cs              测量主体（帧泵、统计、工作集采样、PNG 输出、RESULT 行）
  Stats.cs              分位数 / 掉帧统计 / 计时工具
  run.ps1               一键测量脚本
  out\                  帧图 PNG + 运行日志
```

## 测量方法（要点）

* **真窗口优先**：本机虽然无交互桌面，但窗口站可用，`Window.Show()` 能真正上屏（实测成功），
  所以默认用**真窗口 + WPF 合成器**；`--offscreen` 可切到离屏渲染（不建窗口，靠
  `Measure + Arrange + RenderTargetBitmap` 驱动同一套布局/虚拟化管线），两条路径的容器数与帧耗时一致，
  互相验证。（`Show()` 失败时会自动回退离屏，并在输出里注明原因。）
* **帧驱动**：每帧 `ScrollViewer.ScrollToHorizontalOffset(offset)` → `UpdateLayout()` →
  排空 Dispatcher 队列（`Dispatcher.Invoke(DispatcherPriority.Background, ...)`，
  处理 `ItemContainerGenerator` 的容器回收动作与渲染失效），这正是 WPF 滚动一帧要做的事。
* **帧耗时 = 布局段（改偏移 + UpdateLayout）+ 队列段（容器回收 / 渲染失效）**，用 `Stopwatch` 分两段计时。
* **掉帧**：单帧预算 16.67 ms（60Hz），统计超过预算的帧数与比例（另附 >33.3 ms 的数量）。
* **为什么不用 `CompositionTarget.Rendering` 的帧间隔判掉帧**：本机是无桌面会话的服务器环境，
  没有 vsync；空闲窗口约 30 ms 才合成一次（按需节流），持续滚动反而 0.15~3.5 ms 就能走完一次合成，
  帧间隔既不代表 60Hz 也不代表性能上限。所以帧间隔只作为「渲染侧代价」的旁证（虚拟化 0.15 ms vs 非虚拟化 3.47 ms）。
* **内存**：每 8 帧采样 `Process.WorkingSet64` 取峰值，并记录进程 `PeakWorkingSet64` 与 GC 后托管堆。
* **容器数**：每帧读 `ItemsHost.Children.Count`（ItemsHost 为可视化树里 `IsItemsHost=true` 的面板），
  另在 0/25/50/75/100% 偏移处抽查「可见项序号区间」，证明窗口在移动。
* **实例化真相**：`ThumbCell` 构造时累加静态计数，体现 Recycling 是否真的复用容器。

## 关键结论（本机实测，2000 项，4188 帧，4 次平移）

| 场景 | 每帧 UI 平均 | P95 | 最大 | 掉帧>16.67ms | 常驻容器 | ThumbCell 总数 | 首帧布局 | 工作集峰值 | 合成侧/帧 | 整幅光栅化 |
|---|---|---|---|---|---|---|---|---|---|---|
| 虚拟化 + 回收（推荐） | **0.69 ms** | **1.03 ms** | 5.19 ms | 0 (0.0%) | **9~12（均值 11）** | **12** | 175 ms | 142.5 MB | **0.15 ms** | 1.07 ms |
| 虚拟化 + 标准 | 0.83 ms | 1.33 ms | 13.53 ms | 0 (0.0%) | 10~33（均值 20.4） | 8237 | 174 ms | 152.2 MB | 0.34 ms | 1.73 ms |
| 关闭虚拟化 | 0.16 ms | 0.30 ms | 14.41 ms | 0 (0.0%) | 2000 | 2000 | **286 ms** | **161.5 MB** | **3.47 ms** | **11.81 ms** |

* 2000 项、1280×360 视口下，三种实现**都不掉帧**——虚拟化在这里不是「能不能跑」，而是
  **内存、首帧、渲染线程余量、规模增长**的问题（非虚拟化 2000 项就多占约 22 MB 工作集、多 111 ms 首帧、合成侧慢 23 倍）。
* 规模到 10000 项时差距放大：虚拟化仍是 9~12 个容器、工作集峰值 144 MB；非虚拟化 10000 个容器、
  工作集峰值 263.9 MB（进程峰值 296.8 MB）、首帧布局 1067 ms。
* **最容易踩的坑**：`ItemsControl` 外面套一个外部 `ScrollViewer` 时虚拟化**完全不生效**（实测 2000 个容器全实例化）；
  必须用带内置 `ScrollViewer` 的 `ListBox`。且 `IsVirtualizing / VirtualizationMode / ScrollUnit`
  三个附加属性要写在 **ItemsPanelTemplate 的面板元素上**，写在 ListBox 上不会传到面板。

## 帧图

`out\frame-virt.png`、`out\frame-standard.png`、`out\frame-nonvirt.png`、`out\frame-virt-offscreen.png`
（每张图底部有一行半透明标注：场景 / 数据项数 / 常驻容器数 / 每帧耗时 / 宿主模式）。
