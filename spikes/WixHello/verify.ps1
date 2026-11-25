# ============================================================================
# 「影·筛」spike S3 —— 静默安装 / 校验 / 静默卸载 实测脚本
#
# 调用方式（执行策略会拦 .ps1，用 Invoke-Expression 载入）：
#   $root = "D:\DSH工作区\yingshai"
#   Get-Content "$root\tools\env.ps1" -Raw -Encoding UTF8 | Invoke-Expression
#   Get-Content "$root\spikes\WixHello\verify.ps1" -Raw -Encoding UTF8 | Invoke-Expression
#
# 每一步都会打印：退出码、耗时、文件/注册表/快捷方式是否到位。
# ============================================================================

$ErrorActionPreference = "Stop"

$Version     = if ($env:WIXHELLO_VERSION) { $env:WIXHELLO_VERSION } else { "0.1.0" }
$ProductCode = "{60C513B1-611E-4537-843B-23C17D36FA4E}"

# ---------- 定位根目录（同 build.ps1） ----------
$SpikeRoot = $PSScriptRoot
if (-not $SpikeRoot -and $env:WIXHELLO_ROOT) { $SpikeRoot = $env:WIXHELLO_ROOT }
if (-not $SpikeRoot -and $global:REPO)        { $SpikeRoot = Join-Path $global:REPO "spikes\WixHello" }
if (-not $SpikeRoot -and $env:REPO)           { $SpikeRoot = Join-Path $env:REPO "spikes\WixHello" }
if (-not $SpikeRoot -and (Test-Path (Join-Path (Get-Location) "installer\Product.wxs"))) {
    $SpikeRoot = (Get-Location).Path
}
$SpikeRoot = (Resolve-Path $SpikeRoot).Path

$MsiPath      = Join-Path $SpikeRoot "out\YingShai-Setup-$Version.msi"
$InstallLog   = Join-Path $SpikeRoot "out\install.log"
$UninstLog    = Join-Path $SpikeRoot "out\uninstall.log"
$InstallDir   = Join-Path $env:LOCALAPPDATA "Programs\影·筛"
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\影·筛"
$LnkPath      = Join-Path $StartMenuDir "影·筛.lnk"
$ArpHklm      = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$ProductCode"
$ArpHkcu      = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$ProductCode"
$ClassKey     = "HKCU:\Software\Classes\YingShai.Image"
$HelperKey    = "HKCU:\Software\YingShai\WixSpike"

if (-not (Test-Path $MsiPath)) { throw "找不到 MSI：$MsiPath（先跑 build.ps1）" }
if (Test-Path $InstallLog) { Remove-Item $InstallLog -Force }
if (Test-Path $UninstLog)  { Remove-Item $UninstLog  -Force }

function Invoke-MsiExec {
    param([string]$Arguments)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p  = Start-Process -FilePath "msiexec.exe" -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    $sw.Stop()
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2) }
}

function Get-IntegrityLevel {
    # 从 whoami /groups 里抓 S-1-16-xxxx 完整性级别：8192=Medium（未提权）、12288=High（已提权）
    $line = whoami /groups | Select-String -Pattern "S-1-16-(\d+)" | Select-Object -First 1
    if (-not $line) { return "(未知)" }
    $rid = [regex]::Match($line.Line, "S-1-16-(\d+)").Groups[1].Value
    switch ($rid) {
        "8192"  { return "Medium（未提权，符合预期）" }
        "12288" { return "High（已提权）" }
        default { return "S-1-16-$rid" }
    }
}

function Get-LnkTarget {
    param([string]$Path)
    try {
        $sh = New-Object -ComObject WScript.Shell
        return $sh.CreateShortcut($Path).TargetPath
    } catch { return "(读取失败: $($_.Exception.Message))" }
}

# ============================ 0. 前置状态 ============================
Write-Host ""
Write-Host "===== [0/3] 前置状态 ====="
$msiSize = (Get-Item $MsiPath).Length
Write-Host ("  MSI            : {0}" -f $MsiPath)
Write-Host ("  MSI 大小       : {0:N0} 字节 ({1:N2} MB)" -f $msiSize, ($msiSize / 1MB))
Write-Host ("  当前进程完整性 : {0}" -f (Get-IntegrityLevel))
Write-Host ("  产品是否已安装 : {0}" -f ((Test-Path $ArpHklm) -or (Test-Path $ArpHkcu)))

# ============================ 1. 静默安装 ============================
Write-Host ""
Write-Host "===== [1/3] 静默安装 msiexec /i ... /qn /l*v install.log ====="
$r = Invoke-MsiExec "/i `"$MsiPath`" /qn /l*v `"$InstallLog`""
Write-Host ("  退出码 = {0}   耗时 = {1} 秒" -f $r.ExitCode, $r.Seconds)
if ($r.ExitCode -ne 0) {
    Write-Host "  安装失败，日志尾部："
    Get-Content $InstallLog -Tail 40 | ForEach-Object { Write-Host "    $_" }
    throw "安装失败，退出码 $($r.ExitCode)"
}
Write-Host "  --- 日志里的每用户/免提权证据 ---"
Select-String -Path $InstallLog -Pattern "MSI_LUA|with user privileges|with elevated privileges" -Encoding utf8 |
    Select-Object -First 3 | ForEach-Object { Write-Host ("    " + $_.Line.Substring($_.Line.IndexOf(']:') + 2).Trim()) }

