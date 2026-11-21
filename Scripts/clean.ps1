# ==============================================================================
#  Script: clean.ps1
#  作用: 清理所有构建产物（bin/obj/WebUI dist/publish）。
#  用法: powershell -NoProfile -ExecutionPolicy Bypass -File Scripts/clean.ps1
# ==============================================================================
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $targets = @(
        'Main/bin', 'Main/obj',
        'Tests/BlueRandom.Tests/bin', 'Tests/BlueRandom.Tests/obj',
        'WebUI/dist',
        'publish'
    )
    foreach ($target in $targets) {
        if (Test-Path $target) {
            Remove-Item $target -Recurse -Force
            Write-Host "[clean] 已删除 $target" -ForegroundColor DarkGray
        }
    }
    Write-Host '[clean] 完成' -ForegroundColor Green
}
finally {
    Pop-Location
}
