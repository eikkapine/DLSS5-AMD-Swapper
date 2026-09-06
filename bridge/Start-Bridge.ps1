param(
    [switch]$Build,
    [int]$Width = 960,
    [int]$Height = 540,
    [int]$WarmupFrames = 320,
    [int]$StartupDelayMs = 2000,
    [string]$HipVisibleDevices = '1',
    [string]$VersionSource,
    [string]$NrSource,
    [switch]$NoProxy,
    [switch]$ListWindows,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$devDir = Split-Path -Parent $scriptDir
$installDir = Split-Path -Parent $devDir
$runtimeDir = Join-Path $scriptDir 'runtime'
$configuration = 'Release'
$buildScript = Join-Path $scriptDir 'build.ps1'

function Get-BridgeExecutable {
    $buildDir = Join-Path $scriptDir 'build'
    Get-ChildItem -Path $buildDir -Recurse -Filter 'DlssNrBridge.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\$configuration\\" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Resolve-ExistingPath([string]$Path, [string]$Description) {
    if (-not $Path) {
        throw "$Description path was empty."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found at: $Path"
    }
    (Resolve-Path -LiteralPath $Path).Path
}

function Write-BridgeConfig([string]$Path) {
    @'
[DlssNrOnAmd]
Enabled=1
UseFsrInputs=0
LocalStructure=1
LocalTone=0
SkinStructure=-1
Scale=.03125
'@ | Set-Content -LiteralPath $Path -Encoding ascii
}

function Initialize-Runtime([string]$ExePath) {
    New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

    $stagedExe = Join-Path $runtimeDir 'DlssNrBridge.exe'
    Copy-Item -LiteralPath $ExePath -Destination $stagedExe -Force

    if (-not $NoProxy) {
        $defaultVersion = Join-Path $installDir 'version.dll.bak'
        $defaultNr = Join-Path $installDir 'nvngx_dlssnr.dll'
        $resolvedVersion = Resolve-ExistingPath ($(if ($VersionSource) { $VersionSource } else { $defaultVersion })) 'AMD DLSS-NR proxy'
        $resolvedNr = Resolve-ExistingPath ($(if ($NrSource) { $NrSource } else { $defaultNr })) 'NVIDIA DLSS-NR runtime'

        Copy-Item -LiteralPath $resolvedVersion -Destination (Join-Path $runtimeDir 'version.dll') -Force
        Copy-Item -LiteralPath $resolvedNr -Destination (Join-Path $runtimeDir 'nvngx_dlssnr.dll') -Force
        Write-BridgeConfig (Join-Path $runtimeDir 'dlssnr_on_amd.ini')
    }

    New-Item -ItemType File -Force -Path (Join-Path $runtimeDir 'SpecialK.deny.DlssNrBridge') | Out-Null
    New-Item -ItemType File -Force -Path (Join-Path $runtimeDir 'SpecialK.deny.DlssNrBridge.exe') | Out-Null

    $stagedExe
}

Add-Type @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public sealed class BridgeWindowInfo
{
    public IntPtr Handle;
    public string Title = "";
    public string ProcessName = "";
    public int Width;
    public int Height;
    public override string ToString() { return Title; }
}

public static class BridgeWindowEnum
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    public static BridgeWindowInfo[] List()
    {
        var ownPid = Process.GetCurrentProcess().Id;
        var windows = new List<BridgeWindowInfo>();
        EnumWindows((hWnd, lParam) => {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;

            int titleLength = GetWindowTextLength(hWnd);
            if (titleLength <= 0) return true;

            if (!GetWindowRect(hWnd, out RECT rect)) return true;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == ownPid) return true;

            string processName = "";
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { processName = "pid-" + pid; }

            string loweredProcess = processName.ToLowerInvariant();
            if (loweredProcess == "dlssnrbridge" || loweredProcess == "conhost") return true;

            var title = new StringBuilder(titleLength + 1);
            GetWindowText(hWnd, title, title.Capacity);
            if (string.IsNullOrWhiteSpace(title.ToString())) return true;

            windows.Add(new BridgeWindowInfo {
                Handle = hWnd,
                Title = title.ToString(),
                ProcessName = processName,
                Width = width,
                Height = height
            });
            return true;
        }, IntPtr.Zero);

        windows.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        return windows.ToArray();
    }
}
'@

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Select-SourceWindow {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Select a window for DLSS NR Bridge'
    $form.StartPosition = 'CenterScreen'
    $form.Width = 860
    $form.Height = 520
    $form.MinimizeBox = $false
    $form.MaximizeBox = $false

    $label = New-Object System.Windows.Forms.Label
    $label.Text = "Pick the source window. The bridge starts at ${Width}x${Height}; HIP_VISIBLE_DEVICES=$HipVisibleDevices. Shortcuts: Ctrl+Alt+F6 toggle, Ctrl+Alt+F7 decrease, Ctrl+Alt+F8 increase."
    $label.AutoSize = $false
    $label.Left = 12
    $label.Top = 12
    $label.Width = 820
    $label.Height = 44

    $list = New-Object System.Windows.Forms.ListView
    $list.View = [System.Windows.Forms.View]::Details
    $list.FullRowSelect = $true
    $list.MultiSelect = $false
    $list.Left = 12
    $list.Top = 62
    $list.Width = 820
    $list.Height = 350
    [void]$list.Columns.Add('Title', 470)
    [void]$list.Columns.Add('Process', 130)
    [void]$list.Columns.Add('Size', 95)
    [void]$list.Columns.Add('HWND', 110)

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = 'Start Bridge'
    $ok.Left = 610
    $ok.Top = 430
    $ok.Width = 105
    $ok.Enabled = $false
    $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = 'Cancel'
    $cancel.Left = 727
    $cancel.Top = 430
    $cancel.Width = 105
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

    $refresh = New-Object System.Windows.Forms.Button
    $refresh.Text = 'Refresh'
    $refresh.Left = 12
    $refresh.Top = 430
    $refresh.Width = 90

    $populate = {
        $list.Items.Clear()
        foreach ($window in [BridgeWindowEnum]::List()) {
            $item = New-Object System.Windows.Forms.ListViewItem($window.Title)
            [void]$item.SubItems.Add($window.ProcessName)
            [void]$item.SubItems.Add("$($window.Width)x$($window.Height)")
            [void]$item.SubItems.Add(('0x{0:x}' -f $window.Handle.ToInt64()))
            $item.Tag = $window
            [void]$list.Items.Add($item)
        }
    }

    $list.Add_SelectedIndexChanged({ $ok.Enabled = $list.SelectedItems.Count -eq 1 })
    $list.Add_DoubleClick({ if ($list.SelectedItems.Count -eq 1) { $form.DialogResult = [System.Windows.Forms.DialogResult]::OK; $form.Close() } })
    $refresh.Add_Click($populate)

    [void]$form.Controls.AddRange(@($label, $list, $refresh, $ok, $cancel))
    $form.AcceptButton = $ok
    $form.CancelButton = $cancel
    & $populate

    $result = $form.ShowDialog()
    if ($result -ne [System.Windows.Forms.DialogResult]::OK -or $list.SelectedItems.Count -ne 1) {
        return $null
    }

    $list.SelectedItems[0].Tag
}

