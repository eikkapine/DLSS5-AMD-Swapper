param(
    [string]$LsDir,
    [string]$ProxyVersionSource,
    [string]$NrSource,
    [string]$HipVisibleDevices = "1",
    [int]$Width = 1280,
    [int]$Height = 720,
    [int]$NativeResolution = 0,
    [int]$ReadyTimeoutMs = 180000,
    [string]$BuiltAutoScaleDllPath,
    [string]$BridgeExe,
    [string]$InstallerPath = (Join-Path $PSScriptRoot "Install-AutoScale.ps1"),
    [switch]$ValidateOnly,
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"

function Resolve-ExistingPathOrNull {
    param([string]$Path)
    if ($Path -and (Test-Path -LiteralPath $Path)) { return (Resolve-Path -LiteralPath $Path).Path }
    return $null
}

function Get-ScriptRootParent {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Find-DefaultAutoScaleDll {
    $autoScaleRoot = Get-ScriptRootParent
    $candidates = @(
        (Join-Path $autoScaleRoot "bin\Lossless.dll"),
        (Join-Path $autoScaleRoot "build\Release\Lossless.dll")
    )
    foreach ($candidate in $candidates) {
        $resolved = Resolve-ExistingPathOrNull $candidate
        if ($resolved) { return $resolved }
    }
    return $null
}

function Find-DefaultBridgeExe {
    $autoScaleRoot = Get-ScriptRootParent
    $candidates = @(
        (Join-Path $autoScaleRoot "bin\DlssNrBridge.exe"),
        (Join-Path $autoScaleRoot "..\bridge\build\Release\DlssNrBridge.exe")
    )
    foreach ($candidate in $candidates) {
        $resolved = Resolve-ExistingPathOrNull $candidate
        if ($resolved) { return $resolved }
    }
    return $null
}

function Find-SteamLibraries {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in @('HKCU:\Software\Valve\Steam','HKLM:\SOFTWARE\WOW6432Node\Valve\Steam','HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $steamPath = (Get-ItemProperty -Path $key -ErrorAction Stop).SteamPath
            if ($steamPath -and (Test-Path -LiteralPath $steamPath)) { $roots.Add((Resolve-Path -LiteralPath $steamPath).Path) }
        } catch {}
    }
    foreach ($root in @($roots.ToArray())) {
        $libraryFile = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $libraryFile) {
            $text = Get-Content -LiteralPath $libraryFile -Raw
            foreach ($match in [regex]::Matches($text, '"path"\s+"([^\"]+)"')) {
                $path = $match.Groups[1].Value.Replace('\\','\')
                if (Test-Path -LiteralPath $path) { $roots.Add((Resolve-Path -LiteralPath $path).Path) }
            }
        }
    }
    return @($roots.ToArray() | Select-Object -Unique)
}

function Find-LosslessScalingInstall {
    $candidates = New-Object System.Collections.Generic.List[string]
    $repoInstall = Resolve-ExistingPathOrNull (Join-Path $PSScriptRoot "..\..\..")
    if ($repoInstall) { $candidates.Add($repoInstall) }
    foreach ($library in Find-SteamLibraries) {
        $candidates.Add((Join-Path $library 'steamapps\common\Lossless Scaling'))
    }
    foreach ($candidate in @($candidates.ToArray() | Select-Object -Unique)) {
        if ((Test-Path -LiteralPath (Join-Path $candidate 'Lossless.dll')) -and (Test-Path -LiteralPath (Join-Path $candidate 'LosslessScaling.exe'))) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
        if (Test-Path -LiteralPath (Join-Path $candidate 'Lossless.dll')) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    return $null
}

function Pick-Folder {
    param([string]$Description, [string]$InitialDirectory)
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = $Description
    if ($InitialDirectory -and (Test-Path -LiteralPath $InitialDirectory)) { $dialog.SelectedPath = $InitialDirectory }
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw "No folder selected." }
    return $dialog.SelectedPath
}

function Pick-File {
    param([string]$Title, [string]$Filter)
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = $Title
    $dialog.Filter = $Filter
    $dialog.CheckFileExists = $true
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw "No file selected for $Title." }
    return $dialog.FileName
}

function Prompt-HipVisibleDevices {
    param([string]$DefaultValue)
    Add-Type -AssemblyName Microsoft.VisualBasic
    $message = "HIP_VISIBLE_DEVICES is machine-specific because HIP device indexes depend on GPU enumeration. 0 is typical on a single-GPU system; choose the AMD GPU index for your machine."
    return [Microsoft.VisualBasic.Interaction]::InputBox($message, "NR Auto Scale HIP device selection", $DefaultValue)
}

