param(
    [Parameter(Mandatory = $true)]
    [string]$ProcessName,

    [ValidateRange(5, 3600)]
    [int]$Seconds = 30,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\runs\presentmon'),

    [switch]$NoDownload
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-PresentMon {
    $headers = @{ 'User-Agent' = 'DLSS5-AMD-Swapper performance logger' }
    $release = Invoke-RestMethod -Headers $headers -Uri 'https://api.github.com/repos/GameTechDev/PresentMon/releases/latest'
    $asset = $release.assets | Where-Object { $_.name -match '^PresentMon-[0-9.]+-x64\.exe$' } | Select-Object -First 1
    if (-not $asset) { throw 'The latest PresentMon release has no x64 console executable.' }
    if (-not $asset.digest -or -not $asset.digest.StartsWith('sha256:')) {
        throw 'GitHub did not return a SHA-256 digest for the PresentMon release asset.'
    }

    $cacheRoot = Join-Path $env:LOCALAPPDATA 'DLSS5 AMD Swapper\tools\PresentMon'
    $cacheDir = Join-Path $cacheRoot $release.tag_name
    $exe = Join-Path $cacheDir $asset.name
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

    $expected = $asset.digest.Substring(7).ToLowerInvariant()
    if (Test-Path -LiteralPath $exe) {
        if ((Get-Sha256 $exe) -eq $expected) { return $exe }
        Remove-Item -LiteralPath $exe -Force
    }
    if ($NoDownload) { throw "PresentMon is not cached at $exe and -NoDownload was supplied." }

    Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -OutFile $exe
    $actual = Get-Sha256 $exe
    if ($actual -ne $expected) {
        Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue
        throw "PresentMon SHA-256 mismatch. Expected $expected, got $actual."
    }
    return $exe
}

$processExe = [IO.Path]::GetFileName($ProcessName)
if (-not $processExe.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
    $processExe += '.exe'
}

$presentMon = Get-PresentMon
$outDir = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csv = Join-Path $outDir ("presentmon-{0}-{1}.csv" -f ([IO.Path]::GetFileNameWithoutExtension($processExe)), $stamp)
$allCsv = Join-Path $outDir (".presentmon-all-{0}.csv" -f $stamp)

Write-Host "Recording $processExe for $Seconds seconds..."
try {
    # Capture first and filter afterwards. PresentMon can record ETW presents
    # without elevation even when Windows will not let it resolve a process for
    # --process_name targeting. The transient all-process CSV is deleted before
    # this script returns, so the retained raw log contains only the requested app.
    & $presentMon `
        --timed $Seconds `
        --terminate_after_timed `
        --output_file $allCsv `
        --v1_metrics `
        --no_console_stats

    if ($LASTEXITCODE -ne 0) { throw "PresentMon exited with code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $allCsv)) { throw 'PresentMon did not create a CSV.' }

    $rows = @(Import-Csv -LiteralPath $allCsv | Where-Object {
        $_.Application -and ([IO.Path]::GetFileName($_.Application) -ieq $processExe)
    })
    if ($rows.Count -eq 0) {
        throw "PresentMon recorded no frames attributed to $processExe. Confirm the executable name; if Windows reports the game as <unknown>, rerun this command from an elevated PowerShell."
    }
    $rows | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
}
finally {
    if (Test-Path -LiteralPath $allCsv) { [IO.File]::Delete($allCsv) }
}

$sha = Get-Sha256 $csv
Write-Host "Capture: $csv"
Write-Host "SHA256: $sha"
Write-Host 'The CSV is a private raw log. Publish only sanitized analyzer output, not this file.'
