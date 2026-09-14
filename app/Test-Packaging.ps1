[CmdletBinding()]
param([string]$ResultFile)

$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'Install-Manager.ps1'
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot = Join-Path $tempParent ('DLSS5-packaging-tests-' + [Guid]::NewGuid().ToString('N'))
$previousLocalAppData = $env:LOCALAPPDATA
$results = [Collections.Generic.List[object]]::new()
$packagingFault = @{ Promotion = $false }

function Assert-True([bool]$condition, [string]$message) {
    if (!$condition) { throw $message }
}
function Write-Manifest([string]$folder) {
    $lines = foreach ($file in Get-ChildItem -LiteralPath $folder -File -Recurse | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $relative = $file.FullName.Substring($folder.Length + 1).Replace('\', '/')
        "$hash  $relative"
    }
    $lines | Set-Content -LiteralPath (Join-Path $folder 'SHA256SUMS.txt') -Encoding ASCII
}
function New-Package([string]$name, [string]$version = 'first') {
    $folder = Join-Path $testRoot $name
    New-Item -ItemType Directory -Path (Join-Path $folder 'payload') -Force | Out-Null
    Copy-Item -LiteralPath $installer -Destination (Join-Path $folder 'Install-Manager.ps1')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.cmd') -Destination (Join-Path $folder 'Install.cmd')
    # Deliberately non-executable fixtures: no app, game or graphics window is launched.
    "manager fixture $version" | Set-Content -LiteralPath (Join-Path $folder 'Dlss5AmdSwapper.exe')
    "payload fixture $version" | Set-Content -LiteralPath (Join-Path $folder 'payload\bridge.txt')
    '' | Set-Content -LiteralPath (Join-Path $folder 'SpecialK.deny.Dlss5AmdSwapper')
    Write-Manifest $folder
    return $folder
}
function Invoke-Install([string]$folder, [switch]$VerifyOnly) {
    & (Join-Path $folder 'Install-Manager.ps1') -NoLaunch -NoShortcuts -VerifyOnly:$VerifyOnly
}
function Assert-Rejected([scriptblock]$action, [string]$reason) {
    try { & $action } catch {
        if ($_.Exception.Message -notmatch $reason) { throw "Unexpected rejection: $($_.Exception.Message)" }
        return
    }
    throw 'The invalid package was accepted.'
}
function Test-Case([string]$name, [scriptblock]$action) {
    try {
        & $action
        $results.Add([pscustomobject]@{ name = $name; passed = $true })
        Write-Host "PASS $name"
    } catch {
        $results.Add([pscustomobject]@{ name = $name; passed = $false; error = $_.Exception.Message })
        Write-Host "FAIL $name : $($_.Exception.Message)"
    }
}
# Fault injection uses normal PowerShell command resolution only in this harness.
# The production installer has no test-only error switches.
function Move-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination)
    if ($packagingFault.Promotion -and (Split-Path $LiteralPath -Leaf) -like '.DLSS5-AMD-Swapper-stage-*') {
        $packagingFault.Promotion = $false
        throw 'Injected directory promotion failure.'
    }
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
}

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $env:LOCALAPPDATA = Join-Path $testRoot 'LocalAppData'
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\DLSS5 AMD Swapper'
    $package = New-Package 'release folder with spaces'
    Test-Case 'verify-only leaves the account untouched' {
        Invoke-Install $package -VerifyOnly
        Assert-True (!(Test-Path -LiteralPath $env:LOCALAPPDATA)) 'Verification created installation state.'
    }
    Test-Case 'Install.cmd forwards verification switches from a folder with spaces' {
        & (Join-Path $package 'Install.cmd') -VerifyOnly
        Assert-True ($LASTEXITCODE -eq 0) 'Install.cmd returned a failure exit code.'
        Assert-True (!(Test-Path -LiteralPath $env:LOCALAPPDATA)) 'Install.cmd lost the VerifyOnly switch.'
    }
    Test-Case 'first install copies and verifies the complete package without launching' {
        Invoke-Install $package
        foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse) {
            $target = Join-Path $installed $file.FullName.Substring($package.Length + 1)
            Assert-True ((Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $target).Hash) "Installed content differs: $target"
        }
    }
    Test-Case 'update removes stale payload and retains the complete previous directory' {
        'old payload' | Set-Content -LiteralPath (Join-Path $installed 'obsolete.dll')
        'user note' | Set-Content -LiteralPath (Join-Path $installed 'my-note.txt')
        $second = New-Package 'second release' 'second'
        Invoke-Install $second
        Assert-True (!(Test-Path -LiteralPath (Join-Path $installed 'obsolete.dll'))) 'Stale DLL remains in the active app.'
        $backups = @(Get-ChildItem -LiteralPath (Split-Path $installed -Parent) -Directory -Filter 'DLSS5 AMD Swapper.previous-*')
        Assert-True ($backups.Count -eq 1) 'Expected one retained previous installation.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $backups[0].FullName 'my-note.txt')) -eq 'user note') 'Unrecognized user file was not preserved.'
    }
    Test-Case 'promotion failure restores the previous installation and removes staging' {
        $before = (Get-FileHash -LiteralPath (Join-Path $installed 'Dlss5AmdSwapper.exe')).Hash
        $packagingFault.Promotion = $true
        Assert-Rejected { Invoke-Install $package } 'Injected directory promotion failure'
        $packagingFault.Promotion = $false
        Assert-True ((Get-FileHash -LiteralPath (Join-Path $installed 'Dlss5AmdSwapper.exe')).Hash -eq $before) 'Rollback did not restore the previous manager.'
        $staging = @(Get-ChildItem -LiteralPath (Split-Path $installed -Parent) -Force -Directory -Filter '.DLSS5-AMD-Swapper-stage-*')
        Assert-True ($staging.Count -eq 0) 'A failed staging directory remained.'
    }
    Test-Case 'damaged payload is rejected before changing the installed app' {
        $bad = New-Package 'damaged'
        'damaged' | Set-Content -LiteralPath (Join-Path $bad 'payload\bridge.txt')
        $before = (Get-FileHash -LiteralPath (Join-Path $installed 'Dlss5AmdSwapper.exe')).Hash
        Assert-Rejected { Invoke-Install $bad } 'Checksum mismatch'
        Assert-True ((Get-FileHash -LiteralPath (Join-Path $installed 'Dlss5AmdSwapper.exe')).Hash -eq $before) 'Rejected package changed installed manager.'
    }
    Test-Case 'incomplete ZIP is rejected' {
        $bad = New-Package 'incomplete'
        Remove-Item -LiteralPath (Join-Path $bad 'payload\bridge.txt')
        Assert-Rejected { Invoke-Install $bad -VerifyOnly } 'Missing package file'
    }
    Test-Case 'duplicate case-insensitive manifest entry is rejected' {
        $bad = New-Package 'duplicate'
        $hash = (Get-FileHash -LiteralPath (Join-Path $bad 'Dlss5AmdSwapper.exe')).Hash
        "$hash  DLSS5AMDSWAPPER.EXE" | Add-Content -LiteralPath (Join-Path $bad 'SHA256SUMS.txt')
        Assert-Rejected { Invoke-Install $bad -VerifyOnly } 'Duplicate'
    }
    Test-Case 'path traversal is rejected' {
        $bad = New-Package 'traversal'
        (('0' * 64) + '  ../outside.exe') | Add-Content -LiteralPath (Join-Path $bad 'SHA256SUMS.txt')
        Assert-Rejected { Invoke-Install $bad -VerifyOnly } 'Unsafe package entry'
    }
    Test-Case 'alternate data stream entry is rejected' {
        $bad = New-Package 'alternate-stream'
        (('0' * 64) + '  payload/bridge.txt:alternate') | Add-Content -LiteralPath (Join-Path $bad 'SHA256SUMS.txt')
        Assert-Rejected { Invoke-Install $bad -VerifyOnly } 'Unsafe package entry'
    }
    Test-Case 'unlisted executable is rejected' {
        $bad = New-Package 'unlisted'
        'unexpected fixture' | Set-Content -LiteralPath (Join-Path $bad 'unexpected.exe')
        Assert-Rejected { Invoke-Install $bad -VerifyOnly } 'Unlisted package file'
    }
    Test-Case 'reserved Windows file name is rejected' {
        $bad = New-Package 'reserved'
        (('0' * 64) + '  payload/CON.txt') | Add-Content -LiteralPath (Join-Path $bad 'SHA256SUMS.txt')
        Assert-Rejected { Invoke-Install $bad -VerifyOnly } 'Unsafe package entry'
    }
    Test-Case 'installer refuses to replace its own source directory' {
        Assert-Rejected { Invoke-Install $installed -VerifyOnly } 'freshly extracted release folder'
    }
    Test-Case 'directory junction in package is rejected' {
        $bad = New-Package 'linked-package'
        $link = Join-Path $bad 'linked'
        try {
            New-Item -ItemType Junction -Path $link -Target $package | Out-Null
            Assert-Rejected { Invoke-Install $bad -VerifyOnly } 'Linked package or installation paths'
        } finally {
            if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) }
        }
    }
    Test-Case 'directory junction in installation ancestors is rejected' {
        $redirect = Join-Path $testRoot 'redirected-account'
        New-Item -ItemType Directory -Path $redirect | Out-Null
        $link = Join-Path $redirect 'Programs'
        try {
            New-Item -ItemType Junction -Path $link -Target (Split-Path $installed -Parent) | Out-Null
            $env:LOCALAPPDATA = $redirect
            Assert-Rejected { Invoke-Install $package -VerifyOnly } 'Linked package or installation paths'
        } finally {
            $env:LOCALAPPDATA = Join-Path $testRoot 'LocalAppData'
            if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) }
        }
    }
} finally {
    $env:LOCALAPPDATA = $previousLocalAppData
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (!$resolved.StartsWith($tempParent + '\DLSS5-packaging-tests-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe packaging test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

$failed = @($results | Where-Object { !$_.passed })
if ($ResultFile) {
    [pscustomobject]@{ passed = $failed.Count -eq 0; tests = @($results.ToArray()); timestamp_utc = [DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultFile -Encoding UTF8
}
Write-Host "$($results.Count - $failed.Count)/$($results.Count) packaging regression tests passed."
if ($failed.Count) { throw 'Packaging regression tests failed.' }
