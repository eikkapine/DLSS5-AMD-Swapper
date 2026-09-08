# NR Auto Scale

[![Release](https://img.shields.io/github/v/release/eikkapine/NR-Auto-Scale?include_prereleases&label=preview)](https://github.com/eikkapine/NR-Auto-Scale/releases)
[![License](https://img.shields.io/badge/license-MIT-2ea44f)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-11-0078d4?logo=windows11)](https://www.microsoft.com/windows/windows-11)
[![AMD](https://img.shields.io/badge/tested-RX%209070%20XT-ed1c24?logo=amd)](https://www.amd.com/)

I built **NR Auto Scale** to make an experimental DLSS Neural Rendering compatibility path usable through **Lossless Scaling on AMD hardware** without having to launch and target a separate bridge by hand every time.

The project starts the bridge when Lossless Scaling starts scaling, captures the source window with Windows Graphics Capture, runs the neural path in a separate process, and feeds the result back to Lossless Scaling. It also adds live hotkeys for bypass and effect strength.

This is still a preview. My current test system is an **RX 9070 XT**, and native 2560×1440 neural rendering is still much slower than I want. The current public checkpoint is about making the integration usable, measurable, and easier to improve.

## What it does

- Starts with the normal Lossless Scaling workflow instead of a separate source picker.
- Keeps the bridge outside the source application's process and captures through WGC.
- Supports fixed-size processing or 1:1 native-resolution processing.
- Lets me toggle the neural output live with `Ctrl+Alt+F6`.
- Lets me decrease/increase the live blend with `Ctrl+Alt+F7` / `Ctrl+Alt+F8`.
- Keeps paid Lossless Scaling files, NVIDIA runtime files, AMD proxy binaries, models, logs, and private machine files out of this repository.

## Quick start

1. Install Lossless Scaling normally.
2. Download the latest preview ZIP from [Releases](https://github.com/eikkapine/NR-Auto-Scale/releases).
3. Extract it somewhere **outside** the Lossless Scaling install folder.
4. Run `Setup.cmd`.
5. Select your Lossless Scaling install when prompted.
6. Supply the external runtime files requested by setup from your own legally obtained copies.
7. Open the game, browser, video, or other window you want to process and press **Scale** in Lossless Scaling.

The release does **not** bundle Lossless Scaling files, NVIDIA DLLs, the AMD compatibility proxy, model files, or HIP installers. See [Installation](docs/install.md) for the full setup and uninstall flow.

## Controls

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle between original and processed output |
| `Ctrl+Alt+F7` | Reduce the live effect blend by 10% |
| `Ctrl+Alt+F8` | Increase the live effect blend by 10% |

The live blend is clamped from `0.0` to `1.0`.

## Before / after

These are matching 1:1 crops from the same frozen 2560×1440 CS2 frame. I did not resize, sharpen, recolor, or otherwise modify the crops after capture.

| Original | Neural output |
| :---: | :---: |
| ![Original native-resolution crop](docs/images/cs2-native-off.png) | ![Neural output crop](docs/images/cs2-native-on.png) |

For that frame, source-to-original max error was `0`. The neural output changed about 97.7% of pixels, with a mean absolute RGB difference of about `2.87/255` and a maximum channel difference of `55/255`. The raw comparison values are in [comparison.json](docs/images/comparison.json).

## How it works

```text
source window
    │
    │ Windows Graphics Capture
    ▼
DlssNrBridge.exe
    │
    ├─ D3D11 capture / presentation
    ├─ D3D12 neural runtime feed
    ├─ AMD HIP backend
    └─ live original ↔ neural blend
    │
    ▼
Lossless Scaling
    │
    ▼
display
```

`Lossless.dll` in this project is my own proxy wrapper. During local setup it forwards to the original paid Lossless Scaling DLL kept privately on the installed machine. The original application DLL is never part of this repository or release package.

See [Architecture](docs/architecture.md) for the process and resource flow.

## Current status

The latest published checkpoint is **v0.1.0-pre.2**. On my RX 9070 XT at native 2560×1440, the recorded run produced **1,112 changed RGB submissions over 60.238 seconds (18.46/s)** and I observed roughly **19 FPS** base output. A 2× frame-generation setting would make `38 FPS` a nominal multiplier, but the bridge did not independently measure generated/displayed FPS.

The project is not at a native 60 FPS target yet. I am keeping image resolution, model/weights, neural precision, full effect strength, and the asynchronous inference path intact while working on transport and scheduling overhead.

See [Performance](docs/performance.md) for the measurements and [Verification](docs/verification.md) for exactly what has and has not been checked.

## Processing modes

The preview currently defaults to fixed **1280×720** processing with source aspect ratio preserved. Lossless Scaling can then apply the scaler selected in its profile.

Set `NativeResolution=1` in `NrAutoScale.ini` for 1:1 source-size processing. Native mode uses the captured source dimensions and requests a 1.0 scale path. The bridge currently rejects source sizes above 3840×2160 and stops if the source dimensions change during a native session.

## Requirements

- Windows 11
- Lossless Scaling installed from an official source
- AMD Radeon GPU; development has focused on RDNA4 / RX 9070 XT
- AMD HIP 7.2 runtime for the current compute path
- User-supplied compatibility/runtime files described in [Installation](docs/install.md)

## Documentation

| Page | What it covers |
| --- | --- |
| [Installation](docs/install.md) | Setup, uninstall, runtime files, native mode |
| [Architecture](docs/architecture.md) | Capture, proxy, bridge, GPU handoff |
| [Performance](docs/performance.md) | Current measurements and bottlenecks |
| [Verification](docs/verification.md) | What I have actually tested |
| [Development](docs/development.md) | Current optimization direction and release discipline |
| [Licensing](docs/licensing.md) | Project license and third-party boundaries |

Component-level notes are also available in [bridge/README.md](bridge/README.md), [auto-scale/README.md](auto-scale/README.md), and [controls/README.md](controls/README.md).

## License and third-party files

My original project code is released under the [MIT License](LICENSE). Third-party projects and runtime files keep their own licenses and terms.

I intentionally do not redistribute paid Lossless Scaling files, NVIDIA runtime/model files, or the AMD compatibility proxy. The current AMD DLSS-NR route also has unresolved third-party licensing questions for redistribution/use outside the terms of those upstream components, so check [Licensing](docs/licensing.md) before packaging or sharing anything beyond this repository's original code and release artifacts.

## Credits

This project builds on ideas and compatibility work from:

- [danielblnc/DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD)
- [FrankBarretta/LSP-ReShade](https://github.com/FrankBarretta/LSP-ReShade)
- [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder)

The repository contains attribution/notices where source or ABI references require them.
