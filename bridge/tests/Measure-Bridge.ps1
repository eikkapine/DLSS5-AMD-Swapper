param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SourceHwnd,
    [int]$Width = 640,
    [int]$Height = 360,
    [int]$Seconds = 24,
    [int]$WarmupFrames = 120,
    [switch]$NativeResolution,
    [switch]$LiveSource,
    [string[]]$ExtraArguments = @()
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Use a fresh output directory: $OutputDirectory" }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$stagedExe = Join-Path $OutputDirectory 'DlssNrBridge.exe'
Copy-Item -LiteralPath $Executable -Destination $stagedExe
foreach ($asset in @('version.dll', 'nvngx_dlssnr.dll', 'dlssnr_on_amd_weights.bin', 'dlssnr_on_amd.ini', 'SpecialK.deny.DlssNrBridge', 'SpecialK.deny.DlssNrBridge.exe')) {
    $assetPath = Join-Path $RuntimeDirectory $asset
    if (Test-Path -LiteralPath $assetPath) { Copy-Item -LiteralPath $assetPath -Destination $OutputDirectory }
}
$readyPath = Join-Path $OutputDirectory 'ready.txt'
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $stagedExe
$startInfo.WorkingDirectory = $OutputDirectory
$startInfo.UseShellExecute = $false
$startInfo.Environment['HIP_VISIBLE_DEVICES'] = '1'
foreach ($argument in @('--source-hwnd', $SourceHwnd, '--width', "$Width", '--height', "$Height", '--seconds', "$Seconds", '--startup-delay-ms', '1000', '--warmup-frames', "$WarmupFrames", '--ready-file', $readyPath, '--capture-dir', $OutputDirectory)) {
    $startInfo.ArgumentList.Add($argument)
}
if ($NativeResolution) { $startInfo.ArgumentList.Add('--native-resolution') }
if (!$LiveSource) { $startInfo.ArgumentList.Add('--freeze-source') }
foreach ($argument in $ExtraArguments) { $startInfo.ArgumentList.Add($argument) }
$clock = [Diagnostics.Stopwatch]::StartNew()
$process = [Diagnostics.Process]::Start($startInfo)
$readyAt = $null
$readyJobs = 0
$jobSamples = [Collections.Generic.List[object]]::new()
$nextLogSample = 0.0
$logPath = Join-Path $OutputDirectory 'dlssnr_on_amd.log'
function Read-SharedLog {
    $stream = [IO.FileStream]::new($logPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}
function Get-LastJob {
    if (!(Test-Path -LiteralPath $logPath)) { return 0 }
    $jobMatches = [regex]::Matches((Read-SharedLog), 'network job (\d+) done')
    if ($jobMatches.Count -eq 0) { return 0 }
    return [int64]$jobMatches[$jobMatches.Count - 1].Groups[1].Value
}
try {
    while (!$process.WaitForExit(50)) {
        if ($null -eq $readyAt -and (Test-Path -LiteralPath $readyPath)) {
            $readyAt = $clock.Elapsed.TotalSeconds
            $readyJobs = Get-LastJob
        }
        if ($null -ne $readyAt -and $clock.Elapsed.TotalSeconds -ge $nextLogSample) {
            $jobIndex = Get-LastJob
            if ($jobIndex -ge 100 -and ($jobSamples.Count -eq 0 -or $jobIndex -ne $jobSamples[$jobSamples.Count - 1].jobs)) {
                $jobSamples.Add([pscustomobject]@{ jobs = $jobIndex; seconds = $clock.Elapsed.TotalSeconds })
            }
            $nextLogSample = $clock.Elapsed.TotalSeconds + 0.1
        }
        if ($clock.Elapsed.TotalSeconds -gt ($Seconds + 90)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Bridge exceeded bounded benchmark timeout'
        }
    }
    $elapsed = $clock.Elapsed.TotalSeconds
    if ($process.ExitCode -ne 0) { throw "Bridge exited with code $($process.ExitCode); inspect $OutputDirectory" }
    if ($null -eq $readyAt) { throw 'Bridge never published readiness' }
    $reportPath = Join-Path $OutputDirectory 'bridge-report.txt'
    $report = [IO.File]::ReadAllText($reportPath)
    $visibleFrames = [int64]([regex]::Match($report, '(?m)^visible_frames_presented=(\d+)').Groups[1].Value)
    $visibleSeconds = $elapsed - $readyAt
    $observedJobRate = if ($jobSamples.Count -ge 2) {
        [Math]::Round(($jobSamples[$jobSamples.Count - 1].jobs - $jobSamples[0].jobs) / ($jobSamples[$jobSamples.Count - 1].seconds - $jobSamples[0].seconds), 2)
    } else { $null }
    $log = if (Test-Path -LiteralPath $logPath) { Read-SharedLog } else { '' }
    $measurement = [ordered]@{
        exit_code = $process.ExitCode
        ready_seconds = [Math]::Round($readyAt, 3)
        visible_seconds = [Math]::Round($visibleSeconds, 3)
        visible_frames = $visibleFrames
        bridge_present_fps = [Math]::Round($visibleFrames / $visibleSeconds, 2)
        neural_job_index_at_ready = $readyJobs
        neural_job_index_at_exit = (Get-LastJob)
        neural_job_rate_estimate = [Math]::Round(((Get-LastJob) - $readyJobs) / $visibleSeconds, 2)
        neural_job_rate_observed = $observedJobRate
        neural_job_samples = @($jobSamples.ToArray())
        neural_rate_note = 'Runtime logs every 100 jobs; rate is approximate. Present rate is not unique neural FPS or game FPS.'
        healthy_runtime = ($log -match 'engine init ok' -and $log -match 'network job \d+ done' -and $log -notmatch '(?i)GPU errors|invalid kernel|FAULT|CRASH|100\.00%.*zero')
        zero_copy_runtime = ($log -match 'inputs shared \(zero-copy\), output shared \(zero-copy\)')
        source_hwnd = $SourceHwnd
        native_resolution = [bool]$NativeResolution
        live_source = [bool]$LiveSource
        width = $Width
        height = $Height
        executable_sha256 = (Get-FileHash -LiteralPath $stagedExe).Hash
        runtime_sha256 = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'version.dll')).Hash
        settings_sha256 = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'dlssnr_on_amd.ini')).Hash
    }
    $measurement | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'measurement.json')
    [pscustomobject]$measurement | Select-Object -Property * -ExcludeProperty neural_job_samples | ConvertTo-Json -Depth 4
}
finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}
