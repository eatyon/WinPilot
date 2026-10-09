param(
    [string]$Version = 'dev',
    [switch]$ValidateVersionOnly
)

$ErrorActionPreference = 'Stop'
$versionArguments = @()
if ($Version -ne 'dev' -or $ValidateVersionOnly) {
    $identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
    $pattern = '^v?(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?<prerelease>' + $identifier + '(?:\.' + $identifier + ')*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)$'
    $match = [regex]::Match($Version, $pattern)
    if (-not $match.Success) { throw '版本号必须符合 SemVer，例如 v1.0.0 或 v1.0.0-beta.1' }
    $assemblyVersion = $match.Groups['version'].Value
    $Version = "v$assemblyVersion"
    if ($ValidateVersionOnly) {
        return [pscustomobject]@{
            Tag = $Version
            Version = $assemblyVersion
            Prerelease = $match.Groups['prerelease'].Success
        }
    }
    $versionArguments = @("-p:MinVerVersionOverride=$assemblyVersion", "-p:Version=$assemblyVersion")
}
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
$output = Join-Path $artifacts 'portable\WinPilot'
$staging = Join-Path $artifacts ".portable-staging-$([Guid]::NewGuid().ToString('N'))"

function Assert-ArtifactPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "发布路径超出 artifacts 目录：$resolved"
    }
    $cursor = $resolved
    while ($cursor -and $cursor.Length -ge $artifacts.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "发布路径不能包含符号链接或目录联接：$cursor"
            }
        }
        $cursor = Split-Path -Parent $cursor
    }
}

Assert-ArtifactPath $output
Assert-ArtifactPath $staging
foreach ($dataDirectory in @('User', 'logs')) {
    if (Test-Path -LiteralPath (Join-Path $output $dataDirectory)) {
        throw "发布目录中存在运行数据 $dataDirectory，请移走已有运行目录后重新发布"
    }
}

& (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release -SkipDotNetBuild

try {
    $mainOutput = Join-Path $staging 'main'
    $updaterOutput = Join-Path $staging 'updater'
    $package = Join-Path $staging 'WinPilot'
    dotnet publish (Join-Path $repo 'WinPilot\WinPilot.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true @versionArguments -o $mainOutput
    if ($LASTEXITCODE -ne 0) { throw "WinPilot 发布失败，退出代码：$LASTEXITCODE" }
    dotnet publish (Join-Path $repo 'WinPilot.Updater\WinPilot.Updater.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true @versionArguments -o $updaterOutput
    if ($LASTEXITCODE -ne 0) { throw "WinPilot.Updater 发布失败，退出代码：$LASTEXITCODE" }

    New-Item -ItemType Directory -Path $package -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $mainOutput 'WinPilot.exe') -Destination $package
    Copy-Item -LiteralPath (Join-Path $updaterOutput 'WinPilot.Updater.exe') -Destination $package
    Copy-Item -LiteralPath (Join-Path $mainOutput 'wwwroot') -Destination $package -Recurse

    Assert-ArtifactPath $output
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
    Assert-ArtifactPath $package
    Move-Item -LiteralPath $package -Destination $output
    $packageName = "WinPilot-$Version-win-x64.zip"
    $archive = Join-Path $artifacts $packageName
    Compress-Archive -LiteralPath $output -DestinationPath $archive -Force
    Write-Output "发布目录：$output"
    Write-Output "发布包：$archive"
} finally {
    Assert-ArtifactPath $staging
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
