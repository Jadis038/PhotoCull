# 影·筛 验收脚本公共部分
# 用法（PowerShell 执行策略会拦截 .ps1 直接执行，所以统一用 Invoke-Expression 加载）：
#   Get-Content tests\run-selftest.ps1 -Raw -Encoding UTF8 | Invoke-Expression

$Repo = if ($global:REPO) { $global:REPO } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$global:REPO = $Repo

# 本机沙箱/自动化环境：把「影·筛」数据目录指到工作区（普通用户机器上不需要）
if (-not $env:YING_SHAI_DATA) { $env:YING_SHAI_DATA = Join-Path $Repo '..\toolchain\appdata\影·筛' }

$Exe = (Get-ChildItem (Join-Path $Repo 'src\YingShai.App\bin\Release') -Recurse -Filter '影·筛.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1).FullName
if (-not $Exe) {
    throw "找不到影·筛.exe，请先构建：dotnet build src\YingShai.App\YingShai.App.csproj -c Release"
}

function Invoke-YingShai {
    param([string[]]$Arguments)
    & $Exe @Arguments
    return $LASTEXITCODE
}
