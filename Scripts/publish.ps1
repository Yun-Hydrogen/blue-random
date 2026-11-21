# ==============================================================================
#  Script: publish.ps1
#  作用: 构建 WebUI 并发布单文件独立版可执行程序 (win-x64)。
#  用法: powershell -NoProfile -ExecutionPolicy Bypass -File Scripts/publish.ps1 [-Output publish] [-SkipWebUI]
# ==============================================================================
[CmdletBinding()]
param(
    [string]$Output = 'publish',
    [switch]$SkipWebUI
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $SkipWebUI) {
        Write-Host '[publish] 构建 WebUI (Vite)...' -ForegroundColor Cyan
        npm --prefix WebUI run build
        if ($LASTEXITCODE -ne 0) { throw "WebUI 构建失败 (exit $LASTEXITCODE)" }
    }

    Write-Host "[publish] 发布单文件独立版 -> $Output ..." -ForegroundColor Cyan
    dotnet publish Main/BlueRandom.csproj -c Release -r win-x64 --self-contained true -o $Output
    if ($LASTEXITCODE -ne 0) { throw "发布失败 (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}
