[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$BuildDir = "build"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildPath = Join-Path $root $BuildDir

function Invoke-NativeBuild {
    param([string]$CMakeGenerator)

    cmake -S $root -B $buildPath -G $CMakeGenerator -A x64
    cmake --build $buildPath --config $Configuration
}

try {
    Invoke-NativeBuild "Visual Studio 17 2022"
} catch {
    Write-Warning "Visual Studio generator failed, retrying with the default CMake generator: $($_.Exception.Message)"
    if (Test-Path $buildPath) {
        Remove-Item -LiteralPath $buildPath -Recurse -Force
    }
    cmake -S $root -B $buildPath
    cmake --build $buildPath --config $Configuration
}

$dll = Get-ChildItem -Path $buildPath -Recurse -Filter Lossless.dll |
    Where-Object { $_.FullName -match [regex]::Escape($Configuration) -or $_.DirectoryName -eq $buildPath } |
    Select-Object -First 1

if (-not $dll) {
    throw "Build completed but Lossless.dll was not found under $buildPath"
}

Write-Host "Built $($dll.FullName)"
