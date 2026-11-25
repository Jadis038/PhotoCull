# ============================================================================
# 「影·筛」spike S3 —— 一键构建 MSI（WiX v4 + .NET 8 自包含单文件）
#
# 本机 PowerShell 执行策略会拦截 .ps1 的直接执行，因此按下面方式调用（不要点源）：
#   $root = "D:\DSH工作区\yingshai"
#   Get-Content "$root\tools\env.ps1"    -Raw -Encoding UTF8 | Invoke-Expression
#   Get-Content "$root\spikes\WixHello\build.ps1" -Raw -Encoding UTF8 | Invoke-Expression
#
# 产物：spikes\WixHello\out\YingShai-Setup-<版本>.msi
# ============================================================================

$ErrorActionPreference = "Stop"

# 可用环境变量覆盖，默认值就是本机 spike 的取值
$Version   = if ($env:WIXHELLO_VERSION)   { $env:WIXHELLO_VERSION }   else { "0.1.0" }
$WixToolPath = if ($env:WIX_TOOLPATH)     { $env:WIX_TOOLPATH }       else { "D:\DSH工作区\toolchain\wix" }

# ---------- 1. 定位本 spike 根目录 ----------
# 用 Invoke-Expression 管道执行时 $PSScriptRoot 为空，所以要给几层兜底
$SpikeRoot = $PSScriptRoot
if (-not $SpikeRoot -and $env:WIXHELLO_ROOT) { $SpikeRoot = $env:WIXHELLO_ROOT }
if (-not $SpikeRoot -and $global:REPO)        { $SpikeRoot = Join-Path $global:REPO "spikes\WixHello" }
if (-not $SpikeRoot -and $env:REPO)           { $SpikeRoot = Join-Path $env:REPO "spikes\WixHello" }
if (-not $SpikeRoot -and (Test-Path (Join-Path (Get-Location) "installer\Product.wxs"))) {
    $SpikeRoot = (Get-Location).Path
}
if (-not $SpikeRoot -or -not (Test-Path (Join-Path $SpikeRoot "installer\Product.wxs"))) {
    throw "找不到 WixHello 目录：请设置 `$env:WIXHELLO_ROOT，或先 cd 到该目录"
}
$SpikeRoot = (Resolve-Path $SpikeRoot).Path
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $SpikeRoot)   # ...\spikes\WixHello -> ...\yingshai

# ---------- 2. 确保 dotnet / 环境变量就绪 ----------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $envPs1 = Join-Path $RepoRoot "tools\env.ps1"
    if (Test-Path $envPs1) {
        Get-Content $envPs1 -Raw -Encoding UTF8 | Invoke-Expression   # 载入 SDK / NUGET_PACKAGES / TEMP
    } else {
        throw "找不到 dotnet 命令，也找不到 $envPs1"
    }
}

$WixExe = Join-Path $WixToolPath "wix.exe"
if (-not (Test-Path $WixExe)) {
    throw "找不到 wix.exe：$WixExe（先执行 dotnet tool install --tool-path $WixToolPath wix --version 4.0.5）"
}

Write-Host "== spike 根目录 : $SpikeRoot"
Write-Host "== 版本        : $Version"
Write-Host "== wix.exe     : $(& $WixExe --version)"

# ---------- 3. 发布 hello.exe（自包含 + 单文件） ----------
$PublishDir = Join-Path $SpikeRoot "build\publish"
$OutDir     = Join-Path $SpikeRoot "out"
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PublishDir, $OutDir | Out-Null

$Csproj = Join-Path $SpikeRoot "src\Hello\Hello.csproj"
Write-Host "== dotnet publish ..."
& dotnet publish $Csproj -c Release -r win-x64 --self-contained true -o $PublishDir `
    -p:Version=$Version -p:InformationalVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$HelloExe = Join-Path $PublishDir "hello.exe"
if (-not (Test-Path $HelloExe)) { throw "发布目录里没有 hello.exe：$PublishDir" }

# 正式产品的主程序名是中文「影·筛.exe」；spike 里用同一份二进制改名复制一份，
# 这样文件关联/快捷方式指向的路径在探针环境下也真实可用。
Copy-Item $HelloExe (Join-Path $PublishDir "影·筛.exe") -Force

# ---------- 4. 编译成 MSI ----------
$MsiPath = Join-Path $OutDir "YingShai-Setup-$Version.msi"
if (Test-Path $MsiPath) { Remove-Item $MsiPath -Force }

# 注意：wix.exe 只认「-d 名字=值」两个参数分开写的形式，写成「-d名字=值」会报 WIX0118。
$InterDir = Join-Path $SpikeRoot "build\intermediate"
Write-Host "== wix build ..."
& $WixExe build (Join-Path $SpikeRoot "installer\Product.wxs") `
    -arch x64 `
    -o $MsiPath `
    -pdbtype none `
    -intermediatefolder $InterDir `
    -d SourceDir="$PublishDir" `
    -d ProductVersion="$Version"
if ($LASTEXITCODE -ne 0) { throw "wix build 失败，退出码 $LASTEXITCODE" }
if (-not (Test-Path $MsiPath)) { throw "wix build 未产出 MSI：$MsiPath" }

$MsiSize = (Get-Item $MsiPath).Length
Write-Host ""
Write-Host "== 构建完成"
Write-Host "   MSI : $MsiPath"
Write-Host ("   大小: {0:N0} 字节 / {1:N2} MB" -f $MsiSize, ($MsiSize / 1MB))