function Require-ExistingFile {
    param([string]$Path, [string]$Name)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing ${Name}: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Require-FileName {
    param([string]$Path, [string]$ExpectedName, [string]$Name)
    $actualName = [System.IO.Path]::GetFileName($Path)
    if (!$actualName.Equals($ExpectedName, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must be named $ExpectedName. Selected: $Path"
    }
}

function Require-ExistingDir {
    param([string]$Path, [string]$Name)
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { throw "Missing ${Name}: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path
}

if (!$BuiltAutoScaleDllPath) { $BuiltAutoScaleDllPath = Find-DefaultAutoScaleDll }
if (!$BridgeExe) { $BridgeExe = Find-DefaultBridgeExe }
if (!$LsDir) { $LsDir = Find-LosslessScalingInstall }

if (!$NonInteractive) {
    if (!$LsDir) { $LsDir = Pick-Folder -Description "Select your Lossless Scaling install folder." -InitialDirectory $null }
    if (!$ProxyVersionSource) { $ProxyVersionSource = Pick-File -Title "Select the AMD DLSS-NR proxy version.dll" -Filter "version.dll|version.dll|DLL files (*.dll)|*.dll|All files (*.*)|*.*" }
    if (!$NrSource) { $NrSource = Pick-File -Title "Select NVIDIA nvngx_dlssnr.dll" -Filter "nvngx_dlssnr.dll|nvngx_dlssnr.dll|DLL files (*.dll)|*.dll|All files (*.*)|*.*" }
    $HipVisibleDevices = Prompt-HipVisibleDevices -DefaultValue $HipVisibleDevices
} elseif (!$LsDir -or !$ProxyVersionSource -or !$NrSource) {
    throw "Non-interactive setup requires -LsDir, -ProxyVersionSource, and -NrSource."
}

$lsDirPath = Require-ExistingDir -Path $LsDir -Name "Lossless Scaling install folder"
$proxyPath = Require-ExistingFile -Path $ProxyVersionSource -Name "AMD proxy version.dll"
$nrPath = Require-ExistingFile -Path $NrSource -Name "nvngx_dlssnr.dll"
$wrapperPath = Require-ExistingFile -Path $BuiltAutoScaleDllPath -Name "bundled auto-scale Lossless.dll"
$bridgePath = Require-ExistingFile -Path $BridgeExe -Name "bundled DlssNrBridge.exe"
$installer = Require-ExistingFile -Path $InstallerPath -Name "Install-AutoScale.ps1"
Require-FileName -Path $proxyPath -ExpectedName "version.dll" -Name "AMD proxy"
Require-FileName -Path $nrPath -ExpectedName "nvngx_dlssnr.dll" -Name "NVIDIA DLSS-NR DLL"
Require-FileName -Path $wrapperPath -ExpectedName "Lossless.dll" -Name "auto-scale wrapper"
Require-FileName -Path $bridgePath -ExpectedName "DlssNrBridge.exe" -Name "bridge executable"

if ($ValidateOnly) {
    Write-Host "Setup validation passed. No files were modified."
    Write-Host "Lossless Scaling: $lsDirPath"
    Write-Host "Auto-scale wrapper: $wrapperPath"
    Write-Host "Bridge: $bridgePath"
    Write-Host "AMD proxy version.dll: $proxyPath"
    Write-Host "NVIDIA nvngx_dlssnr.dll: $nrPath"
    Write-Host "HIP_VISIBLE_DEVICES=$HipVisibleDevices (machine-specific)"
    Write-Host "NativeResolution=$NativeResolution; fixed processing bounds=${Width}x${Height} (ignored when NativeResolution=1)."
    Write-Host "ReadyTimeoutMs=$ReadyTimeoutMs"
    exit 0
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer `
    -LsDir $lsDirPath `
    -ProxyVersionSource $proxyPath `
    -NrSource $nrPath `
    -HipVisibleDevices $HipVisibleDevices `
    -Width $Width `
    -Height $Height `
    -NativeResolution $NativeResolution `
    -ReadyTimeoutMs $ReadyTimeoutMs `
    -BuiltAutoScaleDllPath $wrapperPath `
    -BridgeExe $bridgePath
exit $LASTEXITCODE
