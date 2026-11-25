# 影·筛（YingShai）

> 一个为「一次看两千张 RAW」而做的 Windows 图片快速筛选工具。
> 左下角缩略图条扫图，双击 / 按 X 把要留的图复制进同目录的「筛选」文件夹，剩下的一键不想要就取消。
> 中文界面，x64，单文件 MSI 安装，免管理员权限。

## 它是什么

- **只为筛选而生**：不解 RAW 全图、不做修图、不做导出。打开 2000 张 45MP RAW 的文件夹，
  1 秒内列出文件名，2 秒内出首屏缩略图，切图 < 100ms（命中缓存）。
- **标记 = 复制**：把选中的图（含 `.xmp`/`.aae`/`.thm` 伴生文件、同名 JPG/HEIF 双拍）复制到
  图片目录下的 `筛选\` 子目录，**原文件永远留在原地**。可在设置里切「移动」。
- **取消 = 撤销副本**：对已标记的图再按一次 X，删掉的是 `筛选\` 里的副本（进回收站，可恢复），
  原目录文件绝不删除。每条标记记录自己记住是复制还是移动产生的，中途改模式也不影响旧记录。
- **单张即时**：连点 10 张就是 10 次独立操作，各自成功/失败，互不影响；队列并发 1，界面不卡。
- **Ctrl+Z 撤销**：标记 / 取消 / 移动都可撤销（≥ 50 步，实际保留 200 步）。

## 安装

从 [Releases](../../releases) 下载 `影·筛-Setup-1.0.0.msi`，双击安装即可：

- 每用户安装到 `%LOCALAPPDATA%\Programs\影·筛`，**不需要管理员权限**；
- 会创建开始菜单快捷方式，并在「打开方式」里注册「影·筛」（不抢占默认打开程序）；
- 静默安装：`msiexec /i 影·筛-Setup-1.0.0.msi /qn`
- 卸载：`msiexec /x 影·筛-Setup-1.0.0.msi /qn`（或「应用和功能」里卸载）
- 卸载**不会**删除你的图片、`筛选` 文件夹、索引与缩略图缓存；如需清缓存，手动删
  `%LOCALAPPDATA%\影·筛\` 即可。

### SmartScreen「未知发布者」

没有代码签名证书时，Windows SmartScreen 会提示「Windows 已保护你的电脑 / 未知发布者」，
点「更多信息 → 仍要运行」即可。这是预期行为，不是程序有问题。

要消除提示，有两种正规做法：

1. **自签名证书**（自己/小团队内部分发，用户需导入证书信任）：
   ```powershell
   New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=影·筛 开源项目" `
       -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm SHA256
   signtool sign /sha1 <证书指纹> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 影·筛-Setup-1.0.0.msi
   ```
   用户需要把该证书导入「受信任的根证书颁发机构」，否则仍会提示。
2. **购买代码签名证书**（OV/EV，DigiCert、Sectigo、SSL.com 等）。
   OV 证书签名后仍需累积 SmartScreen 声誉；EV 证书可立即获得信誉。
   签名命令同上（用证书对应的 SHA1 指纹或 PFX：`signtool sign /f cert.pfx /p 密码 ...`）。

## 使用

| 操作 | 快捷键 / 方式 |
|---|---|
| 打开文件夹 | 左上「打开文件夹」/ `Ctrl+O` / 把文件或文件夹拖进窗口 |
| 从资源管理器打开 | 右键图片 →「打开方式」→「影·筛」 |
| 切换图片 | 缩略图条单击 / `←` `→` |
| 标记（复制进「筛选」） | 双击缩略图 / `X` / `Enter` / 右键菜单 |
| 取消标记 | 对已标记的图再按一次 `X` / 双击 |
| 撤销上一步 | `Ctrl+Z` |
| 大图适应窗口 / 100% / 自由缩放 | 双击大图 / `0` / `1` / 滚轮 |
| 拖动平移 | 大图里按住左键拖动 |
| 刷新（重新核对索引） | `F5` |

界面：上方大图、下方横向缩略图条（滚轮横向滚动）、右侧信息栏（机型/镜头/焦距/光圈/快门/ISO/
拍摄时间/分辨率/文件大小/色彩空间，取不到的显示「—」）、右下角状态区（后台任务进度与待筛数量）。

## 命令行（无 GUI 也能跑）

```text
影·筛.exe --selftest                    # 内置自检：扫描/自然排序/复制/冲突改名/取消/索引重建/撤销
影·筛.exe --bench <文件夹>              # 性能基准，输出表格 + CSV
影·筛.exe --bench <文件夹> --cold       # 先清空缩略图缓存再测
影·筛.exe --matrix <文件夹> --full-decode   # 生成「测试矩阵」CSV（各格式能否打开、预览、耗时）
影·筛.exe --version
```

## 性能（实测，详见 [docs/bench.md](docs/bench.md)）

测试环境：Windows 11 x64 虚拟机，D 盘；样本为 2000 张 Nikon D750 NEF（21MB）+ 2000 张同名 JPG
（硬链接构造 4000 条目）。Z8 45MP 的 NEF 因手上没有样本，另用单张实测外推。

| 指标 | 目标 | 实测 |
|---|---|---|
| 打开文件夹 → 列表可见（4000 条） | < 1.0s | **0.008s** |
| 首屏 20 张缩略图 | < 2.0s | **0.19s** |
| 单击切图（缓存命中 / 首次） | < 100ms / < 400ms | **0.0ms / 24ms** |
| 100% 全解（24MP） | 后台 < 3s | **0.93s** |
| 全量缩略图（4000 张 / 折算 2000 张） | < 3 分钟 | **16.6s / 8.3s** |
| 内存峰值 | < 1.2GB | **574MB** |
| 单张解码 P50 / P95 | — | 14ms / 23ms |

## 支持格式

RAW（LibRaw 0.21.1）：NEF/NRW、CR2/CR3/CRW、ARW/SR2/SRF、RAF、ORF、RW2、PEF、DNG、SRW、3FR、
MEF、MOS、MRW、ERF、DCR、IIQ、RWL、X3F（见下）。
普通图片：JPG/JPEG、PNG、TIFF、WebP、BMP、HEIC/HEIF（依赖系统 HEIF 解码器）。

> **X3F（Sigma Foveon）不受支持**：LibRaw 0.21 打不开样本里的 Polaroid x530 / Sigma dp2 Quattro；
> 这类文件会显示为「无法解码」，不会崩溃，也不会影响其它文件。

## 目录与数据

```
%LOCALAPPDATA%\影·筛\
├─ cache\      缩略图磁盘缓存（160px 条带 / 1024px 预览，LRU，默认上限 5GB）
├─ state\      index.db（SQLite，WAL）：标记记录 + 缓存索引
├─ logs\       日志
└─ settings.json
```

图片目录里只会多出一个 `筛选\` 子目录，绝不写入数据库文件。

## 从源码构建

需要 .NET 8 SDK 与 WiX v4（`dotnet tool install --global wix`）。

```powershell
dotnet build src\YingShai.App\YingShai.App.csproj -c Release
# 出 MSI（自包含发布 + WiX 打包）
Get-Content installer\build.ps1 -Raw -Encoding UTF8 | Invoke-Expression
```

## 架构

```
src\YingShai.Core\    引擎（与 UI 完全解耦）
  Scanning\   目录枚举（FindFirstFileExW，零解码）、自然排序
  Imaging\    LibRaw P/Invoke（一次打开取元数据+内嵌预览）、WIC 解码、EXIF
  Cache\      三级缓存（内存 LRU / 磁盘 JPEG / 源文件）
  Jobs\       优先级解码调度（4 线程、可去重、可重排、可降并发）
  Ops\        复制/移动/删除（CopyFileEx 进度+取消、.part 原子改名、回收站）、单写队列、撤销栈
  Index\      SQLite（WAL）索引与一致性核对
  Session\    ViewerSession：一个文件夹一个会话
src\YingShai.App\     WPF 界面（大图 + 虚拟化缩略图条 + 信息栏）
installer\            WiX v4 打包（每用户 MSI）
docs\                 规格、测试矩阵、基准、spike 报告
tests\                自检与压测脚本
```

关键设计：**列表阶段零解码**、**每个文件只打开一次**、**不做全解除非 100%**、
**缩略图离开可视区 ±200 立即释放**、**位图用 DecodePixelWidth 在解码期缩小**
（否则 45MP 整张载入会把内存打爆）。

## 许可证

主代码 MIT。RAW 解码使用 LibRaw（LGPL-2.1 / CDDL-1.0 双许可，动态链接分发），
许可证全文随安装包放在 `licenses\` 目录，也见 [native/libraw/](native/libraw/)。
