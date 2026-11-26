# 影·筛 —— MSI 打包（WiX v4，每用户安装，免 UAC）
# 用法：Get-Content installer/build.ps1 -Raw -Encoding UTF8 | Invoke-Expression
#      （本机 PowerShell 执行策略会拦截 .ps1 直接执行，故统一用 Invoke-Expression 加载）

$ErrorActionPreference = 'Stop'
$Repo    = if ($global:REPO) { $global:REPO } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$Dist    = Join-Path $Repo 'dist\app'
$OutDir  = Join-Path $Repo 'dist'
$WixExe  = 'D:\DSH工作区\toolchain\wix\wix.exe'
$Version = '1.0.1'

Write-Host "== [1/5] 自包含发布 (net8.0-windows, win-x64) ==" -ForegroundColor Cyan
if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
New-Item -ItemType Directory -Force $Dist | Out-Null
Push-Location $Repo
try {
    dotnet publish 'src\YingShai.App\YingShai.App.csproj' -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=false -p:DebugType=none -o $Dist --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）" }
} finally { Pop-Location }

Write-Host "== [2/5] 放置许可证与说明文件 ==" -ForegroundColor Cyan
$lic = Join-Path $Dist 'licenses'
New-Item -ItemType Directory -Force $lic | Out-Null
Copy-Item (Join-Path $Repo 'LICENSE') $lic -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $Repo 'README.md') $Dist -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $Repo 'docs\使用说明.md') $Dist -Force -ErrorAction SilentlyContinue
$raw = Join-Path $Repo 'native\libraw'
foreach ($f in 'COPYRIGHT','LICENSE.LGPL','LICENSE.CDDL') {
    if (Test-Path (Join-Path $raw $f)) { Copy-Item (Join-Path $raw $f) $lic -Force }
}
Copy-Item (Join-Path $Repo 'native\libraw\THIRD-PARTY-NOTICES.txt') $lic -Force -ErrorAction SilentlyContinue

Write-Host "== [3/5] 生成文件清单 WXS ==" -ForegroundColor Cyan
Get-Content (Join-Path $Repo 'installer\make-files-wxs.ps1') -Raw -Encoding UTF8 | Invoke-Expression
New-FilesWxs -DistDir $Dist -OutFile (Join-Path $Repo 'installer\generated\Files.wxs') -RootId 'YingShaiAppDir'

Write-Host "== [4/5] wix build ==" -ForegroundColor Cyan
$msi = Join-Path $OutDir ("影·筛-Setup-{0}.msi" -f $Version)
if (Test-Path $msi) { Remove-Item $msi -Force }
$inter = Join-Path $Repo 'installer\obj'
New-Item -ItemType Directory -Force $inter | Out-Null
& $WixExe build (Join-Path $Repo 'installer\Product.wxs') (Join-Path $Repo 'installer\generated\Files.wxs') `
    -d SourceDir=$Dist `
    -d ProductVersion=$Version `
    -d IconFile="$Repo\src\YingShai.App\Assets\影筛.ico" `
    -intermediatefolder $inter `
    -arch x64 `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build 失败（$LASTEXITCODE）" }

Write-Host "== [5/5] 完成 ==" -ForegroundColor Green
$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "MSI：$msi（$size MB）" -ForegroundColor Green
