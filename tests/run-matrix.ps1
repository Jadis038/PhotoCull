# 测试矩阵（验收 §5.5）：生成 docs\测试矩阵.csv
param([string]$Folder, [switch]$FullDecode)
Get-Content (Join-Path $PSScriptRoot 'common.ps1') -Raw -Encoding UTF8 | Invoke-Expression
if (-not $Folder) { $Folder = Join-Path $env:USERPROFILE 'Desktop\逆向' }
$csv = Join-Path $Repo 'docs\测试矩阵.csv'
$args = @('--matrix', $Folder, '--out', $csv)
if ($FullDecode) { $args += '--full-decode' }
Write-Host "测试矩阵：$Folder -> $csv" -ForegroundColor Cyan
exit (Invoke-YingShai $args)