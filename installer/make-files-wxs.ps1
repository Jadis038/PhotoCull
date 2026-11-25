# 把发布目录里所有文件生成为 WiX 组件清单（installer/generated/Files.wxs）。
# 结构：
#   Fragment 1：目录树声明（DirectoryRef -> 嵌套 Directory）
#   Fragment 2：ComponentGroup Id="AppFiles"，每个文件一个 Component（用 Directory 属性指到所属目录）
# 每个文件的 Component GUID 由「相对路径」确定性生成，保证多次构建/升级时组件标识稳定。

function Get-DeterministicGuid([string]$seed) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($seed))
    # 按 RFC 4122 v5 风格修正版本位与变体位，避免与随机 GUID 混淆
    $hash[6] = ($hash[6] -band 0x0F) -bor 0x50
    $hash[8] = ($hash[8] -band 0x3F) -bor 0x80
    $hex = -join ($hash | ForEach-Object { $_.ToString('x2') })
    return ('{' + $hex.Substring(0,8) + '-' + $hex.Substring(8,4) + '-' + $hex.Substring(12,4) + '-' + $hex.Substring(16,4) + '-' + $hex.Substring(20,12) + '}').ToUpper()
}

function Get-SafeId([string]$rel) { return (($rel -replace '[^A-Za-z0-9]', '_')) }

function New-FilesWxs {
    param([string]$DistDir, [string]$OutFile, [string]$RootId = 'YingShaiAppDir')

    $DistDir = (Resolve-Path $DistDir).Path
    $items = Get-ChildItem -LiteralPath $DistDir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($DistDir.Length + 1)
        [pscustomobject]@{
            Full = $_.FullName
            Rel  = $rel
            Dir  = if ([IO.Path]::GetDirectoryName($rel)) { [IO.Path]::GetDirectoryName($rel) } else { '' }
            Name = $_.Name
        }
    } | Sort-Object Rel

    # 收集所有目录（含中间层）
    $dirs = New-Object System.Collections.Generic.List[string]
    $seen = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
    foreach ($it in $items) {
        if (-not $it.Dir) { continue }
        $parts = $it.Dir -split '\\'
        $acc = ''
        foreach ($p in $parts) {
            $acc = if ($acc) { "$acc\$p" } else { $p }
            if ($seen.Add($acc)) { [void]$dirs.Add($acc) }
        }
    }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine('<!-- 由 installer/make-files-wxs.ps1 自动生成，请勿手改 -->')
    [void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')

    # ——— Fragment 1：目录声明 ———
    [void]$sb.AppendLine('  <Fragment>')
    [void]$sb.AppendLine("    <DirectoryRef Id=`"$RootId`">")
    $sortedDirs = $dirs | Sort-Object { ($_ -split '\\').Count }, { $_ }
    $open = New-Object System.Collections.Generic.List[string]
    foreach ($d in $sortedDirs) {
        $depth = ($d -split '\\').Count
        while ($open.Count -ge $depth) {
            [void]$sb.AppendLine("      $(('  ' * ($open.Count - 1)))</Directory>")
            $open.RemoveAt($open.Count - 1)
        }
        $id = 'D_' + (Get-SafeId $d)
        $name = ($d -split '\\')[-1]
        $indent = '      ' + ('  ' * $open.Count)
        [void]$sb.AppendLine("$indent<Directory Id=`"$id`" Name=`"$name`">")
        $open.Add($d)
    }
    while ($open.Count -gt 0) {
        [void]$sb.AppendLine("      $(('  ' * ($open.Count - 1)))</Directory>")
        $open.RemoveAt($open.Count - 1)
    }
    [void]$sb.AppendLine('    </DirectoryRef>')
    [void]$sb.AppendLine('  </Fragment>')

    # ——— Fragment 2：组件 ———
    [void]$sb.AppendLine('  <Fragment>')
    [void]$sb.AppendLine('    <ComponentGroup Id="AppFiles">')
    foreach ($it in $items) {
        $safe = Get-SafeId $it.Rel
        $compId = 'C_' + $safe
        $fileId = 'F_' + $safe
        $dirAttr = if ($it.Dir) { " Directory=`"D_$(Get-SafeId $it.Dir)`"" } else { " Directory=`"$RootId`"" }
        $src = $it.Full -replace '&', '&amp;' -replace '"', '&quot;'
        [void]$sb.AppendLine("      <Component Id=`"$compId`" Guid=`"$(Get-DeterministicGuid $it.Rel)`"$dirAttr>")
        [void]$sb.AppendLine("        <File Id=`"$fileId`" Source=`"$src`" KeyPath=`"yes`" />")
        [void]$sb.AppendLine('      </Component>')
    }
    [void]$sb.AppendLine('    </ComponentGroup>')
    [void]$sb.AppendLine('  </Fragment>')
    [void]$sb.AppendLine('</Wix>')

    New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($OutFile)) | Out-Null
    [IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))
    Write-Host ("  文件清单：{0} 个文件，{1} 个目录 -> {2}" -f $items.Count, $dirs.Count, $OutFile)
}
