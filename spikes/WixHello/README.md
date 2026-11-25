# WixHello —— 「影·筛」spike S3：WiX v4 每用户 MSI 打包探针

用 WiX 4.0.5 把 .NET 8 控制台程序 `hello.exe` 打成 MSI，验证**每用户（perUser）静默安装/卸载**整条链路。
产物：`out\YingShai-Setup-0.1.0.msi`（27.66 MB）。结论与正式安装包建议见 `..\..\docs\spike-S3-wix.md`。

## 环境

- Windows 11，x64；当前用户是管理员组成员但**未提权**（完整性级别 Medium）
- .NET SDK 8.0.428（`D:\DSH工作区\toolchain\dotnet`，不是全机安装）
- WiX：`D:\DSH工作区\toolchain\wix\wix.exe`，版本 **4.0.5+b9b2f1b4**（dotnet tool 方式安装）
- 本机 PowerShell 执行策略禁止直接跑 `.ps1`，所有脚本都要用 `Invoke-Expression` 载入

## 一键复现

```powershell
# 0) 载入工作区环境（dotnet / NUGET_PACKAGES / TEMP / $REPO）
$root = "D:\DSH工作区\yingshai"
Get-Content "$root\tools\env.ps1" -Raw -Encoding UTF8 | Invoke-Expression

# 1) 安装 WiX（只需一次，已装过可跳过）
dotnet tool install --tool-path "D:\DSH工作区\toolchain\wix" wix --version 4.0.5

# 2) 一键构建 MSI（publish hello.exe -> 复制出 影·筛.exe -> wix build）
$env:WIXHELLO_ROOT = "$root\spikes\WixHello"
Get-Content "$env:WIXHELLO_ROOT\build.ps1"  -Raw -Encoding UTF8 | Invoke-Expression

# 3) 静默安装 -> 校验 -> 静默卸载（打印退出码、耗时、注册表/快捷方式检查）
Get-Content "$env:WIXHELLO_ROOT\verify.ps1" -Raw -Encoding UTF8 | Invoke-Expression
```

也可以绕过脚本手动跑（等价命令）：

```powershell
dotnet publish $env:WIXHELLO_ROOT\src\Hello\Hello.csproj -c Release -r win-x64 --self-contained true -o $env:WIXHELLO_ROOT\build\publish
Copy-Item $env:WIXHELLO_ROOT\build\publish\hello.exe $env:WIXHELLO_ROOT\build\publish\影·筛.exe -Force

# 注意：-d 必须和名字=值分成两个参数写，写成 -dSourceDir=... 会报 WIX0118
& "D:\DSH工作区\toolchain\wix\wix.exe" build $env:WIXHELLO_ROOT\installer\Product.wxs `
    -arch x64 -pdbtype none `
    -o $env:WIXHELLO_ROOT\out\YingShai-Setup-0.1.0.msi `
    -intermediatefolder $env:WIXHELLO_ROOT\build\intermediate `
    -d SourceDir="$env:WIXHELLO_ROOT\build\publish" `
    -d ProductVersion=0.1.0
```

安装 / 卸载（实测用的就是这两条）：

```powershell
msiexec /i "D:\DSH工作区\yingshai\spikes\WixHello\out\YingShai-Setup-0.1.0.msi" /qn /l*v "out\install.log"
msiexec /x "D:\DSH工作区\yingshai\spikes\WixHello\out\YingShai-Setup-0.1.0.msi" /qn /l*v "out\uninstall.log"
# 或者用 ProductCode（移动/删除 MSI 文件后更稳）：
msiexec /x "{60C513B1-611E-4537-843B-23C17D36FA4E}" /qn
```

## 目录结构

```
spikes\WixHello\
├─ build.ps1                  # 一键构建：dotnet publish + 复制中文名 exe + wix build
├─ verify.ps1                 # 一键实测：静默装 -> 校验 -> 静默卸
├─ README.md                  # 本文件
├─ src\Hello\
│  ├─ Hello.csproj            # net8.0 win-x64 自包含单文件，AssemblyName=hello
│  └─ Program.cs              # 打印 hello 和命令行参数（用于验证文件关联传参）
├─ installer\Product.wxs      # WiX v4 源码（perUser / LOCALAPPDATA / 开始菜单 / HKCU 文件关联）
├─ build\                     # 构建中间产物（publish、intermediate），可随时删
└─ out\
   ├─ YingShai-Setup-0.1.0.msi   # 交付产物
   ├─ install.log / uninstall.log # msiexec 详细日志（/l*v，实测留档）
