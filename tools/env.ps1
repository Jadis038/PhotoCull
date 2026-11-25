# 开发环境变量（本机：SDK 安装在工作区 toolchain 里，未做全机安装）
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_ROOT = "D:\DSH工作区\toolchain\dotnet"
if ($env:PATH -notlike "*toolchain\dotnet*") { $env:PATH = "$env:DOTNET_ROOT;$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_HOME = "D:\DSH工作区\toolchain\clihome"
$env:NUGET_PACKAGES = "D:\DSH工作区\toolchain\nugetcache"
$env:NUGET_HTTP_CACHE_PATH = "D:\DSH工作区\toolchain\nugethttpcache"
$env:TEMP = "D:\DSH工作区\toolchain\tmp"
$env:TMP = $env:TEMP
# 本机受限：子进程无法写 C:\Users\...，测试时把「影·筛」的数据目录指到工作区（正式安装的用户机器上不用设）
$env:YING_SHAI_DATA = "D:\DSH工作区\toolchain\appdata\影·筛"
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
$global:REPO = "D:\DSH工作区\yingshai"
