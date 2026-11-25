# Spike S3 结论：用 WiX v4 打「影·筛」每用户 MSI

> 日期：2026-11-25 ｜ 机器：Windows 11 x64 ｜ 结论：**可落地**（静默装/卸全绿，免 UAC，perUser）
> 代码与产物：`spikes\WixHello\`（`out\YingShai-Setup-0.1.0.msi`）

## 1. 一句话结论

WiX **4.0.5**（dotnet tool 安装）＋ `Scope="perUser"` ＋ 标准目录 `LocalAppDataFolder` / `ProgramMenuFolder`，可以把 .NET 8 自包含单文件程序打成**每用户、免提权**的 MSI：
静默安装/卸载退出码都是 0、各约 **1 秒**、无 UAC 提示，文件落 `%LOCALAPPDATA%\Programs\影·筛`，开始菜单每用户快捷方式和 `HKCU\Software\Classes\YingShai.Image` 文件关联都正常，卸载后全部清干净。

- WiX：`4.0.5+b9b2f1b4`
- MSI：`YingShai-Setup-0.1.0.msi`，**29,007,872 字节（27.66 MB）**
- 静默安装 `msiexec /i ... /qn /l*v`：退出码 **0**，**1.01–1.02 秒**（两次复测）
- 静默卸载 `msiexec /x ... /qn /l*v`：退出码 **0**，**1.01 秒**（两次复测）
- 全程完整性级别 Medium（未提权），**无 UAC 弹窗**

## 2. 工具链怎么装（可复现命令）

```powershell
# 载入工作区环境（SDK/NUGET_PACKAGES/TEMP 都在 D:\DSH工作区）
$root = "D:\DSH工作区\yingshai"
Get-Content "$root\tools\env.ps1" -Raw -Encoding UTF8 | Invoke-Expression

# 安装 WiX v4（NuGet 走仓库里的 NuGet.config 镜像；4.0.5 在镜像上有）
dotnet tool install --tool-path "D:\DSH工作区\toolchain\wix" wix --version 4.0.5
& "D:\DSH工作区\toolchain\wix\wix.exe" --version    # -> 4.0.5+b9b2f1b4
```

- 采用 **dotnet tool**（`wix.exe`）方式，不依赖 MSBuild 的 `WixToolset.Sdk`，也不用装 Visual Studio。
- 未用 `heat`（Harvest）：程序以自包含单文件发布，一个 `File` 元素就够，避免收割目录。
- 卸载工具：`dotnet tool uninstall --tool-path "D:\DSH工作区\toolchain\wix" wix`（本项目未卸载）。

## 3. 构建命令（可复现）

一键：`spikes\WixHello\build.ps1`（内部做三件事：`dotnet publish` → 复制出中文名主程序 → `wix build`）。
等价手动命令：

```powershell
$spike = "D:\DSH工作区\yingshai\spikes\WixHello"
dotnet publish "$spike\src\Hello\Hello.csproj" -c Release -r win-x64 --self-contained true -o "$spike\build\publish"
Copy-Item "$spike\build\publish\hello.exe" "$spike\build\publish\影·筛.exe" -Force

& "D:\DSH工作区\toolchain\wix\wix.exe" build "$spike\installer\Product.wxs" `
    -arch x64 -pdbtype none `
    -o "$spike\out\YingShai-Setup-0.1.0.msi" `
    -intermediatefolder "$spike\build\intermediate" `
    -d SourceDir="$spike\build\publish" -d ProductVersion=0.1.0
```

要点：`-d` 必须写成 `-d 名字=值`（分开两个 token）；`-pdbtype none` 不产 `.wixpdb`；`-intermediatefolder` 把 CAB 生成用的中间目录也放到工作区。

## 4. 包写法要点

### 4.1 ProductName / 版本 / 编码

```xml
<Package Name="影·筛" Manufacturer="影·筛"
         Version="$(var.ProductVersion)"
         ProductCode="{60C513B1-611E-4537-843B-23C17D36FA4E}"
         UpgradeCode="{58530B61-D984-445B-B2BB-FA850040BFC5}"
         Language="2052" Codepage="936"
         Scope="perUser" InstallerVersion="500" Compressed="yes">