```

## 包内容要点

| 项 | 写法 |
| --- | --- |
| ProductName | `Name="影·筛"`（ARP 里显示「影·筛」） |
| 安装上下文 | `Scope="perUser"` + `InstallerVersion="500"` → MSI_LUA 免提权 |
| 安装目录 | `StandardDirectory Id="LocalAppDataFolder"` → `Name="Programs"` → `Directory Id="YingShaiAppDir" Name="影·筛"` |
| 主程序 | `hello.exe`（探针）和 `影·筛.exe`（同一份二进制改名，供快捷方式/文件关联指向） |
| 开始菜单 | `StandardDirectory Id="ProgramMenuFolder"` → `Name="影·筛"`，中文 `.lnk` 指向 `[YingShaiAppDir]影·筛.exe` |
| 文件关联 | `HKCU\Software\Classes\YingShai.Image` 默认值=`影·筛`，`shell\open\command` 默认值=`"[YingShaiAppDir]影·筛.exe" "%1"` |
| 卸载清理 | 两个 `RemoveFolder On="uninstall"`（安装目录、开始菜单目录）+ 注册表组件自动清除 |
| 组件 KeyPath | 目录清理/快捷方式组件用 `HKCU\Software\YingShai\WixSpike` 下的标记值当 KeyPath（MSI 要求组件必须有 KeyPath） |
| 编码 | `Codepage="936"`（GBK，zh-CN 环境的 MSI 库存中文最稳）、`Language="2052"` |

## 实测结果（2026-11-25，本机）

- MSI：`out\YingShai-Setup-0.1.0.msi`，29,007,872 字节（27.66 MB）
- 静默安装：退出码 **0**，耗时 **1.01–1.02 秒**（两次实测）
- 静默卸载：退出码 **0**，耗时 **1.01 秒**（两次实测）
- 安装后：`%LOCALAPPDATA%\Programs\影·筛\hello.exe` 可运行；开始菜单快捷方式指向 `影·筛.exe`；HKCU 文件关联的 command 展开后能正常带参启动
- 卸载后：安装目录、开始菜单目录、ARP 卸载项、HKCU 文件关联、产品注册数据全部清除
- 全程**无 UAC 弹窗**，msiexec 日志证据：
  - `MSI_LUA: Package is marked as LUA installation capable with no elevation required`
  - `Running product '{60C513B1-...}' with user privileges: It's not assigned.`

## 踩过的坑（按踩坑顺序）

1. **`wix build -d` 的写法**：只能 `-d 名字=值` 两个参数分开；`-dSourceDir=...` 报 `error WIX0118: Additional argument ... was unexpected`。
2. **WiX 打包需要可写的 %TEMP%**：不载入 `env.ps1` 时 `%TEMP%` 指向 `C:\Users\...\AppData\Local\Temp`（本机对该目录的写入被沙箱/ACL 拒掉），wix 生成 CAB 时报
   `WIX0001: System.UnauthorizedAccessException: Access to the path 'C:\Users\...\Temp\xxxx' is denied`。
   解决：先载入 `tools\env.ps1`（`TEMP` 改到 `D:\DSH工作区\toolchain\tmp`），并用 `-intermediatefolder` 把中间目录也指到工作区里。
3. **脚本被 Invoke-Expression 载入时 `$PSScriptRoot` 为空**：`build.ps1`/`verify.ps1` 里做了多层兜底（`$PSScriptRoot` → `$env:WIXHELLO_ROOT` → `$global:REPO`（`env.ps1` 设的）→ 当前目录），所以只要先载入 `tools\env.ps1` 就能直接跑，不用手动设 `WIXHELLO_ROOT`；否则会因为找不到 `installer\Product.wxs` 直接失败。
4. **perUser 包不要再写 `ALLUSERS=2` + `MSIINSTALLPERUSER=1`**：`Scope="perUser"` 已经把包标成 UAC compliant/LUA 包，msiexec 会打印
   `MSIINSTALLPERUSER property is not valid for UAC compliant package. Ignoring`，并把 `ALLUSERS` 删掉。写了没坏处，但日志看着像“perUser 没生效”，容易误判。
5. **每用户安装的 ARP 卸载项实测落在 HKLM**：`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}`，`HKCU` 下没有。
   这不是装成 per-machine（日志明确 `with user privileges`、文件全在 `%LOCALAPPDATA%`、产品注册在 `HKCU\Software\Microsoft\Installer\Products\...`），
   而是 Windows Installer 对“管理员组成员、未提权”用户的每用户安装的注册方式。**验收不要用“HKCU 下有 Uninstall 项”当 perUser 判据**。
6. **`msiexec /i ... ADD_LOCAL=ALL` 是无效属性**：`ADD_LOCAL` 不是 MSI 公开属性，msiexec 静默忽略、退出码仍 0（实测 1.05 秒装完，文件照装）。
   想控制 Feature 要用 **`ADDLOCAL`**；本包只有一个默认 Feature（Level=1），/qn 下不需要它。
7. **`msiexec /x` 用 MSI 文件路径**时，如果 MSI 被移动/删除/改名就卸不掉；用 ProductCode 更稳。重新构建出新 MSI 后不要用新 MSI 去卸旧产品。
8. **`-pdbtype none`**：不写会顺手产出 `.wixpdb`；spike 不需要调试符号。
9. **self-contained 发布首次要拉 runtime pack**：`Microsoft.NETCore.App.Runtime.win-x64` 等从 NuGet 下载；本机 NuGet 的 http 缓存目录被拒写会出现
   `warning NU1900`（只是警告，能装成功），属环境噪音，不影响产物。
10. **中文文件名/目录名**：包内 `Name="影·筛"`、`Name="影·筛.exe"` 需要 `Codepage="936"`，WiX 能正常打包，安装后文件名正确、能启动；
    但**包内 Id（Directory/Component/Feature/ProductCode 等）坚持 ASCII**（`YingShaiAppDir`），否则维护困难、部分工具链对非 ASCII Id 支持差。

## 说明

- spike 里 `hello.exe` 是探针，`影·筛.exe` 只是它的副本，所以 MSI 里同一份 35 MB 自包含二进制存了两份（27.66 MB → 正式包只有一个 exe 时会小一半）。
- 正式包应每次构建生成新的 `ProductCode`、只固定 `UpgradeCode`，并加 `<MajorUpgrade/>`；本 spike 为了可复现把 `ProductCode` 写死了。
- 正式包还需 Authenticode 签名（signtool）+ 图标（`ARPPRODUCTICON` / 快捷方式 `Icon`），spike 未做。
