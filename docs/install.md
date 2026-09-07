# Install And Build

This project provides experimental tooling for an AMD DLSS Neural Rendering compatibility runtime with Lossless Scaling. Current source defaults to **1280×720 processing** with optional native mode. The older `v0.1.0-pre.1` binary preview predates this default and the performance update; build from source for those changes. Direct Scale-button and full native-mode app verification remain incomplete.

## Install The Preview

1. Install Lossless Scaling from its official store page.
2. Download `v0.1.0-pre.1` from <https://github.com/eikkapine/NR-Auto-Scale/releases/tag/v0.1.0-pre.1>.
3. Extract the release ZIP outside the Lossless Scaling folder.
4. Run setup from the extracted release folder:

   ```powershell
   .\Setup.cmd
   ```

5. Pick your Lossless Scaling install folder when prompted.
6. Supply your own local AMD proxy, NVIDIA DLL, and HIP 7.2 runtime files when prompted, following the upstream runtime instructions for those components.
7. Press Scale in Lossless Scaling.

The release ZIP does not include NVIDIA DLLs, AMD proxy binaries, model files, HIP runtime installers, or paid Lossless Scaling files.

The setup flow is still experimental. Automatic activation through the configured Ctrl+Alt+S shortcut and Unscale shutdown passed in a synthetic-source 360p app test. The new 720p preset has isolated runtime/default-forwarding verification; it is not yet a verified full native-resolution app path. See [performance.md](performance.md) for the exact evidence.

Current setup uses `NativeResolution=0`, `Width=1280`, `Height=720`. Lossless Scaling upscales that feed with the selected scaler, or LS1 if the profile's scaler is Off. To choose 1:1 native processing instead, pass `-NativeResolution 1` or set that value in `NrAutoScale.ini`; width/height are then ignored.

Existing INIs are not migrated automatically. Stop scaling, back up `NrAutoScale.ini`, set those three values to the desired preset, and restart Lossless Scaling. Do not rerun the full installer over an existing installation just to change resolution; keep the neural runtime INI and app profile unchanged.

## Runtime Requirements

- Windows 11 24H2 for the currently verified WGC route
- Installed Lossless Scaling
- Local AMD proxy files
- Local NVIDIA DLSS NR DLL
- Local HIP 7.2 runtime
- Visual C++ x64 runtime if using dynamically linked binaries

## Build From Source

Source builds additionally require:

- .NET SDK 8 for the optional legacy controls helper
- CMake 3.20 or newer
- MSVC C++ build tools
- A private local Lossless Scaling installation for integration testing
- Private local runtime files required by the AMD/NVIDIA compatibility experiments

Do not place paid Lossless Scaling files, NVIDIA files, AMD proxy binaries, model files, logs, or screenshots in the public repository.

### Build Developer Components

Build and test the controls helper:

```powershell
dotnet build .\controls\DlssNrControl\DlssNrControl.csproj -c Release
.\controls\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe --self-test
```

Build the native auto-scale wrapper:

```powershell
.\auto-scale\build.ps1
```

The wrapper build output is an original project proxy named `Lossless.dll`. This file is not the paid Lossless Scaling original DLL.

Build the bridge:

```powershell
.\bridge\build.ps1
```

## Developer Setup Script

The release ZIP exposes setup through root `Setup.cmd`. In the source tree, the script wrapper lives under `auto-scale/scripts/`:

```powershell
.\auto-scale\scripts\Setup.cmd
```

The setup script is intended to install the project-authored proxy and bridge into a user's private Lossless Scaling installation. The local install flow preserves the user's original Lossless Scaling DLL privately as `Lossless_original.dll` so the proxy can forward to it.

## Developer Install Script

For local development, the PowerShell setup script accepts explicit private paths:

```powershell
.\auto-scale\scripts\Install-AutoScale.ps1 `
  -LsDir "<Lossless Scaling install folder>" `
  -ProxyVersionSource "<private AMD proxy version.dll>" `
  -NrSource "<private nvngx_dlssnr.dll>"
