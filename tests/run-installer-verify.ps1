# MSI 安装/卸载验收（验收 §6-13），需要先跑 installer\build.ps1
param([switch]$SkipBuild)
Get-Content (Join-Path $PSScriptRoot 'common.ps1') -Raw -Encoding UTF8 | Invoke-Expression
if (-not $SkipBuild) { Get-Content (Join-Path $Repo 'installer\build.ps1') -Raw -Encoding UTF8 | Invoke-Expression }
Get-Content (Join-Path $Repo 'installer\verify.ps1') -Raw -Encoding UTF8 | Invoke-Expression