param(
    [string]$Wrapper = (Join-Path $PSScriptRoot '..\build\Release\Lossless.dll'),
    [string]$BuildDir = (Join-Path $PSScriptRoot 'build'),
    [string]$RunRoot = (Join-Path $PSScriptRoot 'runs')
)

$ErrorActionPreference = 'Stop'
cmake -S $PSScriptRoot -B $BuildDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Auto-scale test CMake configuration failed" }
cmake --build $BuildDir --config Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Auto-scale test build failed" }
New-Item -ItemType Directory -Force -Path $RunRoot | Out-Null
Remove-Item -LiteralPath (Join-Path $RunRoot 'harness-results.txt') -Force -ErrorAction SilentlyContinue
$harness = Join-Path $BuildDir 'bin\Release\AutoScaleHarness.exe'
foreach ($name in @('SpecialK.deny.AutoScaleHarness', 'SpecialK.deny.AutoScaleHarness.exe')) {
    Set-Content -LiteralPath (Join-Path (Split-Path $harness) $name) -Value ''
}
& $harness --wrapper $Wrapper --run-root $RunRoot
if ($LASTEXITCODE -ne 0) { throw "AutoScaleHarness failed with exit code $LASTEXITCODE" }
Get-Content -LiteralPath (Join-Path $RunRoot 'harness-results.txt')
& (Join-Path $PSScriptRoot 'Test-InstallDefaults.ps1') -Wrapper $Wrapper -TestBin (Split-Path $harness) -RunRoot (Join-Path $RunRoot ('installer-' + [Guid]::NewGuid().ToString('n')))