```

- `Name` 中文 → ARP 显示「影·筛」；`Manufacturer` 中文 → 发布者「影·筛」。
- `Codepage="936"`（GBK）是 zh-CN 下存中文 MSI 属性最稳的选择；包内**所有 Id 保持 ASCII**（`YingShaiAppDir`、`HelloExe`…）。
- spike 里 `ProductCode` 写死以便复现；**正式包不要写死**（见第 7 节）。

### 4.2 安装目录（%LOCALAPPDATA%\Programs\影·筛）

```xml
<StandardDirectory Id="LocalAppDataFolder">
  <Directory Id="ProgramsFolder" Name="Programs">
    <Directory Id="YingShaiAppDir" Name="影·筛" />   <!-- 目录 Id 用 ASCII，显示名中文 -->
  </Directory>
</StandardDirectory>
```

`LocalAppDataFolder` 是 Windows Installer 的每用户已知文件夹（`FOLDERID_LocalAppData`），比手写 `%LOCALAPPDATA%` 环境变量可靠。
装完路径：`C:\Users\<用户>\AppData\Local\Programs\影·筛\`。

### 4.3 每用户开始菜单快捷方式

```xml
<StandardDirectory Id="ProgramMenuFolder">
  <Directory Id="YingShaiMenuDir" Name="影·筛" />
</StandardDirectory>

<DirectoryRef Id="YingShaiMenuDir">
  <Component Id="StartMenuShortcut" Guid="{AD7D6DE5-...}">
    <Shortcut Id="AppStartMenuShortcut" Directory="YingShaiMenuDir"
              Name="影·筛" Description="影·筛 —— 图片筛选工具"
              Target="[YingShaiAppDir]影·筛.exe" WorkingDirectory="YingShaiAppDir" />
    <RemoveFolder Id="RemoveYingShaiMenuDir" Directory="YingShaiMenuDir" On="uninstall" />
    <RegistryValue Root="HKCU" Key="Software\YingShai\WixSpike" Name="StartMenu" Type="integer" Value="1" KeyPath="yes" />
  </Component>
</DirectoryRef>
```

- `ProgramMenuFolder` 在 perUser 上下文里解析成 `%APPDATA%\Microsoft\Windows\Start Menu\Programs`（实测落地在用户目录，不是 `ProgramData`）。
- 快捷方式组件**必须有 KeyPath**：这里用 HKCU 下的标记值（MSI 规定每个组件要有 KeyPath，快捷方式本身不能当 KeyPath）。
- 卸载时用 `RemoveFolder On="uninstall"` 删掉「影·筛」这个开始菜单子目录（`Programs` 本身是系统的，不动）。

### 4.4 HKCU 文件关联

```xml
<Component Id="ImageFileClass" Guid="{D735EDFD-...}">
  <!-- HKCU\Software\Classes\YingShai.Image 默认值 = 显示名 -->
  <RegistryValue Root="HKCU" Key="Software\Classes\YingShai.Image"
                 Type="string" Value="影·筛" KeyPath="yes" />
  <RegistryValue Root="HKCU" Key="Software\Classes\YingShai.Image"
                 Name="FriendlyTypeName" Type="string" Value="影·筛 图片" />
  <!-- shell\open\command 默认值；&quot; 是 XML 转义的双引号 -->
  <RegistryValue Root="HKCU" Key="Software\Classes\YingShai.Image\shell\open\command"
                 Type="string" Value="&quot;[YingShaiAppDir]影·筛.exe&quot; &quot;%1&quot;" />
