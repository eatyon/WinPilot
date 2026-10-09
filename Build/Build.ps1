param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipDotNetBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

foreach ($command in @('dotnet', 'node', 'npm.cmd')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "缺少构建工具：$command，请参阅 Docs/REPOSITORY_STRUCTURE.md"
    }
}

$nodeVersionText = & node --version
if ($LASTEXITCODE -ne 0) { throw '无法读取 Node.js 版本' }
$nodeVersion = [version]$nodeVersionText.Trim().TrimStart('v')
if ($nodeVersion -lt [version]'22.12.0') {
    throw "Node.js 版本过低：$nodeVersion，需要 22.12 或更新版本"
}
$sdkVersionText = & dotnet --version
if ($LASTEXITCODE -ne 0) { throw '无法读取 .NET SDK 版本' }
$sdkVersion = [version]$sdkVersionText.Trim().Split('-')[0]
if ($sdkVersion.Major -lt 10) {
    throw ".NET SDK 版本过低：$sdkVersion，需要 10 或更新版本"
}

Push-Location (Join-Path $repo 'WinPilot.Web')
try {
    & npm.cmd ci
    if ($LASTEXITCODE -ne 0) { throw "前端依赖安装失败，退出代码：$LASTEXITCODE" }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw "前端构建失败，退出代码：$LASTEXITCODE" }
} finally {
    Pop-Location
}

if ($SkipDotNetBuild) { return }

Push-Location $repo
try {
    dotnet build .\WinPilot.sln -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "WinPilot 构建失败，退出代码：$LASTEXITCODE" }
} finally {
    Pop-Location
}
