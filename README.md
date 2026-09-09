# NR Auto Scale

[![Release](https://img.shields.io/github/v/release/eikkapine/NR-Auto-Scale?include_prereleases&label=preview)](https://github.com/eikkapine/NR-Auto-Scale/releases)
[![License](https://img.shields.io/badge/license-MIT-2ea44f)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-11-0078d4?logo=windows11)](https://www.microsoft.com/windows/windows-11)
[![AMD](https://img.shields.io/badge/tested-RX%209070%20XT-ed1c24?logo=amd)](https://www.amd.com/)

NR Auto Scale is my AMD Neural Rendering experiment. It has two separate paths:

- **Direct game** — preferred for supported 64-bit DirectX 12 games with FSR, where Neural Rendering can use the game's temporal/upscaler data.
- **Lossless Scaling bridge** — compatibility path for arbitrary capturable windows, with automatic Scale integration and live strength hotkeys.

The current main checkpoint is **v0.1.0-pre.3-dev.15**. It keeps the accepted dev.14 Lossless Scaling compositor and adds the verified direct-game AMD route, log-backed performance capture, publication provenance checks, and stricter package safeguards.

## Direct-game AMD route

Use this path first for a supported single-player/offline game. The helper checks the target before installation, blocks common anti-cheat markers, verifies the upstream installer, verifies the generated Neural Rendering configuration, and keeps a hash-backed local manifest for safe removal or rollback.

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --check

py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --upstream-setup "C:\Downloads\dlssnr_on_amd_setup.exe" `
  --nr-dll "C:\MyDlls\nvngx_dlssnr.dll"
```

The helper requires the user to supply their own official `DLSS-NR-on-AMD` setup file and legitimate `nvngx_dlssnr.dll`. I do not bundle, download, patch, or redistribute those third-party files. See [Direct-game AMD route](direct-game/README.md) for update, diagnose, rollback and uninstall commands.

For the current upstream route, use **AMD Software: Adrenalin Edition 26.1.1 or newer**. A separate ROCm/HIP installation is not required for normal use; the compatibility runtime uses the HIP runtime supplied with the AMD driver.

## Lossless Scaling route

The Lossless Scaling path keeps the visible image at the captured application's native resolution while capping only the neural branch with `NeuralMaxHeight=480` by default. A 2560×1440 source therefore stays 2560×1440 for presentation while Neural Rendering works at about 854×480.

Dev.15 keeps dev.14's accepted motion handling: the native source remains the image base, broad unstable residual color/luminance is filtered, stale chroma is reduced as motion rises, and correction history is reused only where the source is effectively unchanged. This avoids the earlier long-lived trails and wet-paint smearing while preserving the stronger neural detail that remains stable from final-frame color alone.

Pressing **Scale** in Lossless Scaling launches the bridge automatically.

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle processed output on/off |
| `Ctrl+Alt+F7` | Reduce effect strength |
| `Ctrl+Alt+F8` | Increase effect strength |

Strength starts at `1.0` and can be increased up to `4.0`. Above `1.0`, F7/F8 use 0.25 steps; from `0.0` to `1.0` they use 0.1 steps.

Fresh setup uses:

```ini
NativeResolution=0
WorkingScale=0
NeuralMaxHeight=480
```

## Before / after

These are the two previously reviewed 1:1 CS2 crops already approved for the public repository. No new screenshot was added for dev.15.

| Original | Neural output |
| :---: | :---: |
| ![Original native-resolution crop](docs/images/cs2-native-off.png) | ![Neural output crop](docs/images/cs2-native-on.png) |

## Why direct-game can look much stronger

The Lossless Scaling bridge starts after the game has already produced a finished color frame. It does not receive true engine motion vectors, depth, jitter, exposure, or the render-resolution color buffer. Preserving large temporal neural changes from that limited input causes flicker or stale-frame artifacts, so the bridge intentionally rejects unstable broad corrections.

The direct-game route moves Neural Rendering into the game's FSR/DX12 path. When the upstream runtime exposes real color, motion, depth and temporal state, the model can keep much stronger material, shading and local-structure changes while staying aligned across frames. See [Neural Rendering upstream research](docs/neural-upstream-performance.md).

## Performance measurements

I do not publish hand-entered FPS claims. Public performance numbers must come from hashed logs.

Capture actual game/display frame timing with PresentMon:

```powershell
.\tools\Capture-Performance.ps1 -ProcessName Game.exe -Seconds 30
```

Then analyze the capture with `bridge/scripts/Analyze-Run.py`. The analyzer keeps bridge cadence, HIP/runtime timing, and actual game/display timing as separate metrics and emits sanitized JSON with SHA-256 hashes of the raw sources. Raw logs and machine-specific paths stay private.

Before publishing, I run:

```powershell
py .\tools\Check-Publication.py
```

## Requirements

### Direct-game route

- Windows 11
- supported AMD Radeon GPU
- 64-bit DirectX 12 game with FSR evidence
- AMD Software: Adrenalin Edition 26.1.1 or newer for the current upstream
- user-downloaded official `dlssnr_on_amd_setup.exe`
- user-supplied legitimate `nvngx_dlssnr.dll`

Targets with common anti-cheat markers are blocked by default. This route is intended mainly for single-player/offline games.

### Lossless Scaling route

- Windows 11
- Lossless Scaling installed from an official source
- AMD Radeon GPU
- user-supplied compatibility/runtime files described in [Installation](docs/install.md)

Source builds additionally need CMake, MSVC C++ build tools, and the Windows SDK.

## Repository boundary

This repository contains my source code, scripts, documentation, project-built binaries, and sanitized measurement metadata. It does **not** contain:

- paid Lossless Scaling binaries or `Lossless_original.dll`
- `DLSS-NR-on-AMD` binaries or installers
- NVIDIA DLSS-NR DLLs, models, or weights
- AMD third-party runtime/proxy binaries
- raw logs, private INIs, manifests, backups, or machine-specific paths
- personal files, secrets, or unreviewed screenshots

## Documentation

- [Direct-game AMD route](direct-game/README.md)
- [Installation](docs/install.md)
- [Architecture](docs/architecture.md)
- [Performance](docs/performance.md)
- [Verification](docs/verification.md)
- [Development](docs/development.md)
- [Licensing](docs/licensing.md)
- [Bridge internals](bridge/README.md)

## Credits

This project builds on public research and compatibility work from:

- [danielblnc/DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD)
- [matiasLombo/neural-upstream](https://github.com/matiasLombo/neural-upstream)
- [Kizzuwatnaa/DLSS5-Autopilot](https://github.com/Kizzuwatnaa/DLSS5-Autopilot)
- [Dagherbou/OptiScaler_DLSSNR](https://github.com/Dagherbou/OptiScaler_DLSSNR)
- [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder)
- [FrankBarretta/LSP-ReShade](https://github.com/FrankBarretta/LSP-ReShade)

My original project code is released under the [MIT License](LICENSE). Third-party components keep their own licenses and terms.