</Component>
```

- **省略 `Name` 属性 = 写默认值**（未命名值）；要命名的值才写 `Name="..."`。
- 命令用 `[YingShaiAppDir]` 属性而不是硬编码路径：该属性在运行时展开成安装目录，**带尾部反斜杠**。
- 全部用 `HKCU`：每用户安装不碰 HKLM 的 Classes，卸载时 MSI 把自己建的键连同 `FriendlyTypeName`、`command` 子键一起删掉（实测干净）。
- 正式包建议同时注册 `OpenWithProgids`（`HKCU\Software\Classes\.jpg` 等）并在卸载时清掉，spike 只做了最小可验证的 ProgID。

### 4.5 卸载清理

```xml
<Component Id="CleanupAppDir" Guid="{A36EAE59-...}">
  <RegistryValue Root="HKCU" Key="Software\YingShai\WixSpike" Name="AppDir" Type="integer" Value="1" KeyPath="yes" />
  <RemoveFolder Id="RemoveYingShaiAppDir" Directory="YingShaiAppDir" On="uninstall" />
</Component>
```

注意：`%LOCALAPPDATA%\Programs` 是共享目录，不能删；只删「影·筛」子目录。

## 5. 静默安装 / 卸载实测

### 5.1 安装

```powershell
msiexec /i "D:\DSH工作区\yingshai\spikes\WixHello\out\YingShai-Setup-0.1.0.msi" /qn /l*v "...\out\install.log"
# 退出码 0，耗时 1.01 秒（第二次 1.02 秒）
```

安装日志里的关键证据（证明 perUser + 免提权）：

```
MSI_LUA: Package is marked as LUA installation capable with no elevation required
Running product '{60C513B1-611E-4537-843B-23C17D36FA4E}' with user privileges: It's not assigned.
```

落地结果：

```
C:\Users\16774\AppData\Local\Programs\影·筛\hello.exe     35,102,448 字节
C:\Users\16774\AppData\Local\Programs\影·筛\影·筛.exe     35,102,448 字节
%APPDATA%\Microsoft\Windows\Start Menu\Programs\影·筛\影·筛.lnk  -> ...\影·筛.exe
HKLM\...\CurrentVersion\Uninstall\{60C513B1-...}  DisplayName=影·筛  DisplayVersion=0.1.0  Publisher=影·筛（EstimatedSize≈68,547 KB）
HKCU\Software\Classes\YingShai.Image               (默认) = 影·筛
HKCU\Software\Classes\YingShai.Image\shell\open\command
    (默认) = "C:\Users\16774\AppData\Local\Programs\影·筛\影·筛.exe" "%1"
