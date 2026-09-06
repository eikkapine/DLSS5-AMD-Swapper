param(
    [int]$Frames = 700,
    [int]$Width = 640,
    [int]$Height = 360,
    [int]$Seconds = 25,
    [int]$Interop = 1,
    [ValidateSet(0, 1)]
    [int]$Inline = 0,
    [int]$StartupDelayMs = 1000,
    [string]$HipVisibleDevices,
    [switch]$Visible,
    [string]$Config = "Release",
    [string]$VersionSource,
    [string]$NrSource,
    [string]$ValidateRun
)

$ErrorActionPreference = "Stop"

$probeRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$workspaceRoot = Resolve-Path -LiteralPath (Join-Path $probeRoot "..\..")
$buildDir = Join-Path $probeRoot "build-msvc"
$runRoot = if ($ValidateRun) { (Resolve-Path -LiteralPath $ValidateRun).Path } else { Join-Path $probeRoot ("runs\" + (Get-Date -Format "yyyyMMdd-HHmmss")) }

function Get-ReportNumber {
    param([string]$Text, [string]$Name)
    if ($Text -match "(?m)^$([regex]::Escape($Name))=(\d+)") { return [int64]$Matches[1] }
    return $null
}

function New-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    [pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail }
}

function Read-PpmPayloadStats {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $index = 0
    $tokens = New-Object System.Collections.Generic.List[string]
    while ($tokens.Count -lt 4 -and $index -lt $bytes.Length) {
        while ($index -lt $bytes.Length -and [char]$bytes[$index] -match '\s') { $index++ }
        if ($index -lt $bytes.Length -and $bytes[$index] -eq [byte][char]'#') {
            while ($index -lt $bytes.Length -and $bytes[$index] -ne 10) { $index++ }
            continue
        }
        $start = $index
        while ($index -lt $bytes.Length -and !([char]$bytes[$index] -match '\s')) { $index++ }
        if ($index -gt $start) { $tokens.Add([System.Text.Encoding]::ASCII.GetString($bytes, $start, $index - $start)) }
    }
    if ($tokens.Count -ne 4 -or $tokens[0] -ne "P6") { throw "Unsupported or malformed PPM: ${Path}" }
    if ($index -lt $bytes.Length -and [char]$bytes[$index] -match '\s') { $index++ }

    $width = [int]$tokens[1]
    $height = [int]$tokens[2]
    $maxValue = [int]$tokens[3]
    $expectedBytes = $width * $height * 3
    $payloadBytes = $bytes.Length - $index
    if ($maxValue -ne 255 -or $payloadBytes -lt $expectedBytes) {
        throw "Unexpected PPM payload in ${Path}: max=$maxValue payload=$payloadBytes expected=$expectedBytes"
    }

    $first = $bytes[$index]
    $nonZeroCount = 0
    $differentFromFirstCount = 0
    for ($i = $index; $i -lt ($index + $expectedBytes); $i++) {
        if ($bytes[$i] -ne 0) { $nonZeroCount++ }
        if ($bytes[$i] -ne $first) { $differentFromFirstCount++ }
    }

    [pscustomobject]@{
        path = $Path
        width = $width
        height = $height
        payload_bytes = $expectedBytes
        nonzero_bytes = $nonZeroCount
        differs_from_first_byte = $differentFromFirstCount
        is_nonzero = ($nonZeroCount -gt 0)
        is_nonconstant = ($differentFromFirstCount -gt 0)
    }
}

function Get-CaptureFrameNumber {
    param([System.IO.FileInfo]$File)
    if ($File.Name -match '^capture_frame(\d+)_buffer\d+\.ppm$') { return [int]$Matches[1] }
    return -1
}

function Get-LateCaptureFiles {
    param([string]$Dir, [int]$MinimumFrame = 450)
    if (!(Test-Path -LiteralPath $Dir)) { return @() }
    @(Get-ChildItem -LiteralPath $Dir -Filter "capture_frame*_buffer*.ppm" -File |
        Where-Object { (Get-CaptureFrameNumber -File $_) -ge $MinimumFrame } |
        Sort-Object @{ Expression = { Get-CaptureFrameNumber -File $_ } }, Name)
}

function Test-FileBytesEqual {
    param([string]$Left, [string]$Right)
    if (!(Test-Path -LiteralPath $Left) -or !(Test-Path -LiteralPath $Right)) { return $false }
    $leftBytes = [System.IO.File]::ReadAllBytes($Left)
    $rightBytes = [System.IO.File]::ReadAllBytes($Right)
    if ($leftBytes.Length -ne $rightBytes.Length) { return $false }
    for ($i = 0; $i -lt $leftBytes.Length; $i++) {
        if ($leftBytes[$i] -ne $rightBytes[$i]) { return $false }
    }
    return $true
}

function Invoke-ProbeValidation {
    param(
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [int]$StartupDelayMs,
        [int]$Frames,
        [int]$Seconds,
        [bool]$Visible,
        [int]$Interop,
        [int]$Inline,
        [string]$HipVisibleDevices
    )

    $summary = Join-Path $RunRoot "compare-summary.txt"
    $evidencePath = Join-Path $RunRoot "evidence.json"
    $offReport = Join-Path $RunRoot "off\report.txt"
    $onReport = Join-Path $RunRoot "on\report.txt"
    $offLog = Join-Path $RunRoot "off\dlssnr_on_amd.log"
    $onLog = Join-Path $RunRoot "on\dlssnr_on_amd.log"
    $offTimeout = Join-Path $RunRoot "off\probe-timeout.txt"
    $onTimeout = Join-Path $RunRoot "on\probe-timeout.txt"
    $offExpected = Join-Path $RunRoot "off\expected.ppm"
    $onExpected = Join-Path $RunRoot "on\expected.ppm"

    $offReportText = if (Test-Path -LiteralPath $offReport) { Get-Content -LiteralPath $offReport -Raw } else { "missing" }
    $onReportText = if (Test-Path -LiteralPath $onReport) { Get-Content -LiteralPath $onReport -Raw } else { "missing" }
    $offLogText = if (Test-Path -LiteralPath $offLog) { (Get-Content -LiteralPath $offLog -Tail 40) -join [Environment]::NewLine } else { "missing" }
    $onLogText = if (Test-Path -LiteralPath $onLog) { (Get-Content -LiteralPath $onLog -Tail 80) -join [Environment]::NewLine } else { "missing" }
    $offFullLogText = if (Test-Path -LiteralPath $offLog) { Get-Content -LiteralPath $offLog -Raw } else { "" }
    $onFullLogText = if (Test-Path -LiteralPath $onLog) { Get-Content -LiteralPath $onLog -Raw } else { "" }
    $combinedLogText = $offFullLogText + [Environment]::NewLine + $onFullLogText

    $fatalRuntimePattern = '(?is)(GPU errors|invalid kernel file|FAULT|CRASH|100\.00%.*zero)'
    $jobPattern = '(?i)network job.*done'
    $onJobCount = ([regex]::Matches($onFullLogText, $jobPattern)).Count
    $offReadiedDiff = Get-ReportNumber -Text $offReportText -Name "max_readied_changed_pixels_vs_expected"
    $onReadiedDiff = Get-ReportNumber -Text $onReportText -Name "max_readied_changed_pixels_vs_expected"
    $onReadiedMaxDelta = Get-ReportNumber -Text $onReportText -Name "max_readied_channel_delta_vs_expected"
    $hasFatalRuntimeLog = $combinedLogText -match $fatalRuntimePattern

    $lateThreshold = if ($Frames -ge 450) { 450 } else { [Math]::Max(2, $Frames - 10) }
    $offLateCaptures = Get-LateCaptureFiles -Dir (Join-Path $RunRoot "off") -MinimumFrame $lateThreshold
    $onLateCaptures = Get-LateCaptureFiles -Dir (Join-Path $RunRoot "on") -MinimumFrame $lateThreshold

    $offExpectedMatches = @()
    foreach ($capture in $offLateCaptures) { $offExpectedMatches += (Test-FileBytesEqual -Left $capture.FullName -Right $offExpected) }
    $offAllLateMatchExpected = ($offLateCaptures.Count -gt 0 -and ($offExpectedMatches | Where-Object { !$_ }).Count -eq 0)

    $onPayloadStats = @()
    foreach ($capture in $onLateCaptures) { $onPayloadStats += Read-PpmPayloadStats -Path $capture.FullName }
    $onAllLateNonZero = ($onPayloadStats.Count -gt 0 -and ($onPayloadStats | Where-Object { !$_.is_nonzero }).Count -eq 0)
    $onAllLateNonConstant = ($onPayloadStats.Count -gt 0 -and ($onPayloadStats | Where-Object { !$_.is_nonconstant }).Count -eq 0)

    $checks = @(
        (New-Check -Name "off_report_present" -Passed (Test-Path -LiteralPath $offReport) -Detail $offReport),
        (New-Check -Name "on_report_present" -Passed (Test-Path -LiteralPath $onReport) -Detail $onReport),
        (New-Check -Name "off_log_present" -Passed (Test-Path -LiteralPath $offLog) -Detail $offLog),
        (New-Check -Name "on_log_present" -Passed (Test-Path -LiteralPath $onLog) -Detail $onLog),
        (New-Check -Name "off_expected_present" -Passed (Test-Path -LiteralPath $offExpected) -Detail $offExpected),
        (New-Check -Name "on_expected_present" -Passed (Test-Path -LiteralPath $onExpected) -Detail $onExpected),
        (New-Check -Name "off_late_captures_present" -Passed ($offLateCaptures.Count -gt 0) -Detail "late_capture_count=$($offLateCaptures.Count)"),
        (New-Check -Name "on_late_captures_present" -Passed ($onLateCaptures.Count -gt 0) -Detail "late_capture_count=$($onLateCaptures.Count)"),
        (New-Check -Name "off_not_timed_out" -Passed (!(Test-Path -LiteralPath $offTimeout)) -Detail $offTimeout),
        (New-Check -Name "on_not_timed_out" -Passed (!(Test-Path -LiteralPath $onTimeout)) -Detail $onTimeout),
        (New-Check -Name "no_runtime_error_markers" -Passed (!$hasFatalRuntimeLog) -Detail "Fails on GPU errors, invalid kernel file, FAULT, CRASH, or 100.00% zero-output logs"),
        (New-Check -Name "on_jobs_completed" -Passed ($onJobCount -gt 0) -Detail "network_job_done_count=$onJobCount"),
        (New-Check -Name "off_readied_diff_zero" -Passed ($null -ne $offReadiedDiff -and $offReadiedDiff -eq 0) -Detail "off_max_readied_changed_pixels=$offReadiedDiff"),
        (New-Check -Name "off_late_captures_match_expected" -Passed $offAllLateMatchExpected -Detail "late_capture_count=$($offLateCaptures.Count)"),
        (New-Check -Name "on_readied_diff_positive" -Passed ($null -ne $onReadiedDiff -and $onReadiedDiff -gt 0) -Detail "on_max_readied_changed_pixels=$onReadiedDiff"),
        (New-Check -Name "on_late_captures_nonzero" -Passed $onAllLateNonZero -Detail "late_capture_count=$($onPayloadStats.Count)"),
        (New-Check -Name "on_late_captures_nonconstant" -Passed $onAllLateNonConstant -Detail "late_capture_count=$($onPayloadStats.Count)")
    )

    $validationPassed = ($checks | Where-Object { !$_.passed }).Count -eq 0
    $validationStatus = if ($validationPassed) { "standalone_candidate" } else { "failed" }
    $script:ProbeValidationExitCode = if ($validationPassed) { 0 } else { 2 }
    $evidence = [pscustomobject]@{
        status = $validationStatus
        run_root = $RunRoot
        standalone_candidate = $validationPassed
        full_lossless_scaling_verified = $false
        startup_delay_ms = $StartupDelayMs
        frames = $Frames
        seconds = $Seconds
        visible = $Visible
        interop = $Interop
        inline = $Inline
        hip_visible_devices = $HipVisibleDevices
        on_network_job_done_count = $onJobCount
        off_max_readied_changed_pixels = $offReadiedDiff
        on_max_readied_changed_pixels = $onReadiedDiff
        on_max_readied_channel_delta = $onReadiedMaxDelta
        off_late_capture_count = $offLateCaptures.Count
        on_late_capture_count = $onLateCaptures.Count
        on_late_payload_stats = $onPayloadStats
        checks = $checks
    }
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8

    @(
        "Run root: $RunRoot"
        "Validation status: $validationStatus"
        "Evidence JSON: $evidencePath"
        "HIP_VISIBLE_DEVICES: $HipVisibleDevices"
        "on_network_job_done_count=$onJobCount"
        "off_late_capture_count=$($offLateCaptures.Count)"
        "on_late_capture_count=$($onLateCaptures.Count)"
        "on_late_all_nonzero=$onAllLateNonZero"
        "on_late_all_nonconstant=$onAllLateNonConstant"
        "off_late_all_match_expected=$offAllLateMatchExpected"
        ""
        "[off report]"
        $offReportText
        ""
        "[on report]"
        $onReportText
        ""
        "[off proxy log tail]"
        $offLogText
        ""
        "[on proxy log tail]"
        $onLogText
    ) | Set-Content -LiteralPath $summary -Encoding UTF8

    Get-Content -LiteralPath $summary | ForEach-Object { Write-Host $_ }
    if (!$validationPassed) { return 2 }
    return 0
}

if ($ValidateRun) {
    Invoke-ProbeValidation -RunRoot $runRoot -StartupDelayMs $StartupDelayMs -Frames $Frames -Seconds $Seconds -Visible ([bool]$Visible) -Interop $Interop -Inline $Inline -HipVisibleDevices $HipVisibleDevices
    if ($script:ProbeValidationExitCode -ne 0) { exit 2 } else { exit 0 }
}

if (!$VersionSource) { $VersionSource = Join-Path $workspaceRoot "version.dll.bak" }
if (!$NrSource) { $NrSource = Join-Path $workspaceRoot "nvngx_dlssnr.dll" }
if (!(Test-Path -LiteralPath $VersionSource)) { throw "Missing $VersionSource" }
if (!(Test-Path -LiteralPath $NrSource)) { throw "Missing $NrSource" }

$versionSourcePath = (Resolve-Path -LiteralPath $VersionSource).Path
$nrSourcePath = (Resolve-Path -LiteralPath $NrSource).Path
$vcvarsCandidates = @(
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
)
$vcvars = $vcvarsCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$vcvars) { throw "MSVC vcvars64.bat not found" }

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
cmd /c "`"$vcvars`" && cmake -S `"$probeRoot`" -B `"$buildDir`" -G `"NMake Makefiles`" -DCMAKE_BUILD_TYPE=$Config"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed with exit code $LASTEXITCODE" }
cmd /c "`"$vcvars`" && cmake --build `"$buildDir`" --config $Config"
if ($LASTEXITCODE -ne 0) { throw "CMake build failed with exit code $LASTEXITCODE" }

