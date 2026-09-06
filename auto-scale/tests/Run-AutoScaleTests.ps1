param(
    [string]$Wrapper = (Join-Path $PSScriptRoot '..\build\Release\Lossless.dll'),
    [string]$BuildDir = (Join-Path $PSScriptRoot 'build'),
    [string]$RunRoot = (Join-Path $PSScriptRoot 'runs')
)

$ErrorActionPreference = 'Stop'
cmake -S $PSScriptRoot -B $BuildDir | Out-Host
cmake --build $BuildDir --config Release | Out-Host
New-Item -ItemType Directory -Force -Path $RunRoot | Out-Null
Remove-Item -LiteralPath (Join-Path $RunRoot 'harness-results.txt') -Force -ErrorAction SilentlyContinue
$harness = Join-Path $BuildDir 'bin\Release\AutoScaleHarness.exe'
& $harness --wrapper $Wrapper --run-root $RunRoot
if ($LASTEXITCODE -ne 0) { throw "AutoScaleHarness failed with exit code $LASTEXITCODE" }
Get-Content -LiteralPath (Join-Path $RunRoot 'harness-results.txt')
