# UI 无桌面验收：创建真实窗口、打开文件夹、渲染截图
param([string]$Folder, [switch]$MarkDemo)
Get-Content (Join-Path $PSScriptRoot 'common.ps1') -Raw -Encoding UTF8 | Invoke-Expression
if (-not $Folder) { $Folder = "D:\DSH工作区\bench\demo" }
$png = Join-Path $Repo 'docs\ui_selftest.png'
$args = @('--ui-selftest', $Folder, $png)
if ($MarkDemo) { $args += '--mark-demo' }
$p = Start-Process -FilePath $Exe -ArgumentList $args -PassThru -Wait
Write-Host ("UI 自检退出码：" + $p.ExitCode)
Get-Content ([IO.Path]::ChangeExtension($png, '.log')) -ErrorAction SilentlyContinue
exit $p.ExitCode