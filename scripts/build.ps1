#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Output = "dist",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\BatteryDock\BatteryDock.csproj"
$outPath = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 dotnet。请安装 .NET 8 SDK，或把 SDK 目录加入 PATH。"
}

Push-Location $root
try {
    if (-not $NoRestore) {
        & dotnet restore $project
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
    }

    & dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $outPath

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败。" }

    $exe = Join-Path $outPath "BatteryDock.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "发布完成但没有找到 $exe。"
    }

    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumPath = "$exe.sha256"
    [IO.File]::WriteAllText(
        $sumPath,
        "$hash  BatteryDock.exe`n",
        [Text.UTF8Encoding]::new($false)
    )

    Write-Host ""
    Write-Host "已生成: $exe"
    Write-Host "SHA-256: $hash"
    Write-Host "校验文件: $sumPath"
}
finally {
    Pop-Location
}
