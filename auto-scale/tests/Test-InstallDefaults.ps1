param(
    [Parameter(Mandatory)][string]$Wrapper,
    [Parameter(Mandatory)][string]$TestBin,
    [Parameter(Mandatory)][string]$RunRoot
)
$ErrorActionPreference = 'Stop'
$scriptRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\scripts'))
$Wrapper = (Resolve-Path -LiteralPath $Wrapper).Path
$TestBin = (Resolve-Path -LiteralPath $TestBin).Path
$RunRoot = [IO.Path]::GetFullPath($RunRoot)
if (Test-Path -LiteralPath $RunRoot) { throw "Use a fresh installer-test directory: $RunRoot" }
New-Item -ItemType Directory -Path $RunRoot | Out-Null

# Only project-built stub PE files are used. No real app or vendor runtime is
# installed, executed, or needed by these setup/default-forwarding checks.
$cases = @(
    @{ Name = 'installer-default'; Script = 'Install-AutoScale.ps1'; Width = 1280; Height = 720; Native = 0; Extra = @() },
    @{ Name = 'setup-default'; Script = 'Setup.ps1'; Width = 1280; Height = 720; Native = 0; Extra = @('-NonInteractive') },
    @{ Name = 'setup-custom'; Script = 'Setup.ps1'; Width = 1600; Height = 900; Native = 0; Extra = @('-NonInteractive', '-Width', '1600', '-Height', '900') },
    @{ Name = 'setup-native'; Script = 'Setup.ps1'; Width = 1280; Height = 720; Native = 1; Extra = @('-NonInteractive', '-NativeResolution', '1') }
)
foreach ($case in $cases) {
    $caseRoot = Join-Path $RunRoot $case.Name
    $lsPath = Join-Path $caseRoot 'app'
    $assetsPath = Join-Path $caseRoot 'stub-assets'
    New-Item -ItemType Directory -Path $lsPath, $assetsPath | Out-Null
    $original = Join-Path $TestBin 'Lossless_original.dll'
    Copy-Item -LiteralPath $original -Destination (Join-Path $lsPath 'Lossless.dll')
    Copy-Item -LiteralPath $original -Destination (Join-Path $assetsPath 'version.dll')
    Copy-Item -LiteralPath $original -Destination (Join-Path $assetsPath 'nvngx_dlssnr.dll')
    Copy-Item -LiteralPath (Join-Path $TestBin 'FakeBridge.exe') -Destination (Join-Path $assetsPath 'DlssNrBridge.exe')
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $scriptRoot $case.Script),
        '-LsDir', $lsPath,
        '-ProxyVersionSource', (Join-Path $assetsPath 'version.dll'),
        '-NrSource', (Join-Path $assetsPath 'nvngx_dlssnr.dll'),
        '-BuiltAutoScaleDllPath', $Wrapper,
        '-BridgeExe', (Join-Path $assetsPath 'DlssNrBridge.exe')
    ) + $case.Extra
    & powershell.exe @arguments *> (Join-Path $caseRoot 'setup.log')
    if ($LASTEXITCODE -ne 0) { throw "$($case.Name) failed; inspect $caseRoot\setup.log" }
    $ini = Get-Content -LiteralPath (Join-Path $lsPath 'NrAutoScale.ini') -Raw
    foreach ($expected in @("Width=$($case.Width)", "Height=$($case.Height)", "NativeResolution=$($case.Native)", 'DefaultScalingTypeIfOff=1')) {
        if ($ini -notmatch ('(?m)^' + [regex]::Escape($expected) + '\r?$')) {
            throw "$($case.Name) missing expected configuration: $expected"
        }
    }
    if ((Get-FileHash (Join-Path $lsPath 'Lossless_original.dll')).Hash -ne (Get-FileHash $original).Hash) {
        throw "$($case.Name) did not preserve the original DLL"
    }
    "PASS $($case.Name)" | Tee-Object -FilePath (Join-Path $RunRoot 'results.txt') -Append
}
