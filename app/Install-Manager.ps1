param([switch]$NoLaunch, [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
# Per-user installation. Game folders and third-party runtimes are never copied here.
$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$destination = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\DLSS5 AMD Swapper'))
$expectedParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs')).TrimEnd('\') + '\'
if (-not $destination.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installation destination.' }
if ($packageRoot.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) { throw 'Run this installer from the extracted release folder.' }
$manifest = Join-Path $packageRoot 'SHA256SUMS.txt'
if (!(Test-Path -LiteralPath $manifest)) { throw 'Extract the complete ZIP, including SHA256SUMS.txt, first.' }
function Assert-NoLinks([string]$path) {
    $cursor = $path
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked package or installation paths are not supported.' }
        $parent = Split-Path $cursor -Parent
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
$entries = @()
foreach ($line in Get-Content -LiteralPath $manifest) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw 'Invalid package checksum entry.' }
    $hash = $Matches[1]
    $relative = $Matches[2].Replace('/', '\')
    $source = [IO.Path]::GetFullPath((Join-Path $packageRoot $relative))
    $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (!$source.StartsWith($packageRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or !$target.StartsWith($destination.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package entry.' }
    Assert-NoLinks $source
    Assert-NoLinks $target
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash) { throw "Checksum mismatch: $relative" }
    $entries += @{ Source=$source; Target=$target }
}
if (!$entries.Count -or !($entries | Where-Object { $_.Target -eq (Join-Path $destination 'Dlss5AmdSwapper.exe') })) { throw 'Package manifest does not contain the manager.' }
if ($VerifyOnly) { Write-Host 'Package checksum and path verification passed.'; return }
$exe = Join-Path $destination 'Dlss5AmdSwapper.exe'
foreach ($process in Get-Process Dlss5AmdSwapper -ErrorAction SilentlyContinue) {
    if ($process.Path -eq $exe) { throw 'Exit the installed Swapper from its tray menu, then run setup again.' }
}
foreach ($entry in $entries) {
    New-Item -ItemType Directory -Force -Path (Split-Path $entry.Target -Parent) | Out-Null
    Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force
}
Copy-Item -LiteralPath $manifest -Destination (Join-Path $destination 'SHA256SUMS.txt') -Force
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $link = $shell.CreateShortcut((Join-Path $folder 'DLSS5 AMD Swapper.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $destination
    $link.IconLocation = "$exe,0"
    $link.Save()
}
Write-Host 'DLSS5 AMD Swapper installed for this user. Desktop and Start menu shortcuts are ready.'
if (!$NoLaunch) { Start-Process -FilePath $exe }
