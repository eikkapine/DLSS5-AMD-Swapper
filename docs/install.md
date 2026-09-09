# Installation

The easiest way to use DLSS5 AMD Swapper is the portable Windows manager. The command-line and source-build paths are still available for development and troubleshooting.

## Portable manager

1. Download the release ZIP.
2. Extract it to any normal writable folder. Do not extract it inside the Lossless Scaling install directory.
3. Run `Dlss5AmdSwapper.exe`.
4. The app stores only its own settings under `%LOCALAPPDATA%\DLSS5 AMD Swapper`.

The ZIP contains the manager and project-owned Lossless Scaling payload only. It does not contain paid Lossless Scaling files, third-party AMD runtimes, `DLSS-NR-on-AMD`, NVIDIA DLLs/models or generated weights.

## Direct-game setup

Use this route for supported single-player/offline x64 DX12/FSR games.

1. Open **Game library** and press **Scan PC** to check Steam, Epic, GOG, EA, Ubisoft, Battle.net, Xbox/Game Pass and common standalone game folders, or add the game executable manually.
2. Review the scan summary and select a compatible game. The scanner does not select a target for you.
3. Click **Set up**. The manager downloads the latest official `dlssnr_on_amd_setup.exe` from the [DLSS-NR-on-AMD releases](https://github.com/danielblnc/DLSS-NR-on-AMD/releases), verifies the GitHub-published size and SHA-256, and automatically selects install or update.
4. The manager searches for a valid local `nvngx_dlssnr.dll`. If it cannot find one, select your own legitimate local copy when prompted. The project never downloads or redistributes that NVIDIA DLL.
5. Launch the game and use **Refresh evidence** to inspect the runtime state.

The manager verifies x64, FSR and DX12 evidence, blocks common anti-cheat markers, verifies the official upstream installer against GitHub release metadata, checks the generated rich-input configuration and records a hash-backed local manifest for safe update/remove behavior.

New installs use `.dlss5-amd-swapper.json`. Older `.nr-auto-scale-direct.json` manifests are still recognized and are migrated on update.

The generated rich path must enable FSR inputs, depth, temporal history, interop and inline gameplay mode. The manager then looks for FidelityFX dispatches and real colour/motion/depth staging in the runtime log before calling the rich path observed.

For the currently tested upstream route, use **AMD Software: Adrenalin Edition 26.1.1 or newer**. A separate ROCm install is not required for normal use.

### Native output with lower neural cost

Keep the game's final output at your normal/native resolution. If the game offers FSR quality modes, the hidden neural workload can follow the lower FSR input/render resolution while the game still reconstructs the native final output.

The runtime diagnostics show both FSR input size and swapchain/output size. If they are equal, the neural pass is running at full output resolution.

## Lossless Scaling setup

1. Install Lossless Scaling from an official source.
2. Open the **Lossless Scaling** page in DLSS5 AMD Swapper.
3. Select your local AMD compatibility proxy named `version.dll`.
4. Select your own `nvngx_dlssnr.dll`.
5. Choose the HIP device index if your system has more than one AMD-visible device.
6. Click **Install / Update bridge**.
7. Focus any capturable window and press **Scale** in Lossless Scaling normally.

The manager detects the Lossless Scaling installation, installs my project-built proxy/bridge and preserves the existing bridge resolution/runtime choices when updating an already managed install.

The current default is:

```ini
NativeResolution=0
WorkingScale=0
NeuralMaxHeight=480
```

`NeuralMaxHeight=480` caps only the neural branch. The captured source remains the visible output size.

| Captured source | Neural branch | Visible output |
| ---: | ---: | ---: |
| 2560×1440 | ~854×480 | 2560×1440 |
| 1920×1080 | ~854×480 | 1920×1080 |
| 1440×1080 | 640×480 | 1440×1080 |

Sources below the cap are not enlarged for the neural pass.

## Hotkeys

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle effect |
| `Ctrl+Alt+F7` | Decrease strength |
| `Ctrl+Alt+F8` | Increase strength |

Lossless Scaling owns these keys while its bridge is active. The manager registers the same keys for a running managed direct-game target.

## Restore / uninstall

For a direct-game install, select the game and click **Restore**. Hash checks prevent the manager from silently deleting files that changed after installation.

For Lossless Scaling, open the **Lossless Scaling** page and click **Uninstall**. The installer restores the privately preserved original Lossless Scaling DLL when the managed state is valid.

## Build from source

Source builds require the .NET 8 SDK, CMake 3.20+, MSVC C++ build tools and the Windows SDK.

Build the native wrapper and bridge, run the app smoke tests, publish the self-contained manager and create the local ZIP with:

```powershell
.\app\Build-Package.ps1
```

The package script also checks that forbidden third-party/private filenames are absent before it writes `SHA256SUMS.txt` and the ZIP.

For individual native builds:

```powershell
.\auto-scale\build.ps1
.\bridge\build.ps1
```

## Advanced CLI

The Python direct-game helper remains available:

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --check
```

See [Direct-game route](../direct-game/README.md) for install/update/diagnose/remove commands.
