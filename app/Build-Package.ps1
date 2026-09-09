param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\DLSS5-AMD-Swapper-local"
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$payload = Join-Path $output "payload"
$project = Join-Path $PSScriptRoot "Dlss5AmdSwapper\Dlss5AmdSwapper.csproj"
$smokeProject = Join-Path $PSScriptRoot "Dlss5AmdSwapper.SmokeTests\Dlss5AmdSwapper.SmokeTests.csproj"
$autoScaleBuild = Join-Path $repoRoot "auto-scale\build.ps1"
$bridgeBuild = Join-Path $repoRoot "bridge\build.ps1"

Write-Host "Building project-owned Lossless Scaling wrapper..."
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $autoScaleBuild
if ($LASTEXITCODE -ne 0) { throw "auto-scale build failed with exit code $LASTEXITCODE" }

Write-Host "Building project-owned neural bridge..."
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bridgeBuild
if ($LASTEXITCODE -ne 0) { throw "bridge build failed with exit code $LASTEXITCODE" }

Write-Host "Running app smoke tests..."
& dotnet run --project $smokeProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "app smoke tests failed with exit code $LASTEXITCODE" }

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output, $payload | Out-Null

Write-Host "Publishing self-contained win-x64 app..."
& dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $output
if ($LASTEXITCODE -ne 0) { throw "app publish failed with exit code $LASTEXITCODE" }

$payloadSources = @{
    "Setup.ps1" = Join-Path $repoRoot "auto-scale\scripts\Setup.ps1"
    "Install-AutoScale.ps1" = Join-Path $repoRoot "auto-scale\scripts\Install-AutoScale.ps1"
    "Uninstall-AutoScale.ps1" = Join-Path $repoRoot "auto-scale\scripts\Uninstall-AutoScale.ps1"
    "Lossless.dll" = Join-Path $repoRoot "auto-scale\build\Release\Lossless.dll"
    "DlssNrBridge.exe" = Join-Path $repoRoot "bridge\build\Release\DlssNrBridge.exe"
}

foreach ($entry in $payloadSources.GetEnumerator()) {
    if (!(Test-Path -LiteralPath $entry.Value -PathType Leaf)) { throw "Missing package payload: $($entry.Value)" }
    Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $payload $entry.Key) -Force
}

$releaseDocs = @{
    "README.md" = Join-Path $PSScriptRoot "PACKAGE-README.md"
    "INSTALL.md" = Join-Path $PSScriptRoot "PACKAGE-INSTALL.md"
    "THIRD-PARTY.md" = Join-Path $PSScriptRoot "PACKAGE-THIRD-PARTY.md"
    "LICENSE" = Join-Path $repoRoot "LICENSE"
}
foreach ($entry in $releaseDocs.GetEnumerator()) {
    if (!(Test-Path -LiteralPath $entry.Value -PathType Leaf)) { throw "Missing package documentation: $($entry.Value)" }
    Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $output $entry.Key) -Force
}

$forbidden = @(
    "Lossless_original.dll",
    "LosslessScaling.exe",
    "version.dll",
    "nvngx_dlssnr.dll",
    "dlssnr_on_amd_setup.exe",
    "dlssnr_on_amd_weights.bin"
)
$bad = Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object { $forbidden -contains $_.Name }
if ($bad) {
    throw "Package contains a forbidden third-party/private file: $($bad.FullName -join ', ')"
}

$hashLines = foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object Name -ne "SHA256SUMS.txt" | Sort-Object FullName) {
    $outputPrefix = $output.TrimEnd('\') + '\'
    $relative = $file.FullName.Substring($outputPrefix.Length).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
$hashLines | Set-Content -LiteralPath (Join-Path $output "SHA256SUMS.txt") -Encoding ASCII

$zip = "$output.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host "Package ready: $output"
Write-Host "ZIP ready:     $zip"
