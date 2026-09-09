# Installation

NR Auto Scale is still a preview. The installer is designed to work with a normal private Lossless Scaling installation while keeping every paid/vendor file out of the public package.

## What you need

- Windows 11
- Lossless Scaling installed from an official source
- An AMD Radeon GPU supported by your chosen compatibility runtime
- AMD HIP available for the current bridge compute path
- Your own `version.dll` from the AMD DLSS-NR compatibility project
- Your own `nvngx_dlssnr.dll`

The release package does not contain those external runtime files.

## Install the preview

1. Download the latest ZIP from the [GitHub Releases page](https://github.com/eikkapine/NR-Auto-Scale/releases).
2. Extract it somewhere outside the Lossless Scaling installation folder.
3. Close Lossless Scaling if it is running.
4. Run `Setup.cmd`.
5. Select the Lossless Scaling install folder if setup cannot detect it.
6. Select your own `version.dll` when prompted.
7. Select your own `nvngx_dlssnr.dll` when prompted.
8. Choose the HIP device index for the AMD GPU you want to use.
9. Launch Lossless Scaling normally and press **Scale** on the source window you want to process.

`HIP_VISIBLE_DEVICES` is machine-specific. The correct AMD index depends on how HIP enumerates the GPUs on that machine.

## Default performance mode

New setup writes:

```ini
[AutoScale]
NativeResolution=0
WorkingScale=0.75
Width=1280
Height=720
ReadyTimeoutMs=180000
DefaultScalingTypeIfOff=1
ForceCaptureApi=1
```

`WorkingScale=0.75` takes 75% of each source axis for the neural working image. A 2560×1440 source therefore runs NR at 1920×1080, then Lossless Scaling applies the selected scaler. If the selected scaler is Off, `DefaultScalingTypeIfOff=1` can select LS1.

Existing installations without a `WorkingScale` key keep their previous behavior. To opt into the new path, add:

```ini
WorkingScale=0.75
```

while Lossless Scaling is closed.

## Native-resolution mode

To return to 1:1 neural processing, disable the working scale and enable native mode:

```ini
WorkingScale=0
NativeResolution=1
```

or run setup with:

```powershell
.\Setup.cmd -WorkingScale 0 -NativeResolution 1
```

The bridge currently supports native dimensions up to 3840×2160 and stops if the source size changes during a native session.

## Fixed-size fallback

With both source-relative and native modes disabled:

```ini
WorkingScale=0
NativeResolution=0
Width=1280
Height=720
```

the bridge uses the explicit fixed bounds.

## Live controls

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle original / processed output |
| `Ctrl+Alt+F7` | Reduce live blend by `0.1` |
| `Ctrl+Alt+F8` | Increase live blend by `0.1` |

## Uninstall

Close Lossless Scaling first, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Uninstall-AutoScale.ps1 -LsDir "<Lossless Scaling folder>"
```

The installer uses its local manifest/backups to restore the original private Lossless Scaling DLL.

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

Source-tree setup:

```powershell
.\auto-scale\scripts\Setup.cmd
```

## Non-interactive setup

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\auto-scale\scripts\Setup.ps1 `
  -NonInteractive `
  -LsDir "<Lossless Scaling folder>" `
  -ProxyVersionSource "<path to version.dll>" `
  -NrSource "<path to nvngx_dlssnr.dll>" `
  -HipVisibleDevices "0" `
  -WorkingScale 0.75
```

## What setup changes

Setup installs only the project wrapper/bridge and the external runtime files you explicitly select. It creates an `nr-bridge` runtime folder beside the Lossless Scaling installation files, preserves the original local Lossless Scaling DLL as a private backup/forwarding target, and writes `NrAutoScale.ini`.

The public repository and release ZIP must never contain:

- the paid Lossless Scaling original DLL or executables
- `Lossless_original.dll`
- Lossless Scaling assets/config copied from the paid app
- the AMD proxy binary or installer
- NVIDIA runtime/model/SDK files
- private logs, backups, config, machine paths, or runtime configuration
- screenshots beyond the two already reviewed comparison crops under `docs/images/`

See [Licensing](licensing.md) for the third-party boundary and [Verification](verification.md) for the current test status.
