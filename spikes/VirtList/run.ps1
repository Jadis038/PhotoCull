# 影·筛 S4 spike：横向虚拟化缩略图列表 —— 一键测量脚本
#
# 用法（本机执行策略会拦截 .ps1 直接执行，所以推荐下面两种方式）：
#   1) pwsh -ExecutionPolicy Bypass -File "D:\DSH工作区\yingshai\spikes\VirtList\run.ps1"
#   2) pwsh -Command "Get-Content 'D:\DSH工作区\yingshai\spikes\VirtList\run.ps1' -Raw -Encoding UTF8 | Invoke-Expression"
#   可选参数：-Items 2000 -Trips 2 -SkipBuild
#
# 注意：不要用「. 文件」点源，也不要用 `& 文件`——会被执行策略拒绝。

param(
    [string]$SpikeDir = "",
    [int]$Items = 2000,
    [int]$Trips = 2,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 通过 Invoke-Expression 加载时 $PSScriptRoot 为空，这里做兜底定位
$here = $SpikeDir
if ([string]::IsNullOrWhiteSpace($here)) { $here = $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($here)) { $here = 'D:\DSH工作区\yingshai\spikes\VirtList' }
if (-not (Test-Path (Join-Path $here 'VirtList.csproj'))) {
    throw "找不到 spike 项目: $here\VirtList.csproj（可用 -SpikeDir 指定）"
}
$repo = (Resolve-Path (Join-Path $here '..\..')).Path

# —— 加载工作区环境（dotnet SDK / NUGET_PACKAGES / TEMP / $REPO）——
$envPs1 = Join-Path $repo 'tools\env.ps1'
if (Test-Path $envPs1) {
    Get-Content $envPs1 -Raw -Encoding UTF8 | Invoke-Expression
} else {
    Write-Warning "未找到 $envPs1，沿用当前环境的 dotnet"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "找不到 dotnet，请检查 $envPs1"
}

$outDir = Join-Path $here 'out'
New-Item -ItemType Directory -Force $outDir | Out-Null
$exe = Join-Path $here 'bin\Release\net8.0-windows\win-x64\VirtList.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $outDir "bench-$stamp.log"

# —— 构建 ——
if (-not $SkipBuild) {
    Write-Host "== 构建 Release: $here\VirtList.csproj ==" -ForegroundColor Cyan
    & dotnet build (Join-Path $here 'VirtList.csproj') -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "构建失败（exit=$LASTEXITCODE）" }
}
if (-not (Test-Path $exe)) { throw "找不到产物: $exe" }

$scenarioNames = @{
    'virt'     = '虚拟化+回收'
    'standard' = '虚拟化+标准'
    'nonvirt'  = '关闭虚拟化'
}
$rows = @()
$logLines = New-Object System.Collections.Generic.List[string]

foreach ($key in @('virt', 'standard', 'nonvirt')) {
    Write-Host ""
    Write-Host "== 场景 $key（$($scenarioNames[$key])）==" -ForegroundColor Cyan
    $runArgs = @(
        '--scenario', $key,
        '--items', $Items,
        '--trips', $Trips,
        '--png', (Join-Path $outDir "frame-$key.png")
    )
    $lines = & $exe @runArgs 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    foreach ($line in $lines) {
        Write-Host $line
        $logLines.Add($line)
    }
    if ($code -ne 0) { throw "场景 $key 运行失败（exit=$code）" }

    $result = $lines | Where-Object { $_ -like 'RESULT|*' } | Select-Object -Last 1
    if (-not $result) { throw "场景 $key 没有输出 RESULT 行" }
    $parts = $result -split '\|'
    # 字段顺序见 Bench.cs 的 RESULT 输出
    $rows += [pscustomobject]@{
        Key        = $key
        Name       = $scenarioNames[$key]
        Host       = $parts[2]
        Items      = [int]$parts[3]
        Frames     = [int]$parts[4]
        Avg        = [double]$parts[5]
        P50        = [double]$parts[6]
        P95        = [double]$parts[7]
        P99        = [double]$parts[8]
        Max        = [double]$parts[9]
        DropCount  = [int]$parts[10]
        DropPct    = [double]$parts[11]
        Drop2      = [int]$parts[12]
        WsStart    = [double]$parts[13]
        WsPeak     = [double]$parts[14]
        WsPeakProc = [double]$parts[15]
        ContMin    = [int]$parts[16]
        ContAvg    = [double]$parts[17]
        ContMax    = [int]$parts[18]
        Created    = [int]$parts[19]
        FirstMs    = [double]$parts[20]
        Raster     = [double]$parts[21]
        ScrollIvl  = $parts[22]
        IdleIvl    = $parts[23]
        Png        = $parts[24]
    }
}

# —— 汇总表 ——
Write-Host ""
Write-Host "════════════════════════════════════════════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host " S4 汇总：横向缩略图条，$Items 项，$([int]$Trips * 2) 次平移" -ForegroundColor Green
Write-Host "════════════════════════════════════════════════════════════════════════════════════════════════════" -ForegroundColor Green
$table = $rows | ForEach-Object {
    [pscustomobject]@{
        '场景'       = $_.Key
        '宿主管线'   = $_.Host
        '帧数'       = $_.Frames
        '平均ms'     = ('{0:F2}' -f $_.Avg)
        'P95ms'      = ('{0:F2}' -f $_.P95)
        '最大ms'     = ('{0:F2}' -f $_.Max)
        '掉帧>16.7'  = ('{0} ({1:F1}%)' -f $_.DropCount, $_.DropPct)
        '容器 最小/平均/最大' = ('{0}/{1:F1}/{2}' -f $_.ContMin, $_.ContAvg, $_.ContMax)
        '实例化总数' = $_.Created
        '首帧布局ms' = ('{0:F0}' -f $_.FirstMs)
        '工作集 起始→峰值MB' = ('{0:F0}→{1:F0}' -f $_.WsStart, $_.WsPeak)
    }
}
$table | Format-Table -AutoSize | Out-String -Width 250 | ForEach-Object {
    Write-Host $_
    $logLines.Add($_)
}

Write-Host "帧图输出目录: $outDir" -ForegroundColor Green
Get-ChildItem (Join-Path $outDir 'frame-*.png') -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host ("  " + $_.Name + "  " + [math]::Round($_.Length / 1KB) + " KB") }

$logLines | Set-Content -Path $log -Encoding UTF8
Write-Host "完整日志: $log" -ForegroundColor Green
