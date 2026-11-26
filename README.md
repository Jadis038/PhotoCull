# PhotoCull（影·筛）

> Windows 图片快速筛选工具：一次打开几千张 RAW，单击查看、双击标记，选中的图自动复制进「筛选」文件夹。
> 应用名「影·筛」（中间是 U+00B7 间隔号），PhotoCull 是本仓库/发行名。**macOS 版规划中**（见下方 Roadmap）。

[![Release](https://img.shields.io/badge/release-v1.0.5-blue)](../../releases/latest)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2021H2%2B%20x64-lightgrey)](#系统要求)

## 下载

到 [Releases](../../releases/latest) 下载 `PhotoCull-Setup-x.x.x.msi`：

- 每用户安装到 `%LOCALAPPDATA%\Programs\影·筛`，**免管理员权限**；
- 静默安装 `msiexec /i PhotoCull-Setup-x.x.x.msi /qn`；
- **自动更新**：每次启动自动检查 GitHub Releases，有新版本会弹窗提示，同意后自动下载并安装（直连失败自动走镜像）；
- 卸载不删图片、不删「筛选」、不删索引与缓存；
- 无签名证书时 SmartScreen 提示"未知发布者"——点「更多信息 → 仍要运行」即可。

## 它是什么

- **只为筛选而生**：不解 RAW 全图、不做修图。打开 2500 张 45MP RAW 的文件夹，1 秒内列出文件名，
  半秒内出首屏缩略图，切图 <100ms（命中缓存），全量缩略图 30 秒内完成；
- **标记 = 复制**：选中图（含 `.xmp/.aae/.thm` 伴生文件、RAW+JPG 双拍）复制到图片目录下的 `筛选\`，
  **原文件永远留在原地**；可切换"移动"模式；
- **取消 = 撤销副本**：删掉的是 `筛选\` 里的副本（进回收站可恢复），原目录文件绝不删除；
- **单张即时**：连点 10 张就是 10 次独立操作，各自成败互不影响；
- **Ctrl+Z 撤销**（≥50 步）；**重启后自动核对**：副本被外部删掉的图自动取消勾选。

## 界面与操作

上方大图 + 下方横向缩略图条 + 右侧信息栏（机型/镜头/焦距/光圈/快门/ISO/拍摄时间/分辨率/大小/色彩空间）。

| 操作 | 方式 |
|---|---|
| 查看某张图 | 单击缩略图 |
| 标记 / 取消标记 | 双击缩略图 / `X` / `Enter`（画面停留在当前图） |
| 上一张 / 下一张 | `←` `→` |
| 撤销 | `Ctrl+Z` |
| 大图 100% ↔ 适应窗口 | 双击大图（以双击点为锚） |
| 大图缩放 / 平移 | `Alt+滚轮` 缩放 · `Ctrl+滚轮` 左右 · 滚轮上下 · 左键拖动（Photoshop 手感） |
| 打开文件夹 | 按钮 / `Ctrl+O` / 拖拽 / 资源管理器"打开方式" |

## 支持的格式

**RAW（LibRaw 0.21.1，实机测试通过）**：

| 品牌 | 格式 |
|---|---|
| Nikon | NEF（含 Z8）、NRW |
| Canon | CR2、CR3（含 R5/R8） |
| Sony | ARW（含 A6700/A7R2） |
| Fujifilm | RAF |
| Olympus | ORF |
| Panasonic | RW2 |
| Pentax | PEF |
| Samsung | SRW |
| Hasselblad | 3FR |
| Leica | M（DNG） |
| Mamiya / Leaf | MEF / MOS |
| Kodak / Minolta / Epson | DCR / MRW / ERF |
| DJI | DNG（Mavic 系列） |
| 手机 DNG | Apple、Google、Samsung、小米、华为、OPPO、OnePlus、realme、HTC、LG、Ricoh 等 |

**普通图片**：JPG/JPEG、PNG、TIFF、WebP、BMP、HEIC/HEIF、AVIF、JXL（HEIC/AVIF/JXL 依赖系统图像扩展）。

**明确不支持**（专有格式，无公开解码方案）：X3F（Sigma/Polaroid Foveon）、三星专有压缩 DNG（TIFF 头
`Compression=52546`）。这类文件会显示占位与"无法解码"，不影响其它文件。

## 署名与致谢

- **作者 / Author**：[Галин](../../)
- **软件图标 / Logo**：**Ho Wing Own**
- **开发方式**：本软件由 **DeepSeek 与 GLM 系列 AI 模型协作编码完成**（架构设计、引擎实现、UI、打包、
  测试均在 AI 协作下完成，由 Галин 提出需求并验收）
- 程序内署名位于主窗口右下角状态栏与信息栏底部

## 开源协议

主代码 **MIT**。RAW 解码使用 [LibRaw](https://www.libraw.org/)（**LGPL-2.1 / CDDL-1.0** 双许可，动态链接
分发，未修改源码），许可证全文随安装包放在 `licenses\` 目录。

## Roadmap

- **macOS 版**：规划中，技术方案与移植清单见 [docs/MAC移植提示词.md](docs/MAC移植提示词.md)
  （Avalonia + .NET 10，兼容 macOS 16/26/27，复用本仓库 Core 引擎）
- 欢迎提 Issue / PR

## 从源码构建

需要 .NET SDK 与 WiX v4：

```powershell
dotnet build src\YingShai.App\YingShai.App.csproj -c Release
# MSI（自包含发布 + WiX 打包 + 版本号 1.0.5）
Get-Content installer\build.ps1 -Raw -Encoding UTF8 | Invoke-Expression
```

## 命令行

```text
影·筛.exe --selftest               内置自检（17 组 64 项）
影·筛.exe --bench <文件夹>         性能基准 + CSV
影·筛.exe --matrix <文件夹>        逐文件"能否打开"矩阵
影·筛.exe --update-check           手动检查更新
```

## 目录

```
%LOCALAPPDATA%\影·筛\cache\    缩略图缓存（LRU，默认 5GB）
%LOCALAPPDATA%\影·筛\state\    索引数据库（SQLite WAL）
%LOCALAPPDATA%\影·筛\logs\     运行日志
```
