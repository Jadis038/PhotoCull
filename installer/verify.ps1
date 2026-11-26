# 影·筛 MSI 验收：静默安装 → 检查落地/快捷方式/注册表 → 跑自检 → 静默卸载 → 检查残留
# 用法：Get-Content installer/verify.ps1 -Raw -Encoding UTF8 | Invoke-Expression
#      （需要先跑过 installer/build.ps1 生成 MSI）

$ErrorActionPreference = 'Continue'
$Repo = if ($global:REPO) { $global:REPO } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$Msi = Join-Path $Repo 'dist\影·筛-Setup-1.0.4.msi'
$AppDir = Join-Path $env:LOCALAPPDATA 'Programs\影·筛'
$MenuLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\影·筛\影·筛.lnk'
$LogDir = Join-Path $Repo 'dist'
$pass = 0; $fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host "  [PASS] $name  $detail" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  [FAIL] $name  $detail" -ForegroundColor Red }
}

if (-not (Test-Path $Msi)) { throw "找不到 MSI：$Msi" }
Write-Host "MSI：$Msi（$([math]::Round((Get-Item $Msi).Length/1MB,1)) MB）" -ForegroundColor Cyan

# 造一个用户数据目录，验证卸载不会删它
$dataRoot = Join-Path $env:LOCALAPPDATA '影·筛'
New-Item -ItemType Directory -Force (Join-Path $dataRoot 'state') | Out-Null
Set-Content (Join-Path $dataRoot 'state\keepme.txt') 'uninstall must not delete this'

Write-Host "`n== 安装（静默）==" -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$Msi`"", '/qn', '/l*v', "`"$LogDir\install.log`"") -PassThru -Wait
$sw.Stop()
Check "安装退出码 0" ($p.ExitCode -eq 0) "exit=$($p.ExitCode) 用时 $([math]::Round($sw.Elapsed.TotalSeconds,2))s"

Check "安装目录存在" (Test-Path $AppDir) $AppDir
Check "影·筛.exe 落地" (Test-Path (Join-Path $AppDir '影·筛.exe'))
Check "LibRaw(raw_r.dll) 落地" (Test-Path (Join-Path $AppDir 'raw_r.dll'))
Check "LibRaw 许可证随之分发" ((Test-Path (Join-Path $AppDir 'licenses\LICENSE.LGPL')) -and (Test-Path (Join-Path $AppDir 'licenses\LICENSE.CDDL')) -and (Test-Path (Join-Path $AppDir 'licenses\COPYRIGHT')))
Check "MIT LICENSE 随包" (Test-Path (Join-Path $AppDir 'licenses\LICENSE'))
Check "使用说明随包" (Test-Path (Join-Path $AppDir '使用说明.md'))
Check "开始菜单快捷方式" (Test-Path $MenuLnk) $MenuLnk

$progId = Get-ItemProperty -Path 'HKCU:\Software\Classes\YingShai.Image' -ErrorAction SilentlyContinue
Check "ProgID 显示名为「影·筛 图片」" ($progId -and $progId.'(default)' -eq '影·筛 图片') "$($progId.'(default)')"
$cmd = (Get-ItemProperty -Path 'HKCU:\Software\Classes\YingShai.Image\shell\open\command' -ErrorAction SilentlyContinue).'(default)'
Check "打开命令带引号且指向影·筛.exe" ($cmd -like '*影·筛.exe*"%1"*') $cmd
$owp = Get-ItemProperty -Path 'HKCU:\Software\Classes\.nef\OpenWithProgids' -ErrorAction SilentlyContinue
Check "OpenWithProgids 注册了 YingShai.Image" ($null -ne $owp.'YingShai.Image')
$arp = Get-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
       Where-Object { $_.DisplayName -eq '影·筛' } | Select-Object -First 1
Check "控制面板卸载项 DisplayName=影·筛" ($null -ne $arp) "版本=$($arp.DisplayVersion)"

Write-Host "`n== 装好的程序能跑 ==" -ForegroundColor Cyan
$exe = Join-Path $AppDir '影·筛.exe'
$v = & $exe --version 2>&1 | Out-String
Check "--version 正常输出" ($v -match '影·筛' -and $v -match 'LibRaw') ($v.Trim() -replace "`r?`n", ' | ')

$st = & $exe --selftest 2>&1 | Out-String
$stLines = ($st -split "`r?`n" | Where-Object { $_ -match '自检结果' })
Check "--selftest 全绿" ($st -match '失败 0 项') $stLines

Write-Host "`n== 卸载（静默）==" -ForegroundColor Cyan
# 用注册表里实际登记的产品码卸载，避免 msiexec /x <msi路径> 与产品注册错位
$installedCode = ($arp.PSChildName)
if (-not $installedCode) { $installedCode = '{3F8A1C92-6D74-4E5B-9A0C-1B7E2D8F4C63}' }
Write-Host "  产品码：$installedCode"
$sw.Restart()
$p = Start-Process msiexec.exe -ArgumentList @('/x', $installedCode, '/qn', '/l*v', "`"$LogDir\uninstall.log`"") -PassThru -Wait
$sw.Stop()
Check "卸载退出码 0" ($p.ExitCode -eq 0) "exit=$($p.ExitCode) 用时 $([math]::Round($sw.Elapsed.TotalSeconds,2))s"
Check "安装目录已删除" (-not (Test-Path $AppDir)) $AppDir
Check "开始菜单快捷方式已删除" (-not (Test-Path $MenuLnk))
Check "ProgID 已删除" ($null -eq (Get-ItemProperty -Path 'HKCU:\Software\Classes\YingShai.Image' -ErrorAction SilentlyContinue))
Check "OpenWithProgids 条目已删除" ($null -eq (Get-ItemProperty -Path 'HKCU:\Software\Classes\.nef\OpenWithProgids' -ErrorAction SilentlyContinue).'YingShai.Image')
$arp2 = Get-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq '影·筛' } | Select-Object -First 1
Check "卸载项已移除" ($null -eq $arp2)
Check "用户缓存/索引未被删除" (Test-Path (Join-Path $dataRoot 'state\keepme.txt'))

Write-Host "`n结果：通过 $pass 项，失败 $fail 项" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
