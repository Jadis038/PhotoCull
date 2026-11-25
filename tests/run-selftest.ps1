# 跑内置自检（验收 §6-14/15/16/17 等）
Get-Content (Join-Path $PSScriptRoot 'common.ps1') -Raw -Encoding UTF8 | Invoke-Expression
Write-Host "影·筛 自检：$Exe" -ForegroundColor Cyan
exit (Invoke-YingShai @('--selftest'))