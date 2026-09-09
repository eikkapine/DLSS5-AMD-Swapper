# NR Auto Scale — experimental soft-cheat branch

This branch preserves the earlier **dev.5 high-frequency Neural Rendering compositor** that I tested before moving the main project toward broader DLSS-NR reconstruction.

The visible image stays at the captured application's native resolution. Only the expensive neural branch is capped to **480 pixels high** by default. The neural result is then used as a high-frequency detail layer over the native frame.

That produces a different look from the main branch: it is mostly perceived as extra sharpness and distant-detail separation. In my testing it could also make distant fog or haze look weaker in some scenes, which is why I keep this as a separate **experimental soft-cheat** branch instead of mixing it into the normal visual-quality path.

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
- Keeps the normal D3D11/D3D12 shared-resource path.
- Extracts high-frequency structure from the neural result with a five-sample cross filter.
- Adds that detail back to the untouched native source instead of using the low-resolution neural image as the visible base.
- Keeps the bridge outside the target application's process.

For a 2560×1440 source, the visible bridge remains 2560×1440 while the neural branch is approximately 854×480.

## Why this branch exists

The dev.5 compositor deliberately discards the neural result's low-frequency image base. The GPU path computes a small blur from the neural output and applies only:

```text
native + (neural - neural_blur) * strength
```

This makes the result behave more like a neural detail filter. It also avoids depending on the exact input frame that produced an asynchronously published neural frame, which is useful for this experiment.

This branch was reconstructed from the exact dev.5 implementation edits preserved in my previous development session and checked against the preserved dev.5 behavior. I do not claim the newly compiled executable is byte-for-byte identical to the old binary.

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

The reconstructed branch builds successfully in Release mode and the existing bridge pixel tests pass. No gameplay automation or computer-use test was run for this branch.

The main visual evidence for this mode comes from the earlier manual testing where the effect was seen primarily as sharpening/detail separation and reduced distant fog/haze. That observation is the reason this code is preserved separately.

## Public-file boundary

This repository contains project source code and documentation. It does not include:

- paid Lossless Scaling binaries or `Lossless_original.dll`
- NVIDIA DLSS-NR DLLs, models or weights
- AMD compatibility proxy/runtime binaries
- private INI files, logs, backups or machine-specific runtime files
- personal files or secrets

The project-built proxy is also named `Lossless.dll`, but it is my own forwarding wrapper. The paid original remains private on the user's installed machine.

Use external filters only where the software or game rules you are using allow them. This branch does not contain anti-cheat bypasses or in-process game injection.

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
