# Install and build

NR Auto Scale has two separate integration paths: a direct-game AMD route for supported DX12/FSR games and a Lossless Scaling bridge for arbitrary capturable windows.

## Direct-game AMD route

Use this path first for a supported single-player/offline game.

Download `dlssnr_on_amd_setup.exe` yourself from the official [DLSS-NR-on-AMD releases](https://github.com/danielblnc/DLSS-NR-on-AMD/releases) and provide your own legitimate `nvngx_dlssnr.dll`.

Check the target before changing anything:

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --check
```

Install:

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --upstream-setup "C:\Downloads\dlssnr_on_amd_setup.exe" `
  --nr-dll "C:\MyDlls\nvngx_dlssnr.dll"
```

The helper requires x64, FSR evidence and DirectX 12 evidence, blocks common anti-cheat markers, verifies the upstream setup file against the SHA-256 digest published by GitHub, invokes the unchanged upstream installer, verifies the generated proxy/config/weights state, and keeps a reversible local hash manifest.

The rich path is accepted only when the generated upstream configuration enables FSR inputs, depth, temporal history, zero-copy interop, and inline gameplay mode. Use `--diagnose` after launching the game to confirm that FidelityFX dispatches and real color/motion/depth staging actually appeared in the runtime log.

For the current upstream route, use **AMD Software: Adrenalin Edition 26.1.1 or newer**. A separate ROCm/HIP installation is not required for normal use. The compatibility runtime uses the HIP runtime delivered with the AMD driver.

See [direct-game/README.md](../direct-game/README.md) for diagnose, update, remove and rollback commands.

## Lossless Scaling route

The main branch keeps the accepted dev.14 compositor while adding the direct-game tooling. The Lossless Scaling path keeps the visible output at the captured source resolution and caps only the neural branch at **480 pixels high** by default.

### Install the preview

1. Install Lossless Scaling from its official store page.
2. Download the latest preview ZIP from <https://github.com/eikkapine/NR-Auto-Scale/releases>.
3. Extract the ZIP somewhere outside the Lossless Scaling folder.
4. Run:

   ```powershell
   .\Setup.cmd
   ```

5. Pick the Lossless Scaling install folder when prompted.
6. Supply your own local AMD compatibility proxy and NVIDIA DLSS-NR DLL when prompted. Use the AMD driver/runtime version required by that upstream compatibility runtime.
7. Focus a capturable game, browser, video, or other window and press **Scale** in Lossless Scaling.

The release ZIP does not contain NVIDIA DLLs, AMD proxy binaries, third-party installers, model files, paid Lossless Scaling files, raw logs, or local private configuration.

### Default processing mode

Fresh setup writes:

```ini
NativeResolution=0
WorkingScale=0
NeuralMaxHeight=480
```

`NeuralMaxHeight=480` takes precedence. The bridge captures the source at its real size, creates a reduced neural texture with the same aspect ratio, and keeps the visible bridge at the source size.

| Source | Neural branch | Visible bridge |
| ---: | ---: | ---: |
| 2560×1440 | ~854×480 | 2560×1440 |
| 1920×1080 | ~854×480 | 1920×1080 |
| 1440×1080 | 640×480 | 1440×1080 |

Sources below the cap are not enlarged for neural processing.

Legacy modes remain available:

```ini
# Reduced source-relative mode
NeuralMaxHeight=0
WorkingScale=0.75

# Full 1:1 neural processing
NeuralMaxHeight=0
WorkingScale=0
NativeResolution=1

# Fixed fallback
NeuralMaxHeight=0
WorkingScale=0
NativeResolution=0
Width=1280
Height=720
```

If you already have an installation, stop scaling before changing `NrAutoScale.ini` or replacing the bridge executable. Do not overwrite the Lossless Scaling profile or neural runtime INI unless the update specifically requires it.

### Runtime controls

| Hotkey | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle processed output on/off |
| `Ctrl+Alt+F7` | Decrease strength |
| `Ctrl+Alt+F8` | Increase strength |

Native neural-residual mode supports `0.0..4.0` strength with `1.0` as the baseline.

## Build from source

Source builds require CMake 3.20+, MSVC C++ build tools and a Windows SDK with the required WGC/D3D headers.

Build the proxy:

```powershell
.\auto-scale\build.ps1
```

Build the bridge:

```powershell
.\bridge\build.ps1
```

The project-built proxy artifact is also named `Lossless.dll`; it is separate from the paid application's original DLL.

For explicit private Lossless Scaling paths:

```powershell
.\auto-scale\scripts\Install-AutoScale.ps1 `
  -LsDir "<Lossless Scaling install folder>" `
  -ProxyVersionSource "<private AMD proxy version.dll>" `
  -NrSource "<private nvngx_dlssnr.dll>" `
  -NeuralMaxHeight 480 `
  -WorkingScale 0
```

The installer privately preserves the original paid Lossless Scaling DLL as `Lossless_original.dll` so the project proxy can forward to it. That file must never be copied into the public repository or release ZIP.

## Uninstall

For the Lossless Scaling route, stop scaling and run:

```powershell
.\scripts\Uninstall-AutoScale.ps1
```

or from the source tree:

```powershell
.\auto-scale\scripts\Uninstall-AutoScale.ps1
```

For the direct-game route, use the hash-safe remove command documented in `direct-game/README.md`.

## Public release boundary

Do not publish:

- paid Lossless Scaling binaries or app assets
- `Lossless_original.dll`
- AMD compatibility proxy/runtime binaries or third-party installers
- `dlssnr_on_amd_setup.exe`
- NVIDIA DLLs, models, or weights
- private INIs, logs, manifests, backups, captures, or machine-specific paths
- unreviewed screenshots or personal files

The public ZIP contains only project-built artifacts, project scripts, approved existing comparison images, documentation, and sanitized measurement metadata.
