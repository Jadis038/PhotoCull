# 单实例验收：第二个进程把命令行转给已有窗口后自行退出（任务书 §3.6 / §4）
# 用法：Get-Content tests\run-single-instance.ps1 -Raw -Encoding UTF8 | Invoke-Expression

$ErrorActionPreference = 'Continue'
Get-Content (Join-Path $PSScriptRoot 'common.ps1') -Raw -Encoding UTF8 | Invoke-Expression
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  [PASS] $name  $detail" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $name  $detail" -ForegroundColor Red; $script:fail++ }
}

$folderA = 'D:\DSH工作区\bench\demo'
$folderB = 'C:\Users\16774\ys_bench\farm2000'
$log = Join-Path $env:YING_SHAI_DATA 'logs\app.log'
Remove-Item $log -ErrorAction SilentlyContinue

Write-Host "启动第一个实例（打开 demo 文件夹）…" -ForegroundColor Cyan
$p1 = Start-Process -FilePath $Exe -ArgumentList @($folderA) -PassThru
Start-Sleep -Seconds 4
Check "第一个实例在运行" (-not $p1.HasExited) "pid=$($p1.Id)"

Write-Host "启动第二个实例（带另一个文件夹参数）…" -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
$p2 = Start-Process -FilePath $Exe -ArgumentList @($folderB) -PassThru
$exited = $p2.WaitForExit(8000)
$sw.Stop()
Check "第二个进程在 8 秒内退出（没有开第二个窗口）" $exited ("耗时 " + [math]::Round($sw.Elapsed.TotalSeconds, 2) + "s，exit=" + $(if ($exited) { $p2.ExitCode } else { 'running' }))
Check "第一个实例仍在运行" (-not $p1.HasExited) "pid=$($p1.Id)"

Start-Sleep -Seconds 2
$text = if (Test-Path $log) { Get-Content $log -Raw -Encoding UTF8 } else { '' }
Check "日志里有第一个文件夹" ($text -match [regex]::Escape($folderA))
Check "日志里有转交过来的第二个文件夹" ($text -match [regex]::Escape($folderB))
Check "日志记录了「收到新实例请求」" ($text -match '收到新实例请求')

try { $p1.Kill() } catch { }
Write-Host ""
if ($fail -eq 0) { Write-Host "单实例验收：全部通过" -ForegroundColor Green } else { Write-Host "单实例验收：失败 $fail 项" -ForegroundColor Red }
exit $fail