# ============================ 2. 落地校验 ============================
Write-Host ""
Write-Host "===== [2/3] 安装结果校验 ====="
Write-Host "  安装目录 : $InstallDir"
if (Test-Path $InstallDir) {
    Get-ChildItem $InstallDir | ForEach-Object { Write-Host ("    文件: {0} ({1:N0} 字节)" -f $_.Name, $_.Length) }
} else { Write-Host "    (目录不存在)" }

Write-Host ("  开始菜单快捷方式 : {0}" -f (Test-Path $LnkPath))
if (Test-Path $LnkPath) { Write-Host ("    快捷方式指向 : {0}" -f (Get-LnkTarget $LnkPath)) }

Write-Host ("  ARP 卸载项 HKLM : {0}    HKCU : {1}" -f (Test-Path $ArpHklm), (Test-Path $ArpHkcu))
$arpKey = if (Test-Path $ArpHklm) { $ArpHklm } else { $ArpHkcu }
if (Test-Path $arpKey) {
    $arp = Get-ItemProperty $arpKey
    Write-Host ("    DisplayName / Version / Publisher : {0} / {1} / {2}" -f $arp.DisplayName, $arp.DisplayVersion, $arp.Publisher)
}

if (Test-Path $ClassKey) {
    $cls = Get-ItemProperty $ClassKey
    Write-Host ("  文件关联默认值 : {0}" -f $cls.'(default)')
    $cmdKey = Join-Path $ClassKey "shell\open\command"
    if (Test-Path $cmdKey) { Write-Host ("  shell\open\command : {0}" -f (Get-ItemProperty $cmdKey).'(default)') }
} else { Write-Host "  文件关联 : (不存在)" }

$HelloExe = Join-Path $InstallDir "hello.exe"
$AppExe   = Join-Path $InstallDir "影·筛.exe"
if (-not (Test-Path $HelloExe)) { throw "hello.exe 未落地：$HelloExe" }
if (-not (Test-Path $AppExe))   { throw "影·筛.exe 未落地：$AppExe" }

Write-Host "  --- 运行 hello.exe ---"
& $HelloExe | ForEach-Object { Write-Host "    $_" }
if ($LASTEXITCODE -ne 0) { throw "hello.exe 运行失败，退出码 $LASTEXITCODE" }

Write-Host "  --- 按文件关联的 command 启动 影·筛.exe（带一个假参数）---"
$cmd = (Get-ItemProperty "HKCU:\Software\Classes\YingShai.Image\shell\open\command").'(default)'
$expanded = $cmd -replace '"%1"', '"D:\tmp\fake-photo.jpg"'
$exeOfCmd = ($expanded -split '"')[1]
Write-Host "    展开后命令 : $expanded"
& $exeOfCmd "D:\tmp\fake-photo.jpg" | ForEach-Object { Write-Host "    $_" }

# ============================ 3. 静默卸载 ============================
Write-Host ""
Write-Host "===== [3/3] 静默卸载 msiexec /x ... /qn /l*v uninstall.log ====="
$r = Invoke-MsiExec "/x `"$MsiPath`" /qn /l*v `"$UninstLog`""
Write-Host ("  退出码 = {0}   耗时 = {1} 秒" -f $r.ExitCode, $r.Seconds)
if ($r.ExitCode -ne 0) {
    Write-Host "  卸载失败，日志尾部："
    Get-Content $UninstLog -Tail 40 | ForEach-Object { Write-Host "    $_" }
    throw "卸载失败，退出码 $($r.ExitCode)"
}

Write-Host ""
Write-Host "===== 卸载结果校验 ====="
$leftovers = @()
if (Test-Path $InstallDir)   { $leftovers += "安装目录仍存在: $InstallDir" }
if (Test-Path $StartMenuDir) { $leftovers += "开始菜单目录仍存在: $StartMenuDir" }
if (Test-Path $ArpHklm)      { $leftovers += "HKLM ARP 卸载项仍存在: $ArpHklm" }
if (Test-Path $ArpHkcu)      { $leftovers += "HKCU ARP 卸载项仍存在: $ArpHkcu" }
if (Test-Path $ClassKey)     { $leftovers += "文件关联仍存在: $ClassKey" }
if (Test-Path $HelperKey)    { $leftovers += "辅助标记键仍存在: $HelperKey" }
# 产品注册数据（MSI 自己在两处留的“已安装产品”记录）
$PackedGuid = "1B315C06E116735448B3321CD763AFE4"
$UserSid    = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
if (Test-Path "HKCU:\Software\Microsoft\Installer\Products\$PackedGuid") { $leftovers += "HKCU 产品注册仍存在" }
if (Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$UserSid\Products\$PackedGuid") {
    $leftovers += "HKLM UserData 产品注册仍存在"
}

if ($leftovers.Count -gt 0) {
    $leftovers | ForEach-Object { Write-Host "  [残留] $_" }
    throw "卸载后有残留"
}
Write-Host "  安装目录已删除、开始菜单快捷方式已删除、ARP 卸载项已清除、文件关联注册表已清除"
Write-Host ""
Write-Host "全部通过。"