$exe = Get-BridgeExecutable
if ($Build -or -not $exe) {
    & $buildScript -Configuration $configuration
    $exe = Get-BridgeExecutable
}
if (-not $exe) {
    throw 'DlssNrBridge.exe is missing. Run bridge\build.ps1 and check the build output.'
}

if ($ListWindows) {
    [BridgeWindowEnum]::List() | ForEach-Object {
        'hwnd=0x{0:x} process={1} size={2}x{3} title="{4}"' -f $_.Handle.ToInt64(), $_.ProcessName, $_.Width, $_.Height, $_.Title
    }
    exit 0
}

if ($PrepareOnly) {
    $stagedExe = Initialize-Runtime $exe.FullName
    Write-Host "Prepared private bridge runtime at $runtimeDir"
    Write-Host "Staged executable: $stagedExe"
    exit 0
}

$sourceWindow = Select-SourceWindow
if (-not $sourceWindow) {
    Write-Host 'No source window selected.'
    exit 0
}

$stagedExe = Initialize-Runtime $exe.FullName
$arguments = @(
    '--source-hwnd', ('0x{0:x}' -f $sourceWindow.Handle.ToInt64()),
    '--width', $Width,
    '--height', $Height,
    '--warmup-frames', $WarmupFrames,
    '--startup-delay-ms', $StartupDelayMs
)
if ($NoProxy) {
    $arguments += '--no-proxy'
}

$process = New-Object System.Diagnostics.Process
$process.StartInfo = New-Object System.Diagnostics.ProcessStartInfo
$process.StartInfo.FileName = $stagedExe
$process.StartInfo.WorkingDirectory = $runtimeDir
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.Arguments = ($arguments | ForEach-Object {
    if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
}) -join ' '
$process.StartInfo.Environment['HIP_VISIBLE_DEVICES'] = $HipVisibleDevices

if (-not $process.Start()) {
    throw 'Failed to start DlssNrBridge.exe.'
}

Write-Host "Started DLSS NR Bridge for '$($sourceWindow.Title)' ($($sourceWindow.ProcessName)) as PID $($process.Id)."
Write-Host 'Point Lossless Scaling at the DLSS NR Bridge window when you are ready to test the visible output.'
