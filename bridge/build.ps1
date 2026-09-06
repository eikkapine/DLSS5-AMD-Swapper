param(
    [ValidateSet('Debug', 'Release', 'RelWithDebInfo', 'MinSizeRel')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDir = Join-Path $scriptDir 'build'

cmake -S $scriptDir -B $buildDir -A x64
cmake --build $buildDir --config $Configuration

$exe = Get-ChildItem -Path $buildDir -Recurse -Filter 'DlssNrBridge.exe' |
    Where-Object { $_.FullName -match "\\$Configuration\\" } |
    Select-Object -First 1

if (-not $exe) {
    throw "DlssNrBridge.exe was not found after the $Configuration build."
}

Write-Host "Built $($exe.FullName)"
