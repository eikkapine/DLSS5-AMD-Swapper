[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'RelWithDebInfo', 'MinSizeRel')]
    [string]$Configuration = "Release",
    [string]$BuildDir = "build",
    [string]$ResultFile
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildPath = [IO.Path]::GetFullPath((Join-Path $root $BuildDir))
$rootPrefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
if (-not $buildPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "BuildDir must name a directory inside $root"
}

function Invoke-NativeBuild {
    param([string]$CMakeGenerator)

    cmake -S $root -B $buildPath -G $CMakeGenerator -A x64
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed (exit $LASTEXITCODE)." }
    cmake --build $buildPath --config $Configuration
    if ($LASTEXITCODE -ne 0) { throw "CMake build failed (exit $LASTEXITCODE)." }
}

try {
    Invoke-NativeBuild "Visual Studio 17 2022"
} catch {
    Write-Warning "Visual Studio generator failed, retrying with the default CMake generator: $($_.Exception.Message)"
    # Keep the failed build and its evidence; a different generator needs a fresh cache.
    $buildPath += '-fallback-' + [Guid]::NewGuid().ToString('N')
    cmake -S $root -B $buildPath "-DCMAKE_BUILD_TYPE=$Configuration"
    if ($LASTEXITCODE -ne 0) { throw "CMake fallback configure failed (exit $LASTEXITCODE)." }
    cmake --build $buildPath --config $Configuration
    if ($LASTEXITCODE -ne 0) { throw "CMake fallback build failed (exit $LASTEXITCODE)." }
}

$dll = Get-ChildItem -Path $buildPath -Recurse -Filter Lossless.dll |
    Where-Object { $_.FullName -match [regex]::Escape($Configuration) -or $_.DirectoryName -eq $buildPath } |
    Select-Object -First 1

if (-not $dll) {
    throw "Build completed but Lossless.dll was not found under $buildPath"
}

Write-Host "Built $($dll.FullName)"
if ($ResultFile) {
    Set-Content -LiteralPath $ResultFile -Value $dll.FullName -Encoding UTF8
}
