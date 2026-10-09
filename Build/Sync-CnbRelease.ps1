#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$GitHubRepository,
    [Parameter(Mandatory)][string]$CnbRepository
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN) -or [string]::IsNullOrWhiteSpace($env:CNB_TOKEN)) {
    throw '请配置 GITHUB_TOKEN 和 CNB_TOKEN'
}
foreach ($repository in @($GitHubRepository, $CnbRepository)) {
    if ($repository -notmatch '^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)+$' -or $repository.Split('/') -contains '..') {
        throw '仓库路径必须为组织/仓库，不能使用 URL'
    }
}
$version = & (Join-Path $PSScriptRoot 'Publish-Portable.ps1') -Version $Tag -ValidateVersionOnly
if ($version.Tag -cne $Tag) { throw 'Release 标签必须使用 v 开头的规范版本号' }

$githubHeaders = @{
    Authorization = "Bearer $env:GITHUB_TOKEN"
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'WinPilot-Release-Sync'
    'X-GitHub-Api-Version' = '2022-11-28'
}
$cnbHeaders = @{
    Authorization = "Bearer $env:CNB_TOKEN"
    Accept = 'application/json'
    'User-Agent' = 'WinPilot-Release-Sync'
}

function Invoke-ReleaseApi {
    param([string]$Uri, [hashtable]$Headers, [string]$Method = 'Get', [hashtable]$Body, [switch]$AllowNotFound)
    $arguments = @{ Uri = $Uri; Headers = $Headers; Method = $Method; TimeoutSec = 60; MaximumRedirection = 0 }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json; charset=utf-8'
        $arguments.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 10 -Compress))
    }
    try { Invoke-RestMethod @arguments }
    catch {
        $response = $_.Exception.PSObject.Properties['Response']
        $status = if ($null -ne $response -and $null -ne $response.Value) { [int]$response.Value.StatusCode } else { 0 }
        if ($AllowNotFound -and $status -eq 404) { return $null }
        throw "发布 API 请求失败，HTTP 状态：$status"
    }
}

$encodedTag = [Uri]::EscapeDataString($Tag)
$githubBase = "https://api.github.com/repos/$GitHubRepository"
$cnbBase = "https://api.cnb.cool/$CnbRepository/-"
$release = Invoke-ReleaseApi "$githubBase/releases/tags/$encodedTag" $githubHeaders
if ($release.draft) { throw '只能同步已公开的 GitHub Release' }
if ($release.tag_name -cne $Tag) { throw 'GitHub Release 标签不匹配' }

$packageName = "WinPilot-$Tag-win-x64.zip"
$packageAssets = @($release.assets | Where-Object { $_.name -ceq $packageName })
if ($packageAssets.Count -ne 1) { throw "GitHub Release 缺少唯一附件：$packageName" }
$packageAsset = $packageAssets[0]
$digest = $packageAsset.PSObject.Properties['digest']
if ($null -eq $digest -or [string]$digest.Value -cnotmatch '^sha256:([0-9a-fA-F]{64})$') {
    throw 'GitHub 发布包缺少有效的 SHA-256 摘要，未同步到 CNB'
}
$packageHash = $Matches[1].ToLowerInvariant()

$sourceCommit = Invoke-ReleaseApi "$githubBase/commits/$encodedTag" $githubHeaders
$cnbTag = Invoke-ReleaseApi "$cnbBase/git/tags/$encodedTag" $cnbHeaders -AllowNotFound
if ($null -eq $cnbTag) { throw 'CNB 缺少对应标签，请先同步源码和标签' }
if ([string]::IsNullOrWhiteSpace($sourceCommit.sha) -or $cnbTag.commit.sha -cne $sourceCommit.sha) {
    throw 'CNB 标签与 GitHub 提交不一致，请先同步源码和标签'
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) "WinPilot-cnb-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $downloadHeaders = $githubHeaders.Clone()
    $downloadHeaders.Accept = 'application/octet-stream'
    $packagePath = Join-Path $temporary $packageName
    try {
        Invoke-WebRequest -Uri "$githubBase/releases/assets/$($packageAsset.id)" -Headers $downloadHeaders -OutFile $packagePath -TimeoutSec 600 | Out-Null
    }
    catch { throw "GitHub 附件下载失败：$packageName" }
    $hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $packageHash) { throw '发布包 SHA-256 校验失败，未上传到 CNB' }

    $target = Invoke-ReleaseApi "$cnbBase/releases/tags/$encodedTag" $cnbHeaders -AllowNotFound
    if ($null -eq $target) {
        $target = Invoke-ReleaseApi "$cnbBase/releases" $cnbHeaders -Method Post -Body @{
            tag_name = $Tag
            target_commitish = $sourceCommit.sha
            name = $release.name
            body = $release.body
            draft = $true
            prerelease = [bool]$release.prerelease
            make_latest = 'false'
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$target.id)) { throw 'CNB 未返回 Release ID' }
    $targetUrl = "$cnbBase/releases/$($target.id)"
    $upload = Invoke-ReleaseApi "$targetUrl/asset-upload-url" $cnbHeaders -Method Post -Body @{
        asset_name = $packageName
        size = (Get-Item -LiteralPath $packagePath).Length
        overwrite = $true
        ttl = 0
    }
    $uploadUri = $null
    $verifyUri = $null
    if (![Uri]::TryCreate($upload.upload_url, [UriKind]::Absolute, [ref]$uploadUri) -or $uploadUri.Scheme -ne 'https' -or
        ![Uri]::TryCreate($upload.verify_url, [UriKind]::Absolute, [ref]$verifyUri) -or
        !$verifyUri.AbsoluteUri.StartsWith("$targetUrl/asset-upload-confirmation/", [StringComparison]::Ordinal)) {
        throw 'CNB 返回了无效的附件上传地址'
    }
    try {
        Invoke-WebRequest -Uri $uploadUri -Method Put -InFile $packagePath -ContentType 'application/octet-stream' -TimeoutSec 600 -MaximumRedirection 0 | Out-Null
    }
    catch { throw "CNB 附件上传失败：$packageName" }
    Invoke-ReleaseApi $verifyUri.AbsoluteUri $cnbHeaders -Method Post | Out-Null
    Write-Output "附件已上传并确认：$packageName"

    Invoke-ReleaseApi $targetUrl $cnbHeaders -Method Patch -Body @{
        name = $release.name
        body = $release.body
        draft = $false
        prerelease = [bool]$release.prerelease
        make_latest = if ($release.prerelease) { 'false' } else { 'legacy' }
    } | Out-Null
    Write-Output "已同步：$Tag -> $CnbRepository"
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^WinPilot-cnb-[0-9a-f]{32}$') { throw '同步临时目录路径无效' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
