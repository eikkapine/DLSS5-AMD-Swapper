# NR Auto Scale — experimental visual-clarity branch

This is the `experimental-soft-cheat` branch, currently **v0.1.0-pre.3-soft-cheat.2**. It now carries the same dev.14 transport, pacing and anti-flicker work as the main branch, but uses a different whole-frame compositor aimed at stronger visual clarity.

The visible image stays at the captured application's native resolution while only the expensive neural branch is capped to **480 pixels high** by default. A 2560×1440 source therefore remains 2560×1440 for presentation while Neural Rendering works at about 854×480.

The experimental compositor keeps stable neural structure and luminance/shading information, rejects broad unstable chroma, boosts local luminance separation from the current source frame, and applies a mild neutral-veil reduction. The filter is uniform across the image; it does not classify or target players, characters or any other object type.

The branch keeps the existing automatic Lossless Scaling integration and hotkeys:

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle the effect |
| `Ctrl+Alt+F7` | Reduce strength |
| `Ctrl+Alt+F8` | Increase strength |

Strength starts at `1.0` and can be pushed to `4.0`. Above `1.0`, F7/F8 use 0.25 steps. From `0.0` to `1.0`, they use 0.1 steps.

## What this branch does

- Starts automatically when Lossless Scaling activates a source.
- Captures the selected window with Windows Graphics Capture.
- Keeps the visible output at the source resolution.
- Caps only the neural working image with `NeuralMaxHeight=480` by default.
- Keeps the dev.14 D3D11/D3D12 shared-resource, asynchronous pacing and high-rate visible-presentation path.
- Keeps the dev.14 motion rejection and exact-static correction reuse that removed most flicker from the main branch.
- Preserves stable neural detail plus bounded luminance/shading changes while rejecting broad unstable color shifts.
- Adds current-frame local-contrast enhancement and a small neutral-veil reduction for a stronger clarity/dehaze-style look.
- Adds the result to the untouched native source instead of using the low-resolution neural image as the visible base.
- Keeps the bridge outside the target application's process.

For a 2560×1440 source, the visible bridge remains 2560×1440 while the neural branch is approximately 854×480.

## How the experimental compositor differs

The main dev.14 branch is tuned for balanced Neural Rendering reconstruction. This branch biases the same stable residual path toward clarity:

```text
native
  + motion-gated neural structure/luminance residual
  + current-frame local luminance contrast
  - small bright-neutral veil term
```

The source-derived clarity term always comes from the frame being displayed, so it does not inherit stale-frame chromatic trails. Neural color information is reduced as motion rises, while the current source detail remains active.

## Install from this branch

You need your own legitimate Lossless Scaling installation plus the external compatibility/runtime files described in [Installation](docs/install.md). Those files are not included here.

Clone this branch:

```powershell
git clone --branch experimental-soft-cheat https://github.com/eikkapine/NR-Auto-Scale.git
cd NR-Auto-Scale
```

Build the proxy and bridge:

```powershell
.\auto-scale\build.ps1
.\bridge\build.ps1
```

Then run the source-tree setup:

```powershell
.\auto-scale\scripts\Setup.cmd
```

Fresh setup should use:

```ini
NativeResolution=0
WorkingScale=0
NeuralMaxHeight=480
```

When you press **Scale** in Lossless Scaling, the proxy launches the bridge automatically and targets the bridge output.

## Requirements

- Windows 11
- Lossless Scaling from an official source
- AMD Radeon GPU; development has focused on RDNA4 / RX 9070 XT
- CMake 3.20+, MSVC C++ build tools and a Windows SDK when building from source
- The AMD HIP/runtime pieces required by the chosen compatibility runtime
- User-supplied compatibility/runtime files described in [Installation](docs/install.md)

## Verification for this branch

The soft-cheat.2 Release bridge and proxy build successfully, the bridge pixel tests pass, and the full auto-scale/setup harness passes on a clean rerun. The local validation build includes the same HIP 7 timing/pacing support used by the accepted dev.14 main build.

I have not yet done a new manual visual acceptance pass for soft-cheat.2, so the stronger clarity/haze behavior remains an experimental target rather than a measured claim. No new screenshots were added.

## Public-file boundary

This repository contains project source code and documentation. It does not include:

- paid Lossless Scaling binaries or `Lossless_original.dll`
- NVIDIA DLSS-NR DLLs, models or weights
- AMD compatibility proxy/runtime binaries
- private INI files, logs, backups or machine-specific runtime files
- personal files or secrets

The project-built proxy is also named `Lossless.dll`, but it is my own forwarding wrapper. The paid original stays in the local Lossless Scaling installation and is never included here.

This branch does not contain anti-cheat bypasses or in-process game injection.

## Documentation

- [Installation](docs/install.md)
- [Architecture](docs/architecture.md)
- [Performance](docs/performance.md)
- [Verification](docs/verification.md)
- [Licensing](docs/licensing.md)
- [Bridge internals](bridge/README.md)

## Credits

The project builds on compatibility work and ideas from:

- [danielblnc/DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD)
- [FrankBarretta/LSP-ReShade](https://github.com/FrankBarretta/LSP-ReShade)
- [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder)

My original project code is released under the [MIT License](LICENSE). Third-party components keep their own licenses and terms.
