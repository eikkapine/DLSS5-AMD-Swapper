# NR Auto Scale

NR Auto Scale is my AMD Neural Rendering experiment. It now has two separate paths:

- **Direct game** — the preferred path for supported 64-bit DirectX 12 games with FSR. Neural Rendering runs inside the game/upscaler flow where depth, motion, jitter, exposure and render-resolution colour are available.
- **Lossless Scaling bridge** — the compatibility path for arbitrary capturable windows. It works from the final colour frame and keeps the existing automatic Scale workflow and hotkeys.

The direct-game path is where I am putting the full DLSS 5-style quality work. The Lossless Scaling path remains useful, but a finished desktop frame cannot supply the same temporal and engine information as an in-game integration.

## Direct-game AMD install

The first route targets x64 DX12 games with an FSR runtime. It uses the user's own copy of `DLSS-NR-on-AMD` and the user's own legitimately obtained `nvngx_dlssnr.dll`.

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --check

py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --upstream-setup "C:\Downloads\dlssnr_on_amd_setup.exe" `
  --nr-dll "C:\MyDlls\nvngx_dlssnr.dll"
```

The helper verifies the upstream setup file against the SHA-256 digest published by GitHub, checks the game for FSR and common anti-cheat markers, installs into the game folder, verifies the created runtime files and records a local hash manifest for safe removal.

I do not bundle or download `DLSS-NR-on-AMD`. Its current licence forbids redistribution inside another installer/package, so the user downloads it directly from the [official releases page](https://github.com/danielblnc/DLSS-NR-on-AMD/releases). The project also never contains NVIDIA model/runtime files.

See [Direct-game AMD route](direct-game/README.md).

## Lossless Scaling route

The current `experimental-soft-cheat` checkpoint is **v0.1.0-pre.3-soft-cheat.4**. The visible image stays at the captured application's source resolution while the neural branch can be capped independently.

Default controls:

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle the effect |
| `Ctrl+Alt+F7` | Reduce strength |
| `Ctrl+Alt+F8` | Increase strength |

Strength starts at `1.0` and can be increased to `4.0`. The filter remains uniform across the frame; there is no player, character or object-specific targeting.

Build and install from source:

```powershell
.\auto-scale\build.ps1
.\bridge\build.ps1
.\auto-scale\scripts\Setup.cmd
```

Fresh setup uses:

```ini
NativeResolution=0
WorkingScale=0
NeuralMaxHeight=480
```

Pressing **Scale** in Lossless Scaling launches the bridge automatically.

## Performance measurements

I do not publish hand-entered FPS claims. Public performance values must come from hashed logs.

For actual game/display frame timing, record a PresentMon CSV:

```powershell
.\tools\Capture-Performance.ps1 -ProcessName Game.exe -Seconds 30
```

Then feed that CSV to `bridge/scripts/Analyze-Run.py` together with the bridge/runtime logs. The analyzer writes a sanitized JSON report containing source SHA-256 values. Raw logs, process paths, captures and private runtime files stay local.

The publication check enforces this rule:

```powershell
py .\tools\Check-Publication.py
```

## Why the direct-game path can look much stronger

The dramatic Neural Rendering examples are not simple sharpening. A real in-game path can use the scene's temporal/upscaler contract and preserve broad neural changes to materials, local lighting, skin structure and shading.

The Lossless Scaling bridge starts after the game has already produced a finished frame. It has no true engine motion vectors, depth, jitter, exposure or pre-upscale colour buffer. To keep that path stable, the compositor rejects or limits broad unstable residuals, which also removes much of the large appearance change visible in native/direct integrations.

The direct-game route moves the work back into the game/upscaler flow so the neural result can be preserved with the data needed to keep it temporally aligned. See [Neural upstream research](docs/neural-upstream-performance.md).

## Requirements

### Direct-game route

- Windows 11
- 64-bit DirectX 12 game with FSR for the first route
- supported AMD Radeon GPU and AMD Software: Adrenalin Edition 26.1.1+ for the current direct-game upstream
- user-downloaded official `dlssnr_on_amd_setup.exe`
- user-supplied legitimate `nvngx_dlssnr.dll`

The current direct-game upstream uses the HIP runtime supplied by the AMD driver. A separate ROCm install is not required.

Targets containing common anti-cheat markers are blocked by default. The direct-game route is intended mainly for single-player/offline games.

### Lossless Scaling route

- Windows 11
- Lossless Scaling from an official source
- AMD Radeon GPU
- CMake, MSVC C++ build tools and Windows SDK when building from source
- user-supplied compatibility/runtime files described in [Installation](docs/install.md)

## Repository boundary

This repository contains my source code, scripts, documentation and sanitized measurement JSON. It does not contain:

- paid Lossless Scaling binaries or `Lossless_original.dll`
- `DLSS-NR-on-AMD` binaries/installers
- NVIDIA DLSS-NR DLLs, models or weights
- AMD runtime/proxy binaries from third parties
- private INI files, logs, manifests, backups or machine-specific paths
- personal files or secrets

## Documentation

- [Direct-game AMD route](direct-game/README.md)
- [Installation](docs/install.md)
- [Architecture](docs/architecture.md)
- [Performance and log provenance](docs/performance.md)
- [Neural upstream research](docs/neural-upstream-performance.md)
- [Verification](docs/verification.md)
- [Licensing](docs/licensing.md)
- [Bridge internals](bridge/README.md)

## Credits

The project builds on public research and compatibility work from:

- [danielblnc/DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD)
- [matiasLombo/neural-upstream](https://github.com/matiasLombo/neural-upstream)
- [Kizzuwatnaa/DLSS5-Autopilot](https://github.com/Kizzuwatnaa/DLSS5-Autopilot)
- [Dagherbou/OptiScaler_DLSSNR](https://github.com/Dagherbou/OptiScaler_DLSSNR)
- [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder)
- [FrankBarretta/LSP-ReShade](https://github.com/FrankBarretta/LSP-ReShade)

My original project code is released under the [MIT License](LICENSE). Third-party components keep their own licences and terms.