```

运行验证：`hello.exe` 正常打印；把 `shell\open\command` 里的 `%1` 换成假路径 `D:\tmp\fake-photo.jpg` 启动，程序打印出 `argv[0] = D:\tmp\fake-photo.jpg`，说明关联命令（含引号包裹、带中文路径）可用。

### 5.2 卸载

```powershell
msiexec /x "...\out\YingShai-Setup-0.1.0.msi" /qn /l*v "...\out\uninstall.log"   # 退出码 0，1.01 秒
msiexec /x "{60C513B1-611E-4537-843B-23C17D36FA4E}" /qn                            # 退出码 0，1.02 秒（另一种写法）
```

卸载后逐项确认：安装目录已删除、开始菜单目录/快捷方式已删除、ARP 卸载项已清除、`HKCU\Software\Classes\YingShai.Image` 整棵已清除、
`HKCU\Software\YingShai\WixSpike` 标记键已清除、`HKCU\Software\Microsoft\Installer\Products\...` 与
`HKLM\...\Installer\UserData\<SID>\Products\...` 产品注册数据也已清除。

### 5.3 体积与耗时汇总

| 指标 | 数值 |
| --- | --- |
| MSI 大小 | 29,007,872 字节（27.66 MB） |
| MSI 内 hello.exe / 影·筛.exe（自包含单文件，各自） | 35,102,448 字节（33.5 MB） |
| 安装目录总大小 | 70,204,896 字节（≈67 MB，含两份重复二进制） |
| 静默安装耗时 | 1.01 / 1.02 秒 |
| 静默卸载耗时 | 1.01 / 1.02 秒 |
| msiexec 日志体积 | install.log ≈ 94 KB，uninstall.log ≈ 87 KB |

> 体积提醒：spike 里 `hello.exe` 只是探针，`影·筛.exe` 是同一份二进制改名副本，所以 MSI 里存了两份 33.5 MB。正式包只有一个 exe，MSI 预期 ~14–20 MB（视是否单文件/裁剪而定）。

## 6. 踩过的坑（实测）

1. **`wix build -dSourceDir=...` 报 WIX0118**：WiX 4 只认 `-d SourceDir=...`（两个 token）。第一次构建就卡在这。
2. **WiX 打包依赖可写的 `%TEMP%`**：不载入 `tools\env.ps1` 时 `TEMP` 指向 `C:\Users\...\AppData\Local\Temp`，本机该路径写入被拒，wix 生成 CAB 抛 `WIX0001 System.UnauthorizedAccessException`。解决：先载 `env.ps1`（TEMP→`D:\DSH工作区\toolchain\tmp`）＋ `-intermediatefolder` 指到工作区。
3. **`Invoke-Expression` 载入脚本时 `$PSScriptRoot` 为空**：脚本里必须兜底（`$PSScriptRoot` → `$env:WIXHELLO_ROOT` → `$env:REPO\spikes\WixHello`），否则找不到 `.wxs`。
4. **perUser 包别再写 `ALLUSERS=2` + `MSIINSTALLPERUSER=1`**：`Scope="perUser"` 已让包变成 UAC compliant/LUA 包，msiexec 会打印
   `MSIINSTALLPERUSER property is not valid for UAC compliant package. Ignoring` 并删除 `ALLUSERS`——写了不生效，还会让日志看起来像 perUser 没配上。（这是 MS 双用途包文档的标准写法，但纯 perUser 包不需要。）
5. **每用户安装的 ARP 卸载项实测在 HKLM**：`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}`，`HKCU` 下没有。
   这不是装成 per-machine（日志是 `with user privileges`、文件在 `%LOCALAPPDATA%`、产品注册在 `HKCU\...\Installer\Products`），而是 Windows Installer 对「管理员组成员 + 未提权」用户的每用户安装的注册方式。
   **验收 perUser 的正确判据**：日志 `MSI_LUA` + `with user privileges`、文件落 `%LOCALAPPDATA%`、卸载无需提权；不要用「HKCU 下有 Uninstall 项」当唯一判据。
6. **`msiexec /i pkg.msi /qn ADD_LOCAL=ALL` 是假的**：`ADD_LOCAL` 不是 MSI 公开属性，msiexec 静默忽略、退出码照样 0（实测 1.05 秒装完，文件全装），很容易误以为“属性生效了”。控制 Feature 要用 **`ADDLOCAL`**；单 Feature 包（Level=1）在 `/qn` 下不需要它。
7. **`msiexec /x` 用 MSI 文件路径**：MSI 被移动/删除/改名就卸不掉；重新构建出不同包的 MSI 后不能用新 MSI 卸旧产品（会 1620/1635 之类）。持久方案：记 ProductCode，或让安装器把 MSI 缓存路径写进自己的卸载信息。
8. **`-pdbtype none`**：不加会多产一个 `.wixpdb`（spike 不需要）。
9. **首次 self-contained 发布要下载 runtime pack**：`Microsoft.NETCore.App.Runtime.win-x64` 等，本机 NuGet http 缓存目录不可写导致 `warning NU1900`（仅噪音，最终安装成功）。
10. **中文名**：`Name="影·筛"`、`Name="影·筛.exe"` 配 `Codepage="936"` 可正常打包安装运行；但包内 **Id 坚持 ASCII**，别拿中文当 Id。

## 7. 给正式安装包的建议

### 7.1 每用户 vs 每机器

| | perUser（本次方案，推荐） | perMachine |
| --- | --- | --- |
| 提权/UAC | 不需要，静默装 | 必须提权，会弹 UAC |
| 安装目录 | `%LOCALAPPDATA%\Programs\影·筛` | `C:\Program Files\影·筛` |
| 快捷方式 | `%APPDATA%\...\Start Menu`（`ProgramMenuFolder`） | `%ProgramData%\...\Start Menu` |
| 注册表 | HKCU（Classes/配置） | HKLM |
| 多用户 | 每个用户各装一份 | 全机器一份 |
| 升级/卸载 | 每个用户各自卸 | 管理员统一管 |

「影·筛」是图片筛选工具、单个用户使用、无服务/驱动/全局右键扩展需求 → **perUser 正确**：体验最好（双击即装、无 UAC），也避免写入 `C:\Program Files` 的权限问题。
代价：多用户机器上要各自安装；ARP 卸载项在 HKLM（见坑 5），别的管理员账号也会看到这条记录但卸不掉（会提示需要原用户/或直接报错）——正式包可在文档里注明“每用户安装”。

### 7.2 升级

- **`UpgradeCode` 固定**（本 spike 已有），**`ProductCode` 每次构建必须变**（正式包删掉 `<Package ProductCode>`，让 WiX 自动生成；或用 `$(var.ProductCode)` 由 CI 传入）。
- 加 `<MajorUpgrade DowngradeErrorMessage="已安装更高版本的「影·筛」。" />`：安装新版本时自动卸旧版（同一安装上下文）。
- **Component `Guid` 保持稳定**（尤其装了文件的那个组件），否则小版本升级会出现重复文件/残留。
- 版本号用三段 `x.y.z`（MSI 限制），只用前两段比较优先级；spike 用 `0.1.0`。
- 升级时不要用“同 ProductCode 的 MSI 覆盖安装”当升级手段：那是修复（repair），不会更新版本号，正式流程应新版本新 ProductCode + MajorUpgrade。
- 保持安装上下文一致：MSI 会沿用首次安装的上下文；一旦历史用户装成 perMachine，后续 perUser 包会造成两份并存。发布前定死 Scope。

### 7.3 卸载

- 卸载入口：ARP 里的 `MsiExec.exe /I{ProductCode}`（当前是 `/I`，正式包可改成 `/X` 更直观），或程序内“卸载”按钮调 `msiexec /x {ProductCode}`。
- 先让用户退出程序（WPF 进程占用 exe 时卸载会 1603；必要时用 Restart Manager / `MsiRMFilesInUse`）。
- 卸载要清：安装目录、开始菜单目录、HKCU 文件关联、`OpenWithProgids`、自建设置键。**不要**删 `%LOCALAPPDATA%\Programs` 本身。
- 用户数据（缓存、索引、设置）建议放 `%LOCALAPPDATA%\影·筛\` 并**默认保留**（卸载不删数据），提供“同时删除数据”选项时再用自定义动作/`RemoveFolderEx` 处理。
- 验证脚本别只查 HKCU ARP（坑 5），要同时查 HKLM ARP、安装目录、快捷方式、Classes 键、产品注册数据。

### 7.4 正式包还需要补的

- **签名**：`signtool sign /a /fd sha256 /tr <时间戳URL> /td sha256 app.msi`（未签名会触发 SmartScreen/信任提示），签名要在 `wix build` 之后做。
- **图标**：`<Icon Id="AppIcon" SourceFile="app.ico"/>` + `ARPPRODUCTICON`、快捷方式 `Icon="AppIcon"`（spike 无图标，所以 ARP/快捷方式用默认图标）。
- **多文件/依赖**：若最终不是单文件发布，需要用 `wix msi decompile` 或 harvest（`wix extension add WixToolset.Heat` 后用 heat）收集文件，否则几百个文件手写不现实。
- **code page 与多语言**：zh-CN 单语言用 `Codepage=936` + `Language=2052` 即可；要英文界面再用 `.wxl` 本地化。
- **文件关联**：只写 `HKCU\Software\Classes\YingShai.Image` 不会让资源管理器自动把 `.jpg` 关联到「影·筛」；要让用户可选“用影·筛打开”，应注册 `OpenWithProgids`（`HKCU\Software\Classes\.jpg\OpenWithProgids\YingShai.Image`）并让用户确认默认应用（Win10/11 不允许安装器静默抢默认程序）。
- **验收脚本**：把 `spikes\WixHello\verify.ps1` 的思路（退出码 + 落地 + 注册表 + 卸载残留 + 耗时）搬进正式包 CI。