$exe = Join-Path $buildDir "dlssnr_d3d12_probe.exe"
if (!(Test-Path -LiteralPath $exe)) { throw "Build did not produce $exe" }

function New-ProbeRun {
    param([string]$Name, [int]$Enabled)

    $dir = Join-Path $runRoot $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $dir "dlssnr_d3d12_probe.exe") -Force
    Copy-Item -LiteralPath $versionSourcePath -Destination (Join-Path $dir "version.dll") -Force
    Copy-Item -LiteralPath $nrSourcePath -Destination (Join-Path $dir "nvngx_dlssnr.dll") -Force
    $weightsSource = Join-Path $workspaceRoot "dlssnr_on_amd_weights.bin"
    if (Test-Path -LiteralPath $weightsSource) {
        Copy-Item -LiteralPath $weightsSource -Destination (Join-Path $dir "dlssnr_on_amd_weights.bin") -Force
    }
    New-Item -ItemType File -Force -Path (Join-Path $dir "SpecialK.deny.dlssnr_d3d12_probe") | Out-Null
    New-Item -ItemType File -Force -Path (Join-Path $dir "SpecialK.deny.dlssnr_d3d12_probe.exe") | Out-Null

    @"
[DlssNrOnAmd]
Enabled=$Enabled
UseFsrInputs=0
HipDevice=-1
Inline=$Inline
Scale=0.03125
LocalStructure=1
LocalTone=0
SkinStructure=-1
"@ | Set-Content -LiteralPath (Join-Path $dir "dlssnr_on_amd.ini") -Encoding ASCII
    if ($Enabled -eq 1 -and $Interop -ge 0) {
        Add-Content -LiteralPath (Join-Path $dir "dlssnr_on_amd.ini") -Value "Interop=$Interop" -Encoding ASCII
    }

    Push-Location -LiteralPath $dir
    try {
        $probeArgs = @("--proxy", "--frames", "$Frames", "--seconds", "$Seconds", "--startup-delay-ms", "$StartupDelayMs", "--width", "$Width", "--height", "$Height", "--out", ".")
        if ($Visible) { $probeArgs += "--visible" }

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = Join-Path $dir "dlssnr_d3d12_probe.exe"
        $startInfo.WorkingDirectory = $dir
        $startInfo.UseShellExecute = $false
        $startInfo.Arguments = ($probeArgs -join " ")
        if (!$Visible) {
            $startInfo.CreateNoWindow = $true
            $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        }
        if ($HipVisibleDevices) { $startInfo.EnvironmentVariables["HIP_VISIBLE_DEVICES"] = $HipVisibleDevices }

        $process = [System.Diagnostics.Process]::Start($startInfo)
        $deadline = (Get-Date).AddSeconds($Seconds + 20)
        while (!$process.HasExited -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 250
            $process.Refresh()
        }
        if (!$process.HasExited) {
            $timeoutPath = Join-Path $dir "probe-timeout.txt"
            "timeout_after_seconds=$($Seconds + 20)" | Set-Content -LiteralPath $timeoutPath -Encoding ASCII
            "pid=$($process.Id)" | Add-Content -LiteralPath $timeoutPath -Encoding ASCII
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Write-Warning "$Name timed out after $($Seconds + 20) seconds and was terminated"
            return
        }
        if ($process.ExitCode -ne 0) { Write-Warning "$Name exited with code $($process.ExitCode)" }
    }
    finally {
        Pop-Location
    }
}

New-ProbeRun -Name "off" -Enabled 0
New-ProbeRun -Name "on" -Enabled 1
Invoke-ProbeValidation -RunRoot $runRoot -StartupDelayMs $StartupDelayMs -Frames $Frames -Seconds $Seconds -Visible ([bool]$Visible) -Interop $Interop -Inline $Inline -HipVisibleDevices $HipVisibleDevices
if ($script:ProbeValidationExitCode -ne 0) { exit 2 } else { exit 0 }
