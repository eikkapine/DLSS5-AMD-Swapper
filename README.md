# NR Auto Scale

[![Release](https://img.shields.io/github/v/release/eikkapine/NR-Auto-Scale?include_prereleases&label=preview)](https://github.com/eikkapine/NR-Auto-Scale/releases)
[![License](https://img.shields.io/badge/license-MIT-2ea44f)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-11-0078d4?logo=windows11)](https://www.microsoft.com/windows/windows-11)
[![AMD](https://img.shields.io/badge/tested-RX%209070%20XT-ed1c24?logo=amd)](https://www.amd.com/)

I built **NR Auto Scale** to make an experimental DLSS Neural Rendering compatibility path practical through **Lossless Scaling on AMD hardware** without manually targeting a separate bridge every session.

The current checkpoint is **v0.1.0-pre.3-dev.14**. It keeps the visible image at the captured application's native resolution while capping the expensive neural branch at **480 pixels high**. A 2560×1440 source therefore stays 2560×1440 for presentation while Neural Rendering works at about **854×480**.

Dev.14 keeps the native source as the image base, extracts the asynchronous neural residual at the reduced working size, removes broad unstable color/luminance residuals, and rejects stale chroma as motion rises. Exact static pixels can reuse their accepted correction, while changed pixels are never blended with an old native frame. This keeps the neural detail without bringing back the earlier wet-paint/ghosting behavior.

I manually accepted dev.14 after testing it through Lossless Scaling: stationary flicker was fixed, flicker during movement was barely noticeable, and performance still felt good. I did not record a fresh numeric game-FPS value for dev.14, so I do not turn bridge cadence or earlier gameplay observations into a new FPS claim.

## What it does

- Starts automatically when Lossless Scaling activates the configured source.
- Captures the source through Windows Graphics Capture.
- Keeps the bridge outside the source application's process.
- Keeps the visible output at the captured source resolution.
- Caps only the neural branch with `NeuralMaxHeight=480` by default.
- Uses shared D3D11/D3D12 GPU resources for the normal live path.
- Applies the neural correction over the untouched native source instead of displaying the low-resolution neural frame as the base image.
- Provides live on/off and strength controls.
- Keeps paid Lossless Scaling files, NVIDIA runtime/model files, AMD proxy binaries, logs, backups and private machine files out of the repository and release ZIP.

## Quick start

1. Install Lossless Scaling normally from an official source.
2. Download the latest preview ZIP from [Releases](https://github.com/eikkapine/NR-Auto-Scale/releases).
3. Extract it somewhere **outside** the Lossless Scaling install folder.
4. Run `Setup.cmd`.
5. Select your Lossless Scaling install if setup does not detect it.
6. Supply the external runtime files requested by setup from your own legally obtained copies.
7. Focus the game, browser, video or other capturable window and press **Scale** in Lossless Scaling.

New installs use:

```ini
NativeResolution=0
WorkingScale=0
NeuralMaxHeight=480
```

`NeuralMaxHeight=480` is the active source-relative mode. It keeps the bridge output at the captured source size while reducing only the neural working image. `Width` and `Height` remain fallback values for fixed-size mode.

The release does **not** bundle Lossless Scaling files, NVIDIA DLLs, the AMD compatibility proxy, neural weights/models, or HIP installers. See [Installation](docs/install.md) for the full setup and uninstall flow.

## Controls

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle processed output on/off |
| `Ctrl+Alt+F7` | Reduce effect strength |
| `Ctrl+Alt+F8` | Increase effect strength |

In the native neural-delta mode, strength starts at `1.0` and can be increased up to `4.0`. Above `1.0`, F7/F8 use 0.25 steps; from `0.0` to `1.0` they use 0.1 steps.

## Before / after

These are the previously approved matching 1:1 crops from the same frozen 2560×1440 CS2 frame. They are historical native-mode examples showing that the neural runtime produces a real image change; they are **not** new dev.14 screenshots.

| Original | Neural output |
| :---: | :---: |
| ![Original native-resolution crop](docs/images/cs2-native-off.png) | ![Neural output crop](docs/images/cs2-native-on.png) |

No new screenshots were added for dev.14.

## How it works

```text
source window (2560×1440 example)
    │
    │ Windows Graphics Capture
    ▼
native source texture (2560×1440)
    │                         │
    │ keep for visible base   │ GPU resize
    │                         ▼
    │                  neural input (~854×480)
    │                         │
    │                         │ DLSS-NR compatibility runtime
    │                         ▼
    │                  neural output (~854×480)
    │                         │
    └──────────────┬──────────┘
                   │ filtered async neural residual
                   ▼
        native-resolution composite (2560×1440)
                   │
                   ▼
          Lossless Scaling output
```

The color-only Lossless Scaling bridge does not receive the game's real engine depth or motion-vector buffers. The current runtime therefore cannot reproduce the full guided FSR-hook path used by native in-game integrations. Dev.14 keeps the fast color-only path and stabilizes its asynchronous residual spatially: broad exposure/color swings are limited and stale chroma is strongly reduced when the source moves.

`Lossless.dll` in this project is my own proxy wrapper. During local setup it forwards to the original paid Lossless Scaling DLL kept privately on the installed machine. The original application DLL is never part of this repository or release package.

## Performance history

| Checkpoint | Source | Neural working size | Manual observation |
| --- | ---: | ---: | --- |
| v0.1.0-pre.2 | 2560×1440 | 2560×1440 | ~19 FPS base |
| v0.1.0-pre.3-dev.3 | 2560×1440 | 1920×1080 | ~30 FPS base / ~60 FPS with 2× LSFG |
| dev.4 | 2560×1440 | ~854×480 | ~60 FPS base, frame generation off |
| v0.1.0-pre.3-dev.6 | native visible output | max 480p neural branch | neural behavior manually accepted |
| **v0.1.0-pre.3-dev.14** | native visible output | max 480p neural branch | stationary flicker fixed; moving flicker barely noticeable; no fresh numeric FPS recorded |

These are manual observations from different gameplay sessions, not controlled benchmark runs. The bridge also does not count generated LSFG frames.

See [Performance](docs/performance.md) for the measurement boundary and implementation notes.

## Processing modes

```ini
# Current default: native visible output + low-resolution neural branch
WorkingScale=0
NeuralMaxHeight=480

# Legacy source-relative mode
# WorkingScale=0.75
# NeuralMaxHeight=0

# Full 1:1 neural processing
# WorkingScale=0
# NeuralMaxHeight=0
# NativeResolution=1
```

`NeuralMaxHeight` takes precedence when it is non-zero. Sources at or below the cap are not enlarged for neural processing.

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
| [Performance](docs/performance.md) | Current measurements and limitations |
| [Verification](docs/verification.md) | What I have actually exercised |
| [Development](docs/development.md) | Development and release discipline |
| [Licensing](docs/licensing.md) | Project license and third-party boundaries |

Component notes are in [bridge/README.md](bridge/README.md) and [auto-scale/README.md](auto-scale/README.md).

## License and third-party files

My original project code is released under the [MIT License](LICENSE). Third-party projects and runtime files keep their own licenses and terms.

I do not redistribute paid Lossless Scaling files, NVIDIA runtime/model files, or the AMD compatibility proxy. Check [Licensing](docs/licensing.md) before redistributing third-party components.

## Credits

This project builds on ideas and compatibility work from:

- [danielblnc/DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD)
- [FrankBarretta/LSP-ReShade](https://github.com/FrankBarretta/LSP-ReShade)
- [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder)
- current community pre-upscale / Neural Upstream experiments referenced in [the performance note](docs/neural-upstream-performance.md)
