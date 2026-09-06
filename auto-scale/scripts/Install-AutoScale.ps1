param(
    [string]$LsDir = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..\..")).Path,
    [Parameter(Mandatory = $true)][string]$ProxyVersionSource,
    [Parameter(Mandatory = $true)][string]$NrSource,
    [string]$HipVisibleDevices = "1",
    [int]$Width = 960,
    [int]$Height = 540,
    [string]$BuiltAutoScaleDllPath = (Join-Path $PSScriptRoot "..\build\Release\Lossless.dll"),
    [string]$BridgeExe = (Join-Path $PSScriptRoot "..\..\bridge\build\Release\DlssNrBridge.exe"),
    [string]$OriginalSource,
    [int]$StartupDelayMs = 2000,
    [int]$WarmupFrames = 320,
    [int]$ReadyTimeoutMs = 180000,
    [int]$NativeResolution = 1
)

$ErrorActionPreference = "Stop"

function Resolve-ExistingPath {
    param([Parameter(Mandatory = $true)][string]$Path, [string]$Name = "path")
    if (!(Test-Path -LiteralPath $Path)) { throw "Missing ${Name}: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function ConvertTo-RelativeManifestPath {
    param([Parameter(Mandatory = $true)][string]$Base, [Parameter(Mandatory = $true)][string]$Path)
    $baseFull = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Base).Path).TrimEnd('\') + '\'
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $baseUri = [Uri]$baseFull
    $pathUri = [Uri]$pathFull
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Read-NullTerminatedAscii {
    param([byte[]]$Bytes, [int]$Offset)
    $end = $Offset
    while ($end -lt $Bytes.Length -and $Bytes[$end] -ne 0) { $end++ }
    if ($end -le $Offset) { return "" }
    return [System.Text.Encoding]::ASCII.GetString($Bytes, $Offset, $end - $Offset)
}

function Get-PeImageInfo {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x200) { throw "PE image is too small: $Path" }
    if ([BitConverter]::ToUInt16($bytes, 0) -ne 0x5A4D) { throw "PE image is missing MZ header: $Path" }
    $peOffset = [BitConverter]::ToUInt32($bytes, 0x3C)
    if ($peOffset -gt ($bytes.Length - 0x108)) { throw "PE header offset is outside file: $Path" }
    if ([BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) { throw "PE image is missing PE signature: $Path" }

    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $sectionCount = [BitConverter]::ToUInt16($bytes, $peOffset + 6)
    $optionalSize = [BitConverter]::ToUInt16($bytes, $peOffset + 20)
    $optionalOffset = $peOffset + 24
    $magic = [BitConverter]::ToUInt16($bytes, $optionalOffset)
    if ($machine -ne 0x8664) { throw ("PE image is not AMD64/x64: {0} machine=0x{1:x4}" -f $Path, $machine) }
    if ($magic -ne 0x20B) { throw ("PE image is not PE32+: {0} magic=0x{1:x4}" -f $Path, $magic) }

    $exportDirectoryOffset = $optionalOffset + 112
    $exportRva = [BitConverter]::ToUInt32($bytes, $exportDirectoryOffset)
    $exportSize = [BitConverter]::ToUInt32($bytes, $exportDirectoryOffset + 4)
    $sectionOffset = $optionalOffset + $optionalSize
    $sections = @()
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $base = $sectionOffset + ($i * 40)
        if ($base -gt ($bytes.Length - 40)) { throw "PE section table is truncated: $Path" }
        $sections += [pscustomobject]@{
            virtual_size = [BitConverter]::ToUInt32($bytes, $base + 8)
            virtual_address = [BitConverter]::ToUInt32($bytes, $base + 12)
            raw_size = [BitConverter]::ToUInt32($bytes, $base + 16)
            raw_pointer = [BitConverter]::ToUInt32($bytes, $base + 20)
        }
    }

    function Convert-RvaToOffset {
        param([uint32]$Rva)
        foreach ($section in $sections) {
            $size = [Math]::Max([uint32]$section.virtual_size, [uint32]$section.raw_size)
            if ($Rva -ge $section.virtual_address -and $Rva -lt ($section.virtual_address + $size)) {
                return [int]($section.raw_pointer + ($Rva - $section.virtual_address))
            }
        }
        if ($Rva -lt $bytes.Length) { return [int]$Rva }
        throw ("PE RVA 0x{0:x} cannot be mapped: {1}" -f $Rva, $Path)
    }

    $exports = @()
    if ($exportRva -ne 0 -and $exportSize -ne 0) {
        $exportOffset = Convert-RvaToOffset -Rva $exportRva
        if ($exportOffset -gt ($bytes.Length - 40)) { throw "PE export directory is truncated: $Path" }
        $numberOfNames = [BitConverter]::ToUInt32($bytes, $exportOffset + 24)
        $addressOfNames = [BitConverter]::ToUInt32($bytes, $exportOffset + 32)
        if ($numberOfNames -gt 0) {
            $namesOffset = Convert-RvaToOffset -Rva $addressOfNames
            for ($i = 0; $i -lt $numberOfNames; $i++) {
                $nameRvaOffset = $namesOffset + ($i * 4)
                if ($nameRvaOffset -gt ($bytes.Length - 4)) { throw "PE export name table is truncated: $Path" }
                $nameRva = [BitConverter]::ToUInt32($bytes, $nameRvaOffset)
                $nameOffset = Convert-RvaToOffset -Rva $nameRva
                $exports += Read-NullTerminatedAscii -Bytes $bytes -Offset $nameOffset
            }
        }
    }

    [pscustomobject]@{ machine = $machine; magic = $magic; exports = $exports }
}

