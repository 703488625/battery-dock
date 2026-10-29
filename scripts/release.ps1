#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Version = "v1.1.0",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build.ps1")
    if ($LASTEXITCODE -ne 0) { throw "构建失败，没有生成发布包。" }
}

$exe = Join-Path $root "dist\BatteryDock.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "找不到 $exe，请先运行 scripts\build.ps1。"
}

if (-not $Version.StartsWith("v")) { $Version = "v$Version" }

$releaseDir = Join-Path $root "release"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$zip = Join-Path $releaseDir "BatteryDock-$Version-win-x64.zip"

$files = @(
    $exe,
    (Join-Path $root "README.md"),
    (Join-Path $root "使用说明.txt"),
    (Join-Path $root "LICENSE"),
    (Join-Path $root "THIRD-PARTY-NOTICES.md"),
    (Join-Path $root "CHANGELOG.md")
)

Compress-Archive -Path $files -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$sumPath = "$zip.sha256"
[IO.File]::WriteAllText(
    $sumPath,
    "$hash  $([IO.Path]::GetFileName($zip))`n",
    [Text.UTF8Encoding]::new($false)
)

Write-Host ""
Write-Host "已生成发布包: $zip"
Write-Host "SHA-256: $hash"
Write-Host "校验文件: $sumPath"
