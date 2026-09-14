[CmdletBinding()]
param([switch]$NoLaunch, [switch]$VerifyOnly, [switch]$NoShortcuts)

$ErrorActionPreference = 'Stop'
# Settings and downloaded runtimes live separately in LocalAppData, outside this directory.
$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA is unavailable for this Windows account.' }
$programs = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs')).TrimEnd('\')
$destination = Join-Path $programs 'DLSS5 AMD Swapper'

function Assert-NoLinks([string]$path) {
    $cursor = $path
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked package or installation paths are not supported: $cursor"
        }
        $parent = Split-Path $cursor -Parent
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Assert-InstallChild([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    if (!$full.StartsWith($programs + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installation destination.' }
    Assert-NoLinks $full
}

function Get-PackageFiles([string]$directory) {
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        Assert-NoLinks $item.FullName
        if ($item.PSIsContainer) { Get-PackageFiles $item.FullName } else { $item }
    }
}

Assert-NoLinks $packageRoot
Assert-InstallChild $destination
if ($packageRoot.Equals($destination, [StringComparison]::OrdinalIgnoreCase) -or
    $packageRoot.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Run this installer from a freshly extracted release folder outside the installed app.'
}
$manifest = Join-Path $packageRoot 'SHA256SUMS.txt'
if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Extract the complete ZIP, including SHA256SUMS.txt, first.' }
Assert-NoLinks $manifest
$entries = @()
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath $manifest) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw 'Invalid package checksum entry.' }
    $hash = $Matches[1]
    $relative = $Matches[2].Replace('/', '\')
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '[:<>"|?*\x00-\x1f]' -or
        @($relative.Split('\') | Where-Object { !$_ -or $_ -in '.', '..' -or $_ -match '[. ]$' -or $_ -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count) {
        throw "Unsafe package entry: $relative"
    }
    if ($relative -eq 'SHA256SUMS.txt' -or !$names.Add($relative)) { throw "Duplicate or reserved package entry: $relative" }
    $source = [IO.Path]::GetFullPath((Join-Path $packageRoot $relative))
    if (!$source.StartsWith($packageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package entry.' }
    Assert-NoLinks $source
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package file: $relative. Extract the complete ZIP first." }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash) { throw "Checksum mismatch: $relative. Extract a fresh copy of the ZIP." }
    $entries += @{ Source = $source; Relative = $relative; Hash = $hash }
}
if (!$names.Contains('Dlss5AmdSwapper.exe')) { throw 'Package manifest does not contain the manager.' }
foreach ($file in Get-PackageFiles $packageRoot) {
    $relative = $file.FullName.Substring($packageRoot.Length + 1)
    if ($relative -ne 'SHA256SUMS.txt' -and !$names.Contains($relative)) { throw "Unlisted package file: $relative. Extract the release ZIP into an empty folder." }
}
if ($VerifyOnly) { Write-Host "Package checksum and path verification passed ($($entries.Count) files)."; return }

$exe = Join-Path $destination 'Dlss5AmdSwapper.exe'
foreach ($process in Get-Process Dlss5AmdSwapper -ErrorAction SilentlyContinue) {
    if ($process.Path -eq $exe) { throw 'Close the installed Swapper, then run setup again.' }
}
if ((Test-Path -LiteralPath $destination) -and !(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "The installation folder already exists without the manager. Move it aside before installing: $destination"
}

$transaction = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $programs ('.DLSS5-AMD-Swapper-stage-' + $transaction)
$backup = Join-Path $programs ('DLSS5 AMD Swapper.previous-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $transaction.Substring(0, 8))
$movedPrevious = $false
try {
    Assert-InstallChild $stage
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($entry in $entries) {
        $target = Join-Path $stage $entry.Relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $entry.Source -Destination $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.Hash) { throw "Staged file verification failed: $($entry.Relative)" }
    }
    Copy-Item -LiteralPath $manifest -Destination (Join-Path $stage 'SHA256SUMS.txt')
    # The live install is not touched until every new file has copied and verified.
    Assert-InstallChild $destination
    Assert-InstallChild $backup
    if (Test-Path -LiteralPath $destination) {
        Move-Item -LiteralPath $destination -Destination $backup
        $movedPrevious = $true
    }
    try { Move-Item -LiteralPath $stage -Destination $destination }
    catch {
        if ($movedPrevious) { Move-Item -LiteralPath $backup -Destination $destination }
        throw
    }
} finally {
    if (Test-Path -LiteralPath $stage) {
        Assert-InstallChild $stage
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

Write-Host "DLSS5 AMD Swapper installed for this user: $destination"
if ($movedPrevious) { Write-Host "Previous installation preserved for rollback: $backup" }
if (!$NoShortcuts) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
            if ([string]::IsNullOrWhiteSpace($folder)) { throw 'A Windows shortcut folder is unavailable.' }
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
            $link = $shell.CreateShortcut((Join-Path $folder 'DLSS5 AMD Swapper.lnk'))
            $link.TargetPath = $exe
            $link.WorkingDirectory = $destination
            $link.IconLocation = "$exe,0"
            $link.Save()
        }
        Write-Host 'Desktop and Start menu shortcuts are ready.'
    } catch { Write-Warning "The app is installed, but Windows could not create every shortcut. Launch $exe directly. $($_.Exception.Message)" }
}
if (!$NoLaunch) { Start-Process -FilePath $exe }
