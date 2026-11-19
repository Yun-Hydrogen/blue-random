# ==============================================================================
#  Script: test.ps1
#  作用: 构建 WebUI（内嵌资源依赖）并运行 xUnit 单元测试。
#  用法: powershell -NoProfile -ExecutionPolicy Bypass -File Scripts/test.ps1 [-Configuration Debug] [-SkipWebUI]
# ==============================================================================
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipWebUI
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $SkipWebUI) {
        Write-Host '[test] 构建 WebUI (Vite)...' -ForegroundColor Cyan
        npm --prefix WebUI run build
        if ($LASTEXITCODE -ne 0) { throw "WebUI 构建失败 (exit $LASTEXITCODE)" }
    }

    Write-Host "[test] 运行单元测试 ($Configuration)..." -ForegroundColor Cyan
    dotnet test Tests/BlueRandom.Tests/BlueRandom.Tests.csproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "单元测试失败 (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}
