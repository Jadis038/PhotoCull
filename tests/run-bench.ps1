# 性能基准（验收 §6-1）：第二个参数是文件夹；加 -Cold 先清缓存
param([string]$Folder, [switch]$Cold)
Get-Content (Join-Path $PSScriptRoot 'common.ps1') -Raw -Encoding UTF8 | Invoke-Expression
if (-not $Folder) { $Folder = "C:\Users\16774\ys_bench\farm2000" }
$csv = Join-Path $Repo ("docs\bench_{0}.csv" -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
$args = @('--bench', $Folder, '--csv', $csv)
if ($Cold) { $args += '--cold' }
Write-Host "基准：$Folder -> $csv" -ForegroundColor Cyan
exit (Invoke-YingShai $args)