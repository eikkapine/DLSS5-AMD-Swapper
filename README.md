# NR Auto Scale

[![Release](https://img.shields.io/github/v/release/eikkapine/NR-Auto-Scale?include_prereleases&label=preview)](https://github.com/eikkapine/NR-Auto-Scale/releases)
[![License](https://img.shields.io/badge/license-MIT-2ea44f)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-11-0078d4?logo=windows11)](https://www.microsoft.com/windows/windows-11)
[![AMD](https://img.shields.io/badge/tested-RX%209070%20XT-ed1c24?logo=amd)](https://www.amd.com/)

I built **NR Auto Scale** to make an experimental DLSS Neural Rendering compatibility path usable through **Lossless Scaling on AMD hardware** without manually targeting a separate bridge every session.

The latest checkpoint, **v0.1.0-pre.3-dev.3**, adds a pre-upscale neural path inspired by the recent DLSS 5 "Neural Upstream" work. On my RX 9070 XT at a 2560×1440 source, the bridge now runs Neural Rendering at **1920×1080 (`WorkingScale=0.75`)** and lets Lossless Scaling perform the final upscale. I now get about **30 FPS without frame generation** and about **60 FPS with Lossless Scaling 2× frame generation** in the exercised setup.

A clean full-effect window from the non-FG run recorded **1,417 changed RGB submissions over 49.127 seconds (~28.84/s)**. The previous public native-1440p checkpoint was about 19 FPS, so this is the first version that makes 60 FPS output practical on my test system with 2× frame generation. The neural model, weights, precision, settings and full effect strength are unchanged; the performance gain comes from processing fewer neural pixels before the final upscale.

## What it does

- Starts automatically with the normal Lossless Scaling workflow.
- Captures the source window through Windows Graphics Capture.
- Keeps the bridge outside the source application's process.
- Uses shared D3D11/D3D12 GPU resources for the normal live path.
- Can resize the captured frame on the GPU before Neural Rendering.
- Lets me toggle the neural output live with `Ctrl+Alt+F6`.
- Lets me decrease/increase effect strength with `Ctrl+Alt+F7` / `Ctrl+Alt+F8`.
- Keeps paid Lossless Scaling files, NVIDIA runtime files, AMD proxy binaries, models, logs and private machine files out of the repository.

## Quick start

1. Install Lossless Scaling normally.
2. Download the latest preview ZIP from [Releases](https://github.com/eikkapine/NR-Auto-Scale/releases).
3. Extract it somewhere **outside** the Lossless Scaling install folder.
4. Run `Setup.cmd`.
5. Select your Lossless Scaling install if setup does not detect it.
6. Supply the external runtime files requested by setup from your own legally obtained copies.
7. Open the game, browser, video or other window you want to process and press **Scale** in Lossless Scaling.

New installs use `WorkingScale=0.75`. For a 2560×1440 source this means a 1920×1080 neural pass, or **56.25% of the native pixel count**, before Lossless Scaling enlarges the bridge output.

The release does **not** bundle Lossless Scaling files, NVIDIA DLLs, the AMD compatibility proxy, model files or HIP installers. See [Installation](docs/install.md) for the full setup and uninstall flow.

## Controls

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle between original and processed output |
| `Ctrl+Alt+F7` | Reduce the live effect blend by 10% |
| `Ctrl+Alt+F8` | Increase the live effect blend by 10% |

The live blend is clamped from `0.0` to `1.0`.

## Before / after

These are the previously approved matching 1:1 crops from the same frozen 2560×1440 CS2 frame. I did not resize, sharpen, recolor or otherwise modify the crops after capture. They demonstrate that the neural effect is visible; they are not a new quality comparison for the 0.75 working-scale mode.

| Original | Neural output |
| :---: | :---: |
| ![Original native-resolution crop](docs/images/cs2-native-off.png) | ![Neural output crop](docs/images/cs2-native-on.png) |

For that frozen native frame, source-to-original max error was `0`. The neural output changed about 97.7% of pixels, with a mean absolute RGB difference of about `2.87/255` and a maximum channel difference of `55/255`. The raw comparison values are in [comparison.json](docs/images/comparison.json).

## How it works

```text
source window (2560×1440 example)
    │
    │ Windows Graphics Capture
    ▼
GPU capture texture (2560×1440)
    │
    │ GPU resize when WorkingScale < 1
    ▼
neural working texture (1920×1080 at 0.75)
    │
    │ D3D12 + AMD HIP compatibility runtime
    ▼
DLSS Neural Rendering result
    │
    ▼
Lossless Scaling
    │
    │ selected scaler + optional LSFG
    ▼
display
```

`Lossless.dll` in this project is my own proxy wrapper. During local setup it forwards to the original paid Lossless Scaling DLL kept privately on the installed machine. The original application DLL is never part of this repository or release package.

See [Architecture](docs/architecture.md) for the process and resource flow.

## Current performance

| Checkpoint | Source | Neural working size | Base output | 2× FG |
| --- | ---: | ---: | ---: | ---: |
| v0.1.0-pre.2 | 2560×1440 | 2560×1440 | ~19 FPS | ~38 nominal |
| v0.1.0-pre.3-dev.3 | 2560×1440 | 1920×1080 | ~30 FPS | ~60 FPS observed |

The dev.3 non-FG bridge cadence was ~28.84 changed RGB submissions/s in a clean full-effect 49.127-second window. The separate 2× LSFG session used the same 0.75 neural working scale and I observed about 60 FPS output. The bridge itself does not count generated LSFG frames, so the 60 FPS number is recorded as my observed Lossless Scaling output rather than a bridge-derived metric.

See [Performance](docs/performance.md) and [the pre-upscale design note](docs/neural-upstream-performance.md) for the measurements and implementation details.

## Processing modes

`WorkingScale` controls the source-relative neural resolution:

```ini
WorkingScale=0.75
```

- `0.75` is the new-install performance preset.
- `0.25..0.998` runs NR below source resolution and leaves the final upscale to Lossless Scaling.
- `0` disables source-relative working scale.
- `WorkingScale=0` plus `NativeResolution=1` restores the native 1:1 neural path.
- With `WorkingScale=0` and `NativeResolution=0`, `Width` / `Height` provide the older fixed-size fallback.

## Requirements

- Windows 11
- Lossless Scaling installed from an official source
- AMD Radeon GPU; development has focused on RDNA4 / RX 9070 XT
- AMD HIP runtime required by the chosen compatibility runtime
- User-supplied compatibility/runtime files described in [Installation](docs/install.md)

## Documentation

| Page | What it covers |
| --- | --- |
| [Installation](docs/install.md) | Setup, uninstall and processing modes |
| [Architecture](docs/architecture.md) | Capture, proxy, bridge and GPU handoff |
| [Performance](docs/performance.md) | Current measurements and bottlenecks |
| [Pre-upscale performance](docs/neural-upstream-performance.md) | Working-scale design and current research |
| [Verification](docs/verification.md) | What I have actually tested |
| [Development](docs/development.md) | Optimization direction and release discipline |
| [Licensing](docs/licensing.md) | Project license and third-party boundaries |

Component notes are in [bridge/README.md](bridge/README.md), [auto-scale/README.md](auto-scale/README.md), and [controls/README.md](controls/README.md).

## License and third-party files

My original project code is released under the [MIT License](LICENSE). Third-party projects and runtime files keep their own licenses and terms.

I do not redistribute paid Lossless Scaling files, NVIDIA runtime/model files, or the AMD compatibility proxy. Check [Licensing](docs/licensing.md) before redistributing third-party components.

## Credits

This project builds on ideas and compatibility work from:

- [danielblnc/DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD)
- [FrankBarretta/LSP-ReShade](https://github.com/FrankBarretta/LSP-ReShade)
- [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder)
- current community pre-upscale / Neural Upstream experiments referenced in [the performance note](docs/neural-upstream-performance.md)

The repository contains attribution/notices where source or ABI references require them.