```

Optional parameters allow a custom bridge executable, original DLL source, GPU visibility, output size, startup delay, warmup frame count, and ready timeout.

This command must be run only against a private local installation. The public repo and public release package must not include the user's original Lossless Scaling DLL, `Lossless_original.dll`, Lossless Scaling executables, app assets, NVIDIA DLLs, AMD proxy binaries, model files, or local backups.

## Runtime Controls

The live bridge owns the runtime hotkeys:

| Hotkey | Runtime action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle bridge output on/off |
| `Ctrl+Alt+F7` | Decrease live output blend toward original |
| `Ctrl+Alt+F8` | Increase live output blend toward actual NR output |

The old controls helper writes startup INI values only. It does not poll or control the running bridge or native integration after startup.

## Controls Helper

Use the controls helper only for startup-configuration experiments.

Use one-shot commands against an explicit INI path:

```powershell
$config = "<Lossless Scaling install folder>\dlssnr_on_amd.ini"
$control = ".\controls\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe"

& $control --config $config --get
& $control --config $config --on
& $control --config $config --off
& $control --config $config --toggle
& $control --config $config --increase
& $control --config $config --decrease
```

Start tray hotkeys for startup INI edits:

```powershell
& $control --config $config --listen
```

Hotkeys while the tray helper is running:

| Hotkey | Startup INI action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle `Enabled` |
| `Ctrl+Alt+F7` | Decrease `LocalStructure` by `0.1` |
| `Ctrl+Alt+F8` | Increase `LocalStructure` by `0.1` |

`LocalStructure` is clamped from `0.0` to `2.0`. Missing or malformed values use `1.0` as the default.

Run the listener smoke test:

```powershell
.\controls\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe --listener-smoke
```

This verifies hotkey registration and message handling against a temporary INI. It does not verify physical keyboard input or a visible image change.

## Probe

The probe validates whether the runtime changes a real presented D3D12 image. It uses a generated test pattern, not a user screenshot or Windows wallpaper image.

Run:

```powershell
.\probe\run_probe.ps1 -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1
```

`-HipVisibleDevices 1` applies only to the probe child process. It avoids selecting the unsupported APU without changing persistent environment variables. The runner also waits after loading the proxy so the hook can initialize before D3D12 device creation.

The script writes run output under `probe\runs\`. That directory is private test output and must not be committed.

## Public Package Boundary

A public source package may include:

- Original project source.
- Build scripts.
- Documentation and license files.
- The project-built proxy `Lossless.dll`.
- The project-built bridge executable.
- Setup and uninstall scripts.

A public source package must not include:

- Paid Lossless Scaling files, including the original `Lossless.dll`, `Lossless_original.dll`, app executables, app assets, or local backups.
- NVIDIA DLLs, SDK files, model files, or other vendor runtime files.
- AMD proxy binaries, installers, configs, or copied source from references that do not permit redistribution.
- Runtime output directories.
- Screenshots, comparison images, movie frames, browser captures, Windows wallpaper images, logs, crash dumps, or user files.

The approved analytical comparison crops under `docs/images/` may be published on the GitHub page. Do not add extra screenshots, comparison images, user files, browser captures, movie frames, or wallpaper images to the public package.

## Additional Native-Mode Release Verification

The current 720p source update does not establish a completed native-mode release. Before making that separate claim, prove all of the following:

- The native auto-scale wrapper starts from the actual Lossless Scaling Scale button with no separate launcher or source picker.
- Native WGC resolution is used.
- The wrapper keeps Lossless Scaling geometry resampling disabled, or equivalent native-resolution behavior is confirmed.
- The integration captures the intended visible-window path.
- `Ctrl+Alt+F6` toggles the bridge output on/off.
- `Ctrl+Alt+F7` and `Ctrl+Alt+F8` adjust live output blend from original toward actual NR output.
- The deterministic full Lossless Scaling comparison passes on a static image shown through an ordinary desktop window, such as a browser image.
- Runtime logs or counters show completed neural work.
- Off/on captures differ from the known static source pattern in the expected direction.
- No proprietary binaries, model files, user-specific files, or unapproved images are needed in the public repo.

The native 2560x1440 bridge proof completed neural jobs at about `63 ms/job`. That is bridge/runtime timing, not full Lossless Scaling end-to-end latency. The current bridge copies frames through CPU readback and has asynchronous latency. Do not use the current evidence to make a competitive-play claim.