function Assert-PeImage {
    param([string]$Path, [string]$Name, [string[]]$RequiredExports = @())
    $info = Get-PeImageInfo -Path $Path
    foreach ($required in $RequiredExports) {
        if ($info.exports -notcontains $required) {
            throw "$Name is missing required export '$required': $Path"
        }
    }
}

function Assert-LosslessNotRunning {
    param([string]$LsDir)
    $resolvedLs = (Resolve-Path -LiteralPath $LsDir).Path.TrimEnd('\')
    $matches = @()
    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        $isCandidate = $false
        try {
            if ($process.MainModule -and $process.MainModule.FileName -and $process.MainModule.FileName.StartsWith($resolvedLs, [System.StringComparison]::OrdinalIgnoreCase)) {
                $isCandidate = $true
            }
        } catch {
            if ($process.ProcessName -like 'Lossless*') { $isCandidate = $true }
        }
        if ($isCandidate) { $matches += $process }
    }
    if ($matches.Count -gt 0) {
        $names = ($matches | ForEach-Object { "$($_.ProcessName)($($_.Id))" }) -join ', '
        throw "Lossless Scaling appears to be running; close it before installing. Processes: $names"
    }
}

function Copy-WithManifestBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$BackupRoot,
        [Parameter(Mandatory = $true)][string]$LsDir,
        [System.Collections.ArrayList]$Entries
    )
    $existed = Test-Path -LiteralPath $Target -PathType Leaf
    $beforeHash = if ($existed) { Get-FileSha256 -Path $Target } else { $null }
    $relativeTarget = ConvertTo-RelativeManifestPath -Base $LsDir -Path $Target
    $backupRelative = $null
    if ($existed) {
        $backupRelative = Join-Path "files" $relativeTarget
        $backupPath = Join-Path $BackupRoot $backupRelative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupPath) | Out-Null
        Copy-Item -LiteralPath $Target -Destination $backupPath -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Target) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    [void]$Entries.Add([pscustomobject]@{
        relative_path = $relativeTarget
        existed = $existed
        backup_relative_path = $backupRelative
        before_sha256 = $beforeHash
        installed_sha256 = Get-FileSha256 -Path $Target
        action = "copy"
    })
}

function Write-TextWithManifestBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [AllowEmptyString()][string]$Content,
        [Parameter(Mandatory = $true)][string]$BackupRoot,
        [Parameter(Mandatory = $true)][string]$LsDir,
        [System.Collections.ArrayList]$Entries
    )
    $tmp = Join-Path $env:TEMP ([Guid]::NewGuid().ToString('n') + '.tmp')
    $Content | Set-Content -LiteralPath $tmp -Encoding ASCII
    try { Copy-WithManifestBackup -Target $Target -Source $tmp -BackupRoot $BackupRoot -LsDir $LsDir -Entries $Entries }
    finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

$lsDirPath = Resolve-ExistingPath -Path $LsDir -Name "Lossless Scaling directory"
$wrapperPath = Resolve-ExistingPath -Path $BuiltAutoScaleDllPath -Name "built auto-scale Lossless.dll"
$bridgePath = Resolve-ExistingPath -Path $BridgeExe -Name "DlssNrBridge.exe"
$proxyPath = Resolve-ExistingPath -Path $ProxyVersionSource -Name "AMD proxy version.dll"
$nrPath = Resolve-ExistingPath -Path $NrSource -Name "nvngx_dlssnr.dll"
$originalSourcePath = if ($OriginalSource) { Resolve-ExistingPath -Path $OriginalSource -Name "original Lossless.dll source" } else { $null }

$requiredLosslessExports = @("Activate", "Init", "UnInit", "ApplySettings", "GetForegroundWindowEx")

Assert-LosslessNotRunning -LsDir $lsDirPath
Assert-PeImage -Path $wrapperPath -Name "built auto-scale Lossless.dll" -RequiredExports $requiredLosslessExports
Assert-PeImage -Path $bridgePath -Name "DlssNrBridge.exe"
Assert-PeImage -Path $proxyPath -Name "AMD proxy version.dll"
Assert-PeImage -Path $nrPath -Name "nvngx_dlssnr.dll"
if ($originalSourcePath) { Assert-PeImage -Path $originalSourcePath -Name "original Lossless.dll source" -RequiredExports $requiredLosslessExports }

