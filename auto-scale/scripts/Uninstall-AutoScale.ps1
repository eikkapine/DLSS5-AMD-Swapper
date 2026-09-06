param(
    [string]$LsDir = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..\..")).Path,
    [string]$ManifestPath
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

function Resolve-ContainedPath {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$RelativePath, [string]$Name = "manifest path")
    if ([System.IO.Path]::IsPathRooted($RelativePath)) { throw "$Name must be relative: $RelativePath" }
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $full = [System.IO.Path]::GetFullPath((Join-Path $rootFull $RelativePath))
    if (!$full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name escapes its root: $RelativePath"
    }
    return $full
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
        throw "Lossless Scaling appears to be running; close it before uninstalling. Processes: $names"
    }
}

function Get-RestorePlan {
    param([string]$LsDir, [string]$BackupRoot, [pscustomobject]$Entry)
    $relative = [string]$Entry.relative_path
    $target = Resolve-ContainedPath -Root $LsDir -RelativePath $relative -Name "manifest target path"
    $currentHash = Get-FileSha256 -Path $target
    $installedHash = if ($Entry.installed_sha256) { ([string]$Entry.installed_sha256).ToLowerInvariant() } else { $null }
    $beforeHash = if ($Entry.before_sha256) { ([string]$Entry.before_sha256).ToLowerInvariant() } else { $null }

    if ($Entry.existed) {
        if (!$currentHash) { throw "Refusing to restore over missing installed file: $target" }
        if (!$installedHash -or $currentHash -ne $installedHash) { throw "Refusing to overwrite user-modified file: $target" }
        $backupRelative = [string]$Entry.backup_relative_path
        if (!$backupRelative) { throw "Manifest entry is missing backup path for $target" }
        $backup = Resolve-ContainedPath -Root $BackupRoot -RelativePath $backupRelative -Name "manifest backup path"
        if (!(Test-Path -LiteralPath $backup -PathType Leaf)) { throw "Missing backup for ${target}: $backup" }
        $backupHash = Get-FileSha256 -Path $backup
        if (!$beforeHash -or $backupHash -ne $beforeHash) { throw "Backup hash mismatch for ${target}: $backup" }
        return [pscustomobject]@{ action = "restore"; target = $target; backup = $backup; relative_path = $relative }
    }

    if ($currentHash -and $installedHash -and $currentHash -ne $installedHash) { throw "Refusing to remove user-modified file: $target" }
    return [pscustomobject]@{ action = "remove-if-present"; target = $target; backup = $null; relative_path = $relative }
}

$lsDirPath = Resolve-ExistingPath -Path $LsDir -Name "Lossless Scaling directory"
$defaultManifestPath = Join-Path $lsDirPath "nr-bridge\install-manifest.json"
if (!$ManifestPath) { $ManifestPath = $defaultManifestPath }
$manifestFile = Resolve-ExistingPath -Path $ManifestPath -Name "install manifest"
Assert-LosslessNotRunning -LsDir $lsDirPath

$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if (!$manifest.files -or !$manifest.backup_root -or !$manifest.ls_dir) { throw "Manifest is missing required uninstall data: $manifestFile" }
$manifestLsDir = [System.IO.Path]::GetFullPath([string]$manifest.ls_dir).TrimEnd('\')
if (!$manifestLsDir.Equals($lsDirPath.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Manifest LS directory mismatch. Manifest=$manifestLsDir requested=$($lsDirPath.TrimEnd('\'))"
}
$backupRoot = Resolve-ExistingPath -Path $manifest.backup_root -Name "install backup root"

$selected = @()
foreach ($relative in @('Lossless.dll', 'NrAutoScale.ini')) {
    $entry = @($manifest.files | Where-Object { $_.relative_path -eq $relative } | Select-Object -Last 1)
    if ($entry.Count -gt 0) { $selected += $entry[0] }
}
if ($manifest.created_lossless_original) {
    $entry = @($manifest.files | Where-Object { $_.relative_path -eq 'Lossless_original.dll' } | Select-Object -Last 1)
    if ($entry.Count -gt 0) { $selected += $entry[0] }
}
$selected += @($manifest.files | Where-Object { $_.relative_path -like 'nr-bridge\runtime\*' -and $_.existed })

$restorePlan = @()
foreach ($entry in $selected) {
    $restorePlan += Get-RestorePlan -LsDir $lsDirPath -BackupRoot $backupRoot -Entry $entry
}

foreach ($item in $restorePlan) {
    if ($item.action -eq 'restore') {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $item.target) | Out-Null
        Copy-Item -LiteralPath $item.backup -Destination $item.target -Force
    } elseif ($item.action -eq 'remove-if-present') {
        if (Test-Path -LiteralPath $item.target -PathType Leaf) { Remove-Item -LiteralPath $item.target -Force }
    }
}

$defaultManifestFullPath = [System.IO.Path]::GetFullPath($defaultManifestPath)
if ([System.IO.Path]::GetFullPath($manifestFile).Equals($defaultManifestFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $manifestFile -Force
}

Write-Host "Uninstalled auto-scale wrapper from $lsDirPath"
Write-Host "Backups preserved at $backupRoot"
