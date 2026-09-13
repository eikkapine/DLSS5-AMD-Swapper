param(
    [string]$Package,
    [string]$Weights,
    [string]$OfficialSetup,
    [string]$NeuralDll,
    [switch]$OfficialOnly,
    [switch]$RequireNeural,
    [string]$OutputDirectory,
    [string]$VcVars
)
$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$buildRoot = Join-Path $testRoot 'build-msvc'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $testRoot ('runs\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')) }
if (!$VcVars) {
    $candidates = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
    )
    $VcVars = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$VcVars) { throw 'MSVC x64 tools were not found; pass -VcVars with vcvars64.bat.' }
if ([bool]$Package -ne [bool]$Weights) { throw 'Pass both -Package and -Weights, or neither for rendering-only checks.' }
if ([bool]$OfficialSetup -ne [bool]$NeuralDll) { throw 'Pass both -OfficialSetup and -NeuralDll, or neither.' }
if ($OfficialSetup -and !$Package) { throw 'Official checks also require -Package for its real FFX runtime dependencies.' }
if ($OfficialOnly -and !$OfficialSetup) { throw '-OfficialOnly requires -OfficialSetup and -NeuralDll.' }
# Only build commands cross into cmd for the installed MSVC environment. All file
# operations stay in PowerShell/.NET; the driver rejects nonempty run folders.
& cmd.exe /d /c "`"$VcVars`" && cmake -S `"$testRoot`" -B `"$buildRoot`" -G `"NMake Makefiles`" -DCMAKE_BUILD_TYPE=Release && cmake --build `"$buildRoot`""
if ($LASTEXITCODE -ne 0) { throw "Native build failed ($LASTEXITCODE)." }
$project = Join-Path $testRoot 'Swapper.RuntimeTests\Swapper.RuntimeTests.csproj'
& dotnet build $project -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Installer test driver build failed ($LASTEXITCODE)." }
$driver = Join-Path $testRoot 'Swapper.RuntimeTests\bin\Release\net8.0-windows\win-x64\Swapper.RuntimeTests.exe'
$testArguments = @('--binaries', $buildRoot, '--out', [IO.Path]::GetFullPath($OutputDirectory))
if ($Package) { $testArguments += @('--package', [IO.Path]::GetFullPath($Package), '--weights', [IO.Path]::GetFullPath($Weights)) }
if ($OfficialSetup) { $testArguments += @('--setup', [IO.Path]::GetFullPath($OfficialSetup), '--nr', [IO.Path]::GetFullPath($NeuralDll)) }
if ($OfficialOnly) { $testArguments += @('--official-only', 'true') }
if ($RequireNeural) { $testArguments += @('--require-neural', 'true') }
& $driver @testArguments
if ($LASTEXITCODE -ne 0) { throw "Runtime checks failed. Read $OutputDirectory\summary.json." }