$losslessDll = Join-Path $lsDirPath "Lossless.dll"
$losslessOriginal = Join-Path $lsDirPath "Lossless_original.dll"
$configPath = Join-Path $lsDirPath "NrAutoScale.ini"
$runtimeDir = Join-Path $lsDirPath "nr-bridge\runtime"
$manifestPath = Join-Path $lsDirPath "nr-bridge\install-manifest.json"
$backupRoot = Join-Path $lsDirPath ("nr-bridge\backups\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$entries = New-Object System.Collections.ArrayList
$createdOriginal = $false

if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    throw "Auto-scale is already installed according to $manifestPath. Run Uninstall-AutoScale.ps1 first, then install again."
}
if (!(Test-Path -LiteralPath $losslessDll -PathType Leaf) -and !$originalSourcePath) {
    throw "Missing current Lossless.dll. Provide -OriginalSource if installing into a sandbox without an existing Lossless.dll."
}
if (Test-Path -LiteralPath $losslessDll -PathType Leaf) { Assert-PeImage -Path $losslessDll -Name "current Lossless.dll" -RequiredExports $requiredLosslessExports }
if (Test-Path -LiteralPath $losslessOriginal -PathType Leaf) { Assert-PeImage -Path $losslessOriginal -Name "existing Lossless_original.dll" -RequiredExports $requiredLosslessExports }

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

if (!(Test-Path -LiteralPath $losslessOriginal -PathType Leaf)) {
    if ((Test-Path -LiteralPath $losslessDll -PathType Leaf) -and (Get-FileSha256 -Path $losslessDll) -eq (Get-FileSha256 -Path $wrapperPath) -and !$originalSourcePath) {
        throw "Current Lossless.dll already matches the wrapper and Lossless_original.dll is missing. Provide -OriginalSource to avoid saving the wrapper as the original."
    }
    if ($originalSourcePath) {
        Copy-WithManifestBackup -Target $losslessOriginal -Source $originalSourcePath -BackupRoot $backupRoot -LsDir $lsDirPath -Entries $entries
    } else {
        Copy-WithManifestBackup -Target $losslessOriginal -Source $losslessDll -BackupRoot $backupRoot -LsDir $lsDirPath -Entries $entries
    }
    $createdOriginal = $true
}

Copy-WithManifestBackup -Target (Join-Path $runtimeDir "DlssNrBridge.exe") -Source $bridgePath -BackupRoot $backupRoot -LsDir $lsDirPath -Entries $entries
Copy-WithManifestBackup -Target (Join-Path $runtimeDir "version.dll") -Source $proxyPath -BackupRoot $backupRoot -LsDir $lsDirPath -Entries $entries
Copy-WithManifestBackup -Target (Join-Path $runtimeDir "nvngx_dlssnr.dll") -Source $nrPath -BackupRoot $backupRoot -LsDir $lsDirPath -Entries $entries

$amdConfig = @"
[DlssNrOnAmd]
Enabled=1
UseFsrInputs=0
Inline=0
Scale=0.03125
LocalStructure=1
LocalTone=1
SkinStructure=1
"@
Write-TextWithManifestBackup -Target (Join-Path $runtimeDir "dlssnr_on_amd.ini") -Content $amdConfig -BackupRoot $backupRoot -LsDir $lsDirPath -Entries $entries
Write-TextWithManifestBackup -Target (Join-Path $runtimeDir "SpecialK.deny.DlssNrBridge") -Content "" -BackupRoot $backupRoot -LsDir $lsDirPath -Entries $entries
Write-TextWithManifestBackup -Target (Join-Path $runtimeDir "SpecialK.deny.DlssNrBridge.exe") -Content "" -BackupRoot $backupRoot -LsDir $lsDirPath -Entries $entries

$autoScaleConfig = @"
[AutoScale]
Enabled=1
BridgeExe=$(Join-Path $runtimeDir "DlssNrBridge.exe")
RuntimeDirectory=$runtimeDir
HipVisibleDevices=$HipVisibleDevices
Width=$Width
Height=$Height
StartupDelayMs=$StartupDelayMs
WarmupFrames=$WarmupFrames
ReadyTimeoutMs=$ReadyTimeoutMs
NativeResolution=$NativeResolution
DefaultScalingTypeIfOff=0
ForceCaptureApi=1
"@
Write-TextWithManifestBackup -Target $configPath -Content $autoScaleConfig -BackupRoot $backupRoot -LsDir $lsDirPath -Entries $entries

Copy-WithManifestBackup -Target $losslessDll -Source $wrapperPath -BackupRoot $backupRoot -LsDir $lsDirPath -Entries $entries

$manifest = [pscustomobject]@{
    installed_at = (Get-Date).ToString("o")
    ls_dir = $lsDirPath
    backup_root = $backupRoot
    created_lossless_original = $createdOriginal
    full_lossless_scaling_verified = $false
    files = $entries
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $backupRoot "install-manifest.json") -Force

Write-Host "Installed auto-scale wrapper into $lsDirPath"
Write-Host "Manifest: $manifestPath"
Write-Host "Backup: $backupRoot"
