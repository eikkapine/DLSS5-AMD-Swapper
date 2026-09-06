param(
    [int]$Frames = 700,
    [int]$Width = 640,
    [int]$Height = 360,
    [int]$Seconds = 25,
    [int]$StartupDelayMs = 2000,
    [switch]$Visible,
    [switch]$SkipExecution,
    [switch]$FsrOnly,
    [string]$Config = "Release",
    [string]$OptiScalerArchive = "",
    [string]$VersionSource = "",
    [string]$NrSource = ""
)

$ErrorActionPreference = "Stop"

$probeRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$workspaceRoot = Resolve-Path -LiteralPath (Join-Path $probeRoot "..\..")
$buildDir = Join-Path $probeRoot "build-msvc"
$runRoot = Join-Path $probeRoot ("runs\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
if (!$VersionSource) {
    $VersionSource = Join-Path $workspaceRoot "version.dll.bak"
}
if (!$NrSource) {
    $NrSource = Join-Path $workspaceRoot "nvngx_dlssnr.dll"
}

if (!$SkipExecution -and !$OptiScalerArchive) {
    throw "Missing OptiScaler archive. Pass -OptiScalerArchive with the local zip path."
}
if ($OptiScalerArchive -and !(Test-Path -LiteralPath $OptiScalerArchive)) {
    throw "Missing OptiScaler archive: $OptiScalerArchive"
}
if (!$FsrOnly -and !(Test-Path -LiteralPath $VersionSource)) {
    throw "Missing $VersionSource"
}
if (!$FsrOnly -and !(Test-Path -LiteralPath $NrSource)) {
    throw "Missing $NrSource"
}

$vcvarsCandidates = @(
    "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
)
$vcvars = $vcvarsCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$vcvars) {
    throw "MSVC vcvars64.bat not found"
}

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
cmd /c "`"$vcvars`" && cmake -S `"$probeRoot`" -B `"$buildDir`" -G `"NMake Makefiles`" -DCMAKE_BUILD_TYPE=$Config"
if ($LASTEXITCODE -ne 0) {
    throw "CMake configure failed with exit code $LASTEXITCODE"
}
cmd /c "`"$vcvars`" && cmake --build `"$buildDir`" --config $Config"
if ($LASTEXITCODE -ne 0) {
    throw "CMake build failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $buildDir "dlssnr_fsr_probe.exe"
if (!(Test-Path -LiteralPath $exe)) {
    throw "Build did not produce $exe"
}

function Expand-NeededRuntime {
    param([string]$Destination)

    if (!$OptiScalerArchive) {
        return
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $needed = @(
        "OptiScaler/amd_fidelityfx_loader_dx12.dll",
        "OptiScaler/amd_fidelityfx_upscaler_dx12.dll"
    )
    $zip = [IO.Compression.ZipFile]::OpenRead($OptiScalerArchive)
    try {
        foreach ($entryName in $needed) {
            $entry = $zip.Entries | Where-Object { ($_.FullName -replace '\\','/') -eq $entryName } | Select-Object -First 1
            if (!$entry) {
                throw "Missing $entryName inside $OptiScalerArchive"
            }
            $target = Join-Path $Destination ([IO.Path]::GetFileName($entryName))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally {
        $zip.Dispose()
    }
}

function New-ProbeRun {
    param(
        [string]$Name,
        [bool]$Proxy,
        [bool]$UseFsrInputs,
        [bool]$FsrOnly
    )

    $dir = Join-Path $runRoot $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $dir "dlssnr_fsr_probe.exe") -Force
    if (!$SkipExecution) {
        Expand-NeededRuntime -Destination $dir
    }
    New-Item -ItemType File -Force -Path (Join-Path $dir "SpecialK.deny.dlssnr_fsr_probe") | Out-Null
    New-Item -ItemType File -Force -Path (Join-Path $dir "SpecialK.deny.dlssnr_fsr_probe.exe") | Out-Null

    if ($Proxy) {
        Copy-Item -LiteralPath $VersionSource -Destination (Join-Path $dir "version.dll") -Force
        Copy-Item -LiteralPath $NrSource -Destination (Join-Path $dir "nvngx_dlssnr.dll") -Force
    }

    $enabled = if ($Proxy) { 1 } else { 0 }
    $useFsrValue = if ($UseFsrInputs) { 1 } else { 0 }
    @"
[DlssNrOnAmd]
Enabled=$enabled
UseFsrInputs=$useFsrValue
HipDevice=-1
Inline=0
Scale=0.03125
LocalStructure=1
LocalTone=0
SkinStructure=-1
"@ | Set-Content -LiteralPath (Join-Path $dir "dlssnr_on_amd.ini") -Encoding ASCII

    if ($SkipExecution) {
        "execution_skipped=1" | Set-Content -LiteralPath (Join-Path $dir "report.txt") -Encoding ASCII
        return
    }

    Push-Location -LiteralPath $dir
    try {
        $args = @("--fsr", "--frames", "$Frames", "--seconds", "$Seconds", "--width", "$Width", "--height", "$Height", "--out", ".")
        if ($Proxy) {
            $args += "--proxy"
            $args += "--startup-delay-ms"
            $args += "$StartupDelayMs"
        }
        if ($UseFsrInputs) {
            $args += "--use-fsr-inputs"
        }
        if ($Visible) {
            $args += "--visible"
            $process = Start-Process -FilePath (Join-Path $dir "dlssnr_fsr_probe.exe") -ArgumentList $args -PassThru
        } else {
            $process = Start-Process -FilePath (Join-Path $dir "dlssnr_fsr_probe.exe") -ArgumentList $args -PassThru -WindowStyle Hidden
        }
        $watchdogSeconds = $Seconds + 20
        $completed = $process.WaitForExit($watchdogSeconds * 1000)
        if (!$completed) {
            $timeoutReport = Join-Path $dir "timeout-report.txt"
            @(
                "timeout=1"
                "watchdog_seconds=$watchdogSeconds"
                "process_id=$($process.Id)"
                "name=$Name"
            ) | Set-Content -LiteralPath $timeoutReport -Encoding ASCII
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Write-Warning "$Name timed out after $watchdogSeconds seconds and was terminated"
            return
        }
        if ($process.ExitCode -ne 0) {
            Write-Warning "$Name exited with code $($process.ExitCode)"
        }
    }
    finally {
        Pop-Location
    }
}

$runNames = @("fsr-only")
New-ProbeRun -Name "fsr-only" -Proxy $false -UseFsrInputs $false -FsrOnly $true
if (!$FsrOnly) {
    $runNames += "proxy-fsr-inputs"
    New-ProbeRun -Name "proxy-fsr-inputs" -Proxy $true -UseFsrInputs $true -FsrOnly $false
}

$summary = Join-Path $runRoot "compare-summary.txt"
$parts = @("Run root: $runRoot", "skip_execution=$([int]$SkipExecution.IsPresent)")
foreach ($name in $runNames) {
    $report = Join-Path $runRoot "$name\report.txt"
    $timeoutReport = Join-Path $runRoot "$name\timeout-report.txt"
    $proxyLog = Join-Path $runRoot "$name\dlssnr_on_amd.log"
    $ffxLog = Join-Path $runRoot "$name\ffx-debug.log"
    $parts += ""
    $parts += "[$name report]"
    $parts += if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw } else { "missing" }
    $parts += "[$name timeout report]"
    $parts += if (Test-Path -LiteralPath $timeoutReport) { Get-Content -LiteralPath $timeoutReport -Raw } else { "missing" }
    $parts += "[$name proxy log tail]"
    $parts += if (Test-Path -LiteralPath $proxyLog) { (Get-Content -LiteralPath $proxyLog -Tail 80) -join [Environment]::NewLine } else { "missing" }
    $parts += "[$name ffx debug tail]"
    $parts += if (Test-Path -LiteralPath $ffxLog) { (Get-Content -LiteralPath $ffxLog -Tail 80) -join [Environment]::NewLine } else { "missing" }
}

$parts | Set-Content -LiteralPath $summary -Encoding UTF8
Get-Content -LiteralPath $summary
