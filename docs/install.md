# Installation

NR Auto Scale is still a preview. The installer is designed to work with a normal private Lossless Scaling installation while keeping every paid/vendor file out of the public package.

## What you need

- Windows 11
- Lossless Scaling installed from an official source
- An AMD Radeon GPU supported by your chosen compatibility runtime
- AMD HIP 7.2 available for the current bridge compute path
- Your own `version.dll` from the AMD DLSS-NR compatibility project
- Your own `nvngx_dlssnr.dll`

The release package does not contain those external runtime files.

## Install the preview

1. Download the latest ZIP from the [GitHub Releases page](https://github.com/eikkapine/NR-Auto-Scale/releases).
2. Extract it somewhere outside the Lossless Scaling installation folder.
3. Close Lossless Scaling if it is running.
4. Run:

   ```powershell
   .\Setup.cmd
   ```

5. Select the Lossless Scaling install folder if setup cannot detect it.
6. Select your own `version.dll` when prompted.
7. Select your own `nvngx_dlssnr.dll` when prompted.
8. Choose the HIP device index for the AMD GPU you want to use.
9. Launch Lossless Scaling normally and press **Scale** on the source window you want to process.

`HIP_VISIBLE_DEVICES` is machine-specific. `0` is common on single-GPU systems, but the correct AMD index depends on how HIP enumerates the GPUs on that machine.

## Default processing mode

Fresh setup currently writes:

```ini
[AutoScale]
NativeResolution=0
Width=1280
Height=720
ReadyTimeoutMs=180000
DefaultScalingTypeIfOff=1
ForceCaptureApi=1
```

That keeps the bridge inside 1280×720 bounds while preserving source aspect ratio, then lets Lossless Scaling apply the scaler selected in its profile. If the profile scaler is Off, the wrapper can select LS1 through `DefaultScalingTypeIfOff=1`.

## Native-resolution mode

For 1:1 processing, run setup with:

```powershell
.\Setup.cmd -NativeResolution 1
```

or set this in the installed `NrAutoScale.ini`:

```ini
NativeResolution=1
```

In native mode the bridge uses the dimensions of the captured source and the fixed `Width`/`Height` values are ignored. The current bridge supports native dimensions up to 3840×2160 and stops if the source size changes during the session.

If NR Auto Scale is already installed, do not rerun the full installer just to switch modes. Stop scaling, close Lossless Scaling, edit `NrAutoScale.ini`, then reopen it.

## Live controls

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle original / processed output |
| `Ctrl+Alt+F7` | Reduce live blend by `0.1` |
| `Ctrl+Alt+F8` | Increase live blend by `0.1` |

These shortcuts are handled by the running bridge and apply immediately.

## Uninstall

Close Lossless Scaling first, then run the packaged uninstall script against the installation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Uninstall-AutoScale.ps1 -LsDir "<Lossless Scaling folder>"
```

The installer keeps a manifest and scoped backup so the uninstall path can restore the original local Lossless Scaling DLL that was moved aside during installation.

## Build from source

Requirements:

- Visual Studio 2022 with the C++ desktop workload
- CMake
- .NET 8 SDK for the optional controls helper
- Windows SDK with Direct3D 11/12 and Windows Graphics Capture headers

Build the bridge:

```powershell
.\bridge\build.ps1
```

Build the auto-scale proxy:

```powershell
.\auto-scale\build.ps1
```

Build the optional startup-config helper:

```powershell
dotnet build .\controls\DlssNrControl\DlssNrControl.csproj -c Release
```

From a source checkout, the interactive setup wrapper is:

```powershell
.\auto-scale\scripts\Setup.cmd
```

## Non-interactive setup

For development or scripted setup:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\auto-scale\scripts\Setup.ps1 `
  -NonInteractive `
  -LsDir "<Lossless Scaling folder>" `
  -ProxyVersionSource "<path to version.dll>" `
  -NrSource "<path to nvngx_dlssnr.dll>" `
  -HipVisibleDevices "0" `
  -NativeResolution 1
```

Use the HIP device index that actually selects your AMD GPU.

## What setup changes

Setup installs only the project wrapper/bridge and the external runtime files you explicitly select. It creates an `nr-bridge` runtime folder beside the Lossless Scaling installation files, preserves the original local Lossless Scaling DLL as a private backup/forwarding target, and writes `NrAutoScale.ini`.

The public repository and release ZIP must never contain:

- the paid Lossless Scaling original DLL or executables
- `Lossless_original.dll`
- Lossless Scaling assets/config copied from the paid app
- the AMD proxy binary or installer
- NVIDIA runtime/model/SDK files
- private logs, backups, machine paths, or runtime configuration
- screenshots beyond the two approved comparison crops already under `docs/images/`

See [Licensing](licensing.md) for the third-party boundary and [Verification](verification.md) for the current test status.
