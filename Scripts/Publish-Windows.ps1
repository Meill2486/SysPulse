#requires -Version 7.0
<#
.SYNOPSIS
发布并验证 Windows x64 自包含单文件，输出许可、依赖清单、ZIP 和校验值。输出应指向新的空目录。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [string] $ReleaseTag,
    [string] $CertificateThumbprint,
    [uri] $TimestampUrl
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $projectRoot 'SysPulse.csproj'
$version = ([xml](Get-Content -Raw -LiteralPath $project)).SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) {
    throw "请使用新的空输出目录。$output"
}

if ($ReleaseTag) {
    if ($ReleaseTag -cne "v$version") { throw "标签 $ReleaseTag 与项目版本 $version 不一致。" }
    $revision = git -C $projectRoot rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0) { throw '无法确定发布源码的提交。' }
    $tagRevision = git -C $projectRoot rev-parse --verify "refs/tags/$ReleaseTag^{commit}"
    if ($LASTEXITCODE -ne 0 -or $tagRevision -ne $revision) { throw '发布标签未指向当前提交。' }
    $worktreeChanges = git -C $projectRoot status --porcelain
    if ($LASTEXITCODE -ne 0 -or $worktreeChanges) { throw '标签发布需要干净的工作区和暂存区。' }
}

if ([bool]$CertificateThumbprint -ne [bool]$TimestampUrl) {
    throw '签名时需同时指定证书指纹和 RFC 3161 时间戳服务。'
}
if ($CertificateThumbprint) {
    if ($CertificateThumbprint -notmatch '^[0-9a-fA-F]{40}$' -or $TimestampUrl.Scheme -notin 'https', 'http') {
        throw '证书指纹或时间戳服务地址无效。'
    }
    $signTool = Get-Command signtool.exe -CommandType Application
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('SysPulse-publish-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $temp 'package'
$oldExtract = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$oldTemp = $env:TEMP
$oldTmp = $env:TMP
Push-Location -LiteralPath $projectRoot
try {
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -warnaserror -o $package
    if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE。" }
    $exe = Join-Path $package 'SysPulse.exe'
    if ($ReleaseTag -and (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -cne "$version+$revision") {
        throw 'EXE 内嵌版本与发布标签或源码提交不一致。'
    }
    if ($CertificateThumbprint) {
        & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 $exe
        if ($LASTEXITCODE -ne 0) { throw "代码签名失败，退出码 $LASTEXITCODE。" }
        & $signTool.Source verify /pa /tw $exe
        if ($LASTEXITCODE -ne 0) { throw "签名验证失败，退出码 $LASTEXITCODE。" }
    }

    # 验证的就是待交付文件。自检日志和原生库解包均与正式运行隔离。
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $temp 'bundle'
    $env:TEMP = $env:TMP = $temp
    $check = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru
    Get-Content -LiteralPath (Join-Path $temp 'SysPulse-selftest.log') | Select-Object -First 1 -Last 1
    if ($check.ExitCode -ne 0) { throw "发布包自检失败，退出码 $($check.ExitCode)。" }

    [void](New-Item -ItemType Directory -Path $output -Force)
    Copy-Item -LiteralPath $exe -Destination $output
    foreach ($name in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $output
    }
    foreach ($name in 'Lucide-LICENSE.txt', 'Microsoft-MIT-LICENSE.txt') {
        Copy-Item -LiteralPath (Join-Path $projectRoot "Assets/$name") -Destination $output
    }
    # 归档本次发布实际使用的运行时许可，随 SDK 修补版本自动更新。
    $assets = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'obj/project.assets.json') | ConvertFrom-Json -AsHashtable
    $dependencies = @("SysPulse/$version")
    foreach ($library in ($assets.libraries.GetEnumerator() | Sort-Object Key)) {
        if ($library.Value.type -ne 'package') { continue }
        $locations = $assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $library.Value.path }
        $libraryPath = $locations | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (!$libraryPath) { throw "找不到依赖包目录。$($library.Key)" }
        $spec = Get-ChildItem -LiteralPath $libraryPath -Filter '*.nuspec' -File | Select-Object -First 1
        $license = ([xml](Get-Content -Raw -LiteralPath $spec.FullName)).package.metadata.license
        # 当前依赖均为 Microsoft/.NET 的 MIT 包；未来引入其他许可时先审核正文，不静默套用 MIT。
        if ($license.type -ne 'expression' -or $license.InnerText -ne 'MIT') {
            throw "依赖许可需要人工审核。$($library.Key)"
        }
        $dependencies += "$($library.Key)  MIT"
        $prefix = $library.Key.Replace('/', '-')
        Get-ChildItem -LiteralPath $libraryPath -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output "$prefix-$($_.Name)") }
    }
    $runtimePacks = $assets.project.frameworks.Values.downloadDependencies | Where-Object {
        $_.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$'
    }
    foreach ($runtime in $runtimePacks) {
        $runtimeVersion = $runtime.version.Trim('[', ']').Split(',')[0].Trim()
        $dependencies += "$($runtime.name)/$runtimeVersion  MIT (runtime notices included)"
        $relative = Join-Path $runtime.name.ToLowerInvariant() $runtimeVersion
        $locations = $assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $relative }
        $runtimePath = $locations | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (!$runtimePath) { throw "找不到运行时许可文件目录。$relative" }
        Get-ChildItem -LiteralPath $runtimePath -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output "$($runtime.name)-$($_.BaseName).txt") }
    }
    Set-Content -LiteralPath (Join-Path $output 'DEPENDENCIES.txt') -Encoding utf8 -Value $dependencies
    $zip = Join-Path $output "SysPulse-v$version-win-x64.zip"
    $files = (Get-ChildItem -LiteralPath $output -File).FullName
    Compress-Archive -LiteralPath $files -DestinationPath $zip -CompressionLevel Optimal
    $hashes = foreach ($file in (Join-Path $output 'SysPulse.exe'), $zip) {
        "$((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash)  $([IO.Path]::GetFileName($file))"
    }
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii -Value $hashes
    Write-Output "已验证的包位于 $output"
}
finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $oldExtract
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
    Pop-Location
    if (Test-Path -LiteralPath $temp) {
        $resolvedTemp = (Resolve-Path -LiteralPath $temp).ProviderPath
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
        if ($resolvedTemp.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path $resolvedTemp -Leaf) -match '^SysPulse-publish-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
        }
    }
}
