# Installation

The easiest way to use DLSS5 AMD Swapper is the portable Windows manager. The command-line and source-build paths are still available for development and troubleshooting.

## Portable manager

1. Download the release ZIP.
2. Extract it to any normal writable folder. Do not extract it inside the Lossless Scaling install directory.
3. Run `Dlss5AmdSwapper.exe`.
4. The app stores only its own settings under `%LOCALAPPDATA%\DLSS5 AMD Swapper`.

The ZIP contains the manager and project-owned Lossless Scaling payload only. It does not contain paid Lossless Scaling files, third-party AMD runtimes, `DLSS-NR-on-AMD`, NVIDIA DLLs/models or generated weights.

## Direct-game setup

Use this route for supported single-player/offline x64 DX12/FSR games. Two backends are supported: the official post-FSR runtime and the OptiScaler pre-SR route.

### Official post-FSR runtime

1. Open **Game library** and press **Scan PC** to check Steam, Epic, GOG, EA, Ubisoft, Battle.net, Xbox/Game Pass and common standalone game folders, or add the game executable manually.
2. Review the scan summary and select a compatible game. The scanner does not select a target for you.
3. Click **Set up**. The manager downloads the latest official `dlssnr_on_amd_setup.exe` from the [DLSS-NR-on-AMD releases](https://github.com/danielblnc/DLSS-NR-on-AMD/releases), verifies the GitHub-published size and SHA-256, and automatically selects install or update.
4. The manager searches for a valid local `nvngx_dlssnr.dll`. If it cannot find one, select your own legitimate local copy when prompted. The project never downloads or redistributes that NVIDIA DLL.
5. Launch the game and use **Refresh evidence** to inspect the runtime state.

The manager verifies x64, FSR and DX12 evidence, blocks common anti-cheat markers, verifies the official upstream installer against GitHub release metadata, checks the generated rich-input configuration and records a hash-backed local manifest for safe update/remove behavior.

New installs use `.dlss5-amd-swapper.json`. Older `.nr-auto-scale-direct.json` manifests are still recognized and are migrated on update.

The generated rich path must enable FSR inputs, depth, temporal history, interop and inline gameplay mode. The manager then looks for FidelityFX dispatches and real colour/motion/depth staging in the runtime log before calling the rich path observed.

For the currently tested upstream route, use **AMD Software: Adrenalin Edition 26.1.1 or newer**. A separate ROCm install is not required for normal use.

### Direct Game — OptiScaler pre-SR

Use this alternative direct-game backend when you want to evaluate neural rendering before super-resolution (FSR (ffx)) upscaling rather than after it.

1. In **Settings**, specify your user-supplied `OptiScaler-AMD-PreSR-Multipass-v1.2` package folder or zip. The manager automatically scans `Downloads`, `Desktop`, and `Documents` for package archives (cached under `%LOCALAPPDATA%\DLSS5 AMD Swapper\optiscaler-packages\<hash>`).
2. Specify your locally generated `dlssnr_on_amd_weights.bin`. The manager reuses locally generated weights from the bridge runtime folder, Lossless Scaling folder, or previously managed game folders; all copies must agree in SHA-256 hash. Git LFS pointer stubs and undersized files are rejected. The project never downloads or bundles package files or weights.
3. Open **Game library**, select a compatible x64 DirectX 12 target, and click **Set up**.
4. In the route selection dialog, choose **OptiScaler pre-SR**.
5. Select **Quality** (pre-SR neural pass, the game's own FSR ratio, no frame generation) or **Performance** (pre-SR with a 3.0x ratio override and 3x frame generation through OptiScaler).
6. Click **Set up**. The manager validates PE x64 binaries, verifies the `amd-presr` version string and `dlssnr_amd` marker, copies required proxy and dependency files, and writes a schema 3 `.dlss5-amd-swapper.json` manifest.
7. Launch the game with FSR enabled. Press `Insert` to open the in-game OptiScaler menu.
8. Use **Refresh evidence** in the manager to verify active passes, render and target dimensions, and execution times.

OptiScaler cannot be used inside Lossless Scaling because Lossless Scaling uses Direct3D 11 presentation whereas OptiScaler pre-SR requires a DirectX 12 super-resolution call to hook.

### Native output with lower neural cost

Keep the game's final output at your normal/native resolution. If the game offers FSR quality modes, the hidden neural workload can follow the lower FSR input/render resolution while the game still reconstructs the native final output.

The runtime diagnostics show both FSR input size and swapchain/output size. If they are equal, the neural pass is running at full output resolution.

## Lossless Scaling setup

1. Install Lossless Scaling from an official source.
2. Open the **Lossless Scaling** page in DLSS5 AMD Swapper.
3. Select your local AMD compatibility proxy named `version.dll`.
4. Select your own `nvngx_dlssnr.dll`.
5. Choose the HIP device index if your system has more than one AMD-visible device.
6. Adjust the Structure, Skin, and Tone sliders (or enable **Skin follows structure**).
7. Click **Install / Update bridge**.
8. Focus any capturable window and press **Scale** in Lossless Scaling normally.

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
| `Ctrl+Alt+F6` | Toggle saved direct-game on/off state |
| `Ctrl+Alt+F7` | Decrease strength |
| `Ctrl+Alt+F8` | Increase strength |
| `Ctrl+Alt+F9` | Cycle selected layer (Structure, Skin, Tone) |
| `Ctrl+Alt+F10` | Decrease selected layer strength (-0.1) |
| `Ctrl+Alt+F11` | Increase selected layer strength (+0.1) |

Lossless Scaling owns `Ctrl+Alt+F6/F7/F8` while its bridge is active, and accepts `Ctrl+Alt+F9/F10/F11` to adjust layer settings in the bridge runtime INI while `LosslessScaling.exe` runs. The manager registers `Ctrl+Alt+F6/F7/F8` for a running managed direct-game target. For the official post-FSR runtime, F6 saves the configured state; use the upstream `End` overlay for the authoritative live toggle.

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

See [Direct-game route](../direct-game/README.md) and [OptiScaler pre-SR](optiscaler-presr.md) for install/update/diagnose/remove commands.

## Optional manager installation

Extract the complete release ZIP, then run `Install.cmd`. It verifies `SHA256SUMS.txt`, copies the manager and project-owned payload into the current Windows account's Programs directory, and creates Desktop and Start menu shortcuts. Exit the installed app from its tray menu before updating. Running `Dlss5AmdSwapper.exe` directly remains the portable option.

Settings includes light/dark themes, close-to-tray, additional scan folders and an optional full-drive scan. Right-click library entries for folder actions, local covers and hiding. Diagnostics export previews an allowlisted report before saving; private paths and raw logs are excluded.
