param(
    [ValidateSet('Debug', 'Release', 'RelWithDebInfo', 'MinSizeRel')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDir = Join-Path $scriptDir 'build'

cmake -S $scriptDir -B $buildDir -A x64
if ($LASTEXITCODE -ne 0) { throw "Bridge CMake configuration failed" }
cmake --build $buildDir --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "Bridge $Configuration build failed" }

$exe = Get-ChildItem -Path $buildDir -Recurse -Filter 'DlssNrBridge.exe' |
    Where-Object { $_.FullName -match "\\$Configuration\\" } |
    Select-Object -First 1

if (-not $exe) {
    throw "DlssNrBridge.exe was not found after the $Configuration build."
}

Write-Host "Built $($exe.FullName)"
