# ==============================================================================
#  Script: build.ps1
#  作用: 全量构建（WebUI 前端 + .NET 后端）。
#  用法: powershell -NoProfile -ExecutionPolicy Bypass -File Scripts/build.ps1 [-SkipWebUI] [-Configuration Debug]
# ==============================================================================
[CmdletBinding()]
param(
    [switch]$SkipWebUI,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $SkipWebUI) {
        Write-Host '[build] 构建 WebUI (Vite)...' -ForegroundColor Cyan
        npm --prefix WebUI run build
        if ($LASTEXITCODE -ne 0) { throw "WebUI 构建失败 (exit $LASTEXITCODE)" }
    }

    Write-Host "[build] 构建后端 Main/BlueRandom.csproj ($Configuration)..." -ForegroundColor Cyan
    dotnet build Main/BlueRandom.csproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "后端构建失败 (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}
