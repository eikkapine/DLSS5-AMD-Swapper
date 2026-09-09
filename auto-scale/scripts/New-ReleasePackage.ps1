param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\runs\packages"),
    [string]$PackageName = ("nr-auto-scale-" + (Get-Date -Format "yyyyMMdd-HHmmss")),
    [string]$AutoScaleDllPath = (Join-Path $PSScriptRoot "..\build\Release\Lossless.dll"),
    [string]$BridgeExePath = (Join-Path $PSScriptRoot "..\..\bridge\build\Release\DlssNrBridge.exe")
)

$ErrorActionPreference = "Stop"

function Resolve-RequiredFile {
    param([string]$Path, [string]$Name)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing ${Name}: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Test-ForbiddenPackageEntry {
    param([string]$RelativePath)
    $normalized = $RelativePath.Replace('/', '\')
    $allowedImageEntries = @(
        'docs\images\cs2-native-off.png',
        'docs\images\cs2-native-on.png'
    )
    if ($allowedImageEntries -contains $normalized) {
        return $false
    }
    $patterns = @(
        '(^|\\)Lossless_original\.dll$',
        '(^|\\)LosslessScaling.*\.(dll|exe)$',
        '(^|\\)version\.dll$',
        '(^|\\)dlssnr_on_amd_setup\.exe$',
        '(^|\\)amdhip64[^\\]*\.dll$',
        '(^|\\)nvngx_dlssnr\.dll$',
        '(^|\\)dlssnr_on_amd_weights\.bin$',
        '(^|\\)\.nr-auto-scale-direct\.json$',
        '(^|\\)NrAutoScale\.ini$',
        '(^|\\)dlssnr_on_amd\.ini$',
        '(^|\\)install-manifest\.json$',
        '(^|\\)(install-backups|backups|runs)(\\|$)',
        '\.(log|png|jpg|jpeg|ppm|bmp|webp)$'
    )
    foreach ($pattern in $patterns) {
        if ($normalized -match $pattern) { return $true }
    }
    return $false
}

function Add-PackageFile {
    param([string]$Source, [string]$RelativePath, [System.Collections.ArrayList]$Entries)
    if (Test-ForbiddenPackageEntry -RelativePath $RelativePath) { throw "Forbidden package entry: $RelativePath" }
    $target = Join-Path $stageRoot $RelativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $target -Force
    [void]$Entries.Add([pscustomobject]@{
        path = $RelativePath.Replace('\','/')
        sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $target).Length
    })
}

function Add-PackageText {
    param([string]$Content, [string]$RelativePath, [System.Collections.ArrayList]$Entries)
    if (Test-ForbiddenPackageEntry -RelativePath $RelativePath) { throw "Forbidden package entry: $RelativePath" }
    $target = Join-Path $stageRoot $RelativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Set-Content -LiteralPath $target -Value $Content -Encoding ASCII
    [void]$Entries.Add([pscustomobject]@{
        path = $RelativePath.Replace('\','/')
        sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $target).Length
    })
}

$scriptDir = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$autoScaleRoot = (Resolve-Path -LiteralPath (Join-Path $scriptDir "..")).Path
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $autoScaleRoot "..")).Path
$docsRoot = Join-Path $repoRoot "docs"
$autoScaleDll = Resolve-RequiredFile -Path $AutoScaleDllPath -Name "built auto-scale Lossless.dll proxy"
$bridgeExe = Resolve-RequiredFile -Path $BridgeExePath -Name "built DlssNrBridge.exe"
$setupCmd = Resolve-RequiredFile -Path (Join-Path $scriptDir "Setup.cmd") -Name "Setup.cmd"
$setupPs1 = Resolve-RequiredFile -Path (Join-Path $scriptDir "Setup.ps1") -Name "Setup.ps1"
$installPs1 = Resolve-RequiredFile -Path (Join-Path $scriptDir "Install-AutoScale.ps1") -Name "Install-AutoScale.ps1"
$uninstallPs1 = Resolve-RequiredFile -Path (Join-Path $scriptDir "Uninstall-AutoScale.ps1") -Name "Uninstall-AutoScale.ps1"
$packagePs1 = Resolve-RequiredFile -Path (Join-Path $scriptDir "New-ReleasePackage.ps1") -Name "New-ReleasePackage.ps1"
$readme = Resolve-RequiredFile -Path (Join-Path $repoRoot "README.md") -Name "release README.md"
$installDoc = Resolve-RequiredFile -Path (Join-Path $docsRoot "install.md") -Name "install.md"
$licensingDoc = Resolve-RequiredFile -Path (Join-Path $docsRoot "licensing.md") -Name "licensing.md"
$verificationDoc = Resolve-RequiredFile -Path (Join-Path $docsRoot "verification.md") -Name "verification.md"
$performanceDoc = Resolve-RequiredFile -Path (Join-Path $docsRoot "performance.md") -Name "performance.md"
$directGameScript = Resolve-RequiredFile -Path (Join-Path $repoRoot "direct-game\amd_dlss5.py") -Name "direct-game amd_dlss5.py"
$directGameReadme = Resolve-RequiredFile -Path (Join-Path $repoRoot "direct-game\README.md") -Name "direct-game README.md"
$capturePerformance = Resolve-RequiredFile -Path (Join-Path $repoRoot "tools\Capture-Performance.ps1") -Name "Capture-Performance.ps1"
$publicationCheck = Resolve-RequiredFile -Path (Join-Path $repoRoot "tools\Check-Publication.py") -Name "Check-Publication.py"
$comparisonOffImage = Resolve-RequiredFile -Path (Join-Path $docsRoot "images\cs2-native-off.png") -Name "cs2-native-off.png"
$comparisonOnImage = Resolve-RequiredFile -Path (Join-Path $docsRoot "images\cs2-native-on.png") -Name "cs2-native-on.png"
$comparisonJson = Resolve-RequiredFile -Path (Join-Path $docsRoot "images\comparison.json") -Name "comparison.json"
$thirdParty = Resolve-RequiredFile -Path (Join-Path $autoScaleRoot "THIRD_PARTY.md") -Name "auto-scale THIRD_PARTY.md"
$license = Resolve-RequiredFile -Path (Join-Path $repoRoot "LICENSE") -Name "nr-development LICENSE"
$versionFile = Resolve-RequiredFile -Path (Join-Path $repoRoot "VERSION") -Name "project VERSION"
$releaseManifest = Resolve-RequiredFile -Path (Join-Path $repoRoot "RELEASE.json") -Name "release artifact manifest"
$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw "Invalid project version" }
$declared = Get-Content -LiteralPath $releaseManifest -Raw | ConvertFrom-Json
if ($declared.version -ne $version) { throw "VERSION and RELEASE.json disagree" }
foreach ($artifact in @(@{path=$bridgeExe; name='DlssNrBridge.exe'}, @{path=$autoScaleDll; name='Lossless.dll'})) {
    $expected = @($declared.artifacts | Where-Object { $_.name -eq $artifact.name })
    if ($expected.Count -ne 1 -or (Get-FileHash -LiteralPath $artifact.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[0].sha256) {
        throw "Artifact does not match the declared release: $($artifact.name)"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$stageRoot = Join-Path $outputRoot ($PackageName + "-stage")
$zipPath = Join-Path $outputRoot ($PackageName + ".zip")
if (Test-Path -LiteralPath $stageRoot) { throw "Stage directory already exists: $stageRoot" }
if (Test-Path -LiteralPath $zipPath) { throw "Package already exists: $zipPath" }
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

$entries = New-Object System.Collections.ArrayList
Add-PackageFile -Source $autoScaleDll -RelativePath "bin\Lossless.dll" -Entries $entries
Add-PackageFile -Source $bridgeExe -RelativePath "bin\DlssNrBridge.exe" -Entries $entries
Add-PackageText -Content @"
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Setup.ps1" %*
exit /b %ERRORLEVEL%
"@ -RelativePath "Setup.cmd" -Entries $entries
Add-PackageFile -Source $setupCmd -RelativePath "scripts\Setup.cmd" -Entries $entries
Add-PackageFile -Source $setupPs1 -RelativePath "scripts\Setup.ps1" -Entries $entries
Add-PackageFile -Source $installPs1 -RelativePath "scripts\Install-AutoScale.ps1" -Entries $entries
Add-PackageFile -Source $uninstallPs1 -RelativePath "scripts\Uninstall-AutoScale.ps1" -Entries $entries
Add-PackageFile -Source $packagePs1 -RelativePath "scripts\New-ReleasePackage.ps1" -Entries $entries
Add-PackageFile -Source $readme -RelativePath "README.md" -Entries $entries
Add-PackageFile -Source $installDoc -RelativePath "docs\install.md" -Entries $entries
Add-PackageFile -Source $licensingDoc -RelativePath "docs\licensing.md" -Entries $entries
Add-PackageFile -Source $verificationDoc -RelativePath "docs\verification.md" -Entries $entries
Add-PackageFile -Source $performanceDoc -RelativePath "docs\performance.md" -Entries $entries
Add-PackageFile -Source $directGameScript -RelativePath "direct-game\amd_dlss5.py" -Entries $entries
Add-PackageFile -Source $directGameReadme -RelativePath "direct-game\README.md" -Entries $entries
Add-PackageFile -Source $capturePerformance -RelativePath "tools\Capture-Performance.ps1" -Entries $entries
Add-PackageFile -Source $publicationCheck -RelativePath "tools\Check-Publication.py" -Entries $entries
Add-PackageFile -Source $comparisonOffImage -RelativePath "docs\images\cs2-native-off.png" -Entries $entries
Add-PackageFile -Source $comparisonOnImage -RelativePath "docs\images\cs2-native-on.png" -Entries $entries
Add-PackageFile -Source $comparisonJson -RelativePath "docs\images\comparison.json" -Entries $entries
Add-PackageFile -Source $thirdParty -RelativePath "docs\THIRD_PARTY.md" -Entries $entries
Add-PackageFile -Source $license -RelativePath "LICENSE" -Entries $entries
Add-PackageFile -Source $license -RelativePath "docs\LICENSE.txt" -Entries $entries
Add-PackageFile -Source $versionFile -RelativePath "VERSION" -Entries $entries
Add-PackageFile -Source $releaseManifest -RelativePath "RELEASE.json" -Entries $entries
foreach ($relative in @('architecture.md', 'development.md', 'neural-upstream-performance.md', 'release-checklist.md',
        "releases\v$version.md")) {
    $source = Resolve-RequiredFile -Path (Join-Path $docsRoot $relative) -Name $relative
    Add-PackageFile -Source $source -RelativePath ("docs\" + $relative) -Entries $entries
}
Get-ChildItem -LiteralPath (Join-Path $docsRoot 'measurements') -Filter '*.json' -File | ForEach-Object {
    Add-PackageFile -Source $_.FullName -RelativePath ("docs\measurements\" + $_.Name) -Entries $entries
}
Add-PackageFile -Source (Join-Path $repoRoot 'bridge\scripts\Analyze-Run.py') -RelativePath 'bridge\scripts\Analyze-Run.py' -Entries $entries

$manifest = [pscustomobject]@{
    package = [System.IO.Path]::GetFileName($zipPath)
    version = $version
    created_at = (Get-Date).ToString("o")
    contents_are_allowlisted = $true
    contains_vendor_runtime_or_lossless_scaling_files = $false
    note = "External AMD/NVIDIA runtime files are user supplied. The package does not include Lossless Scaling binaries, DLSS-NR-on-AMD, NVIDIA model/runtime files, or other vendor runtime payloads."
    files = @($entries | Sort-Object path)
}
$manifestPath = Join-Path $stageRoot "CHECKSUMS.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$relativeEntries = Get-ChildItem -LiteralPath $stageRoot -Recurse -File | ForEach-Object {
    $root = [System.IO.Path]::GetFullPath($stageRoot).TrimEnd('\') + '\'
    [System.IO.Path]::GetFullPath($_.FullName).Substring($root.Length)
}
foreach ($entry in $relativeEntries) {
    if ($entry -ne 'CHECKSUMS.json' -and (Test-ForbiddenPackageEntry -RelativePath $entry)) { throw "Forbidden staged entry: $entry" }
}

Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $zip.Entries) {
        $relative = $entry.FullName.Replace('/', '\')
        if ($relative -ne 'CHECKSUMS.json' -and (Test-ForbiddenPackageEntry -RelativePath $relative)) { throw "Forbidden zip entry: $($entry.FullName)" }
    }
}
finally {
    $zip.Dispose()
}

Write-Host "Package: $zipPath"
Write-Host "Stage: $stageRoot"
Write-Host "Files: $($entries.Count + 1)"
