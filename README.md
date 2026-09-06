# NR Auto Scale

NR Auto Scale is an experimental integration for an AMD DLSS Neural Rendering compatibility runtime with Lossless Scaling.

It is not an official NVIDIA tool, it does not include NVIDIA or Lossless Scaling binaries, and it is not a native DLSS 5 engine implementation. The goal is narrower: make the AMD neural-rendering experiment start from Lossless Scaling's normal Scale button, then expose simple hotkeys for live on/off and strength control.

Repo: <https://github.com/eikkapine/NR-Auto-Scale>

## Current Status

Current preview: `v0.1.0-pre.1`

This is an experimental preview. It is useful for testers, but it is not a proven native-resolution automatic release yet.

Verified so far:

- Standalone D3D12 probe: neural jobs complete and the enabled path changes the rendered image.
- Bridge path: on/off/blend controls work against a tested 960x540 source.
- Native bridge proof: 2560x1440 same-frame input matched the original with max error `0`; NR output changed the image with mean absolute difference `2.87` and max channel delta `55`.
- Lossless Scaling automatic Scale/Unscale path: verified previously for the 960-to-1440 diagnostic path.
- Native wrapper harness: all 9 fake integration cases pass.

Still required before claiming a working public release:

- Verify the full native-resolution Lossless Scaling path from the real Scale button. The latest stopped test did not produce the expected proxy activation log before testing was halted.
- Verify output on a static desktop image at native WGC resolution.
- Confirm the wrapper keeps the Lossless Scaling path at native resolution with no geometry resizing.

## Visual Difference

The public comparison crops are approved analytical crops from the same frozen source frame. They are the same pixel rectangle with no resizing, no sharpening, and no color edits.

| Original crop | NR enabled crop |
| --- | --- |
| ![Original native-resolution crop](docs/images/cs2-native-off.png) | ![NR enabled native-resolution crop](docs/images/cs2-native-on.png) |

These crops are for visual inspection only. The final native-resolution Lossless Scaling app path still needs full verification.

## What It Does

NR Auto Scale is designed to:

1. Let the user press Scale in Lossless Scaling.
2. Load the project-built proxy `Lossless.dll`.
3. Forward to the user's local original Lossless Scaling DLL, kept privately as `Lossless_original.dll` during local installation.
4. Start the bridge automatically.
5. Send Lossless Scaling the bridge output window.
6. Keep the Lossless Scaling path at native resolution with no geometry resizing.
7. Let the user toggle and blend the effect live.

The project-built `Lossless.dll` is not the paid Lossless Scaling original DLL.

## Hotkeys

| Hotkey | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle NR output on/off |
| `Ctrl+Alt+F7` | Decrease output blend toward original |
| `Ctrl+Alt+F8` | Increase output blend toward NR output |

## Install

Use the preview package only if you are comfortable testing experimental Windows graphics tooling.

1. Install Lossless Scaling from its official store page.
2. Download `v0.1.0-pre.1` from <https://github.com/eikkapine/NR-Auto-Scale/releases/tag/v0.1.0-pre.1>.
3. Extract the package outside the Lossless Scaling folder.
4. Run:

   ```powershell
   .\Setup.cmd
   ```

5. Pick your Lossless Scaling install folder when prompted.
6. Supply your own local AMD proxy, NVIDIA DLL, and HIP 7.2 runtime files when prompted, following the upstream runtime instructions for those components. The release package does not include NVIDIA DLLs, AMD proxy binaries, model files, HIP runtime installers, or paid Lossless Scaling files.
7. Press Scale.

The setup flow is still experimental. Previous automatic Scale/Unscale testing passed for the diagnostic 960-to-1440 path, but the full native-resolution Lossless Scaling app path is not verified yet.

## Build From Source

Requirements:

- Windows 11 24H2 for the currently verified WGC route
- .NET SDK 8 for the optional legacy controls helper
- CMake 3.20 or newer
- MSVC C++ build tools

Build the helper tools:

```powershell
dotnet build .\controls\DlssNrControl\DlssNrControl.csproj -c Release
.\auto-scale\build.ps1
.\bridge\build.ps1
```

Run the wrapper harness:

```powershell
.\auto-scale\tests\Run-AutoScaleTests.ps1
```

Run the standalone probe:

```powershell
.\probe\run_probe.ps1 -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1
```

## Performance Status

The native 2560x1440 bridge proof completed neural jobs at about `63 ms/job`. That is bridge/runtime timing, not full Lossless Scaling end-to-end latency.

The current bridge still uses CPU readback/copy for visible output with asynchronous latency. Treat it as experimental. Do not describe it as suitable for competitive gameplay until a lower-latency path is implemented and tested.

## Repository Contents

The public repo may include original source, scripts, documentation, license files, and approved project-built artifacts.

The public repo must not include:

- Paid Lossless Scaling files, including the original `Lossless.dll`, `Lossless_original.dll`, app executables, app assets, configs copied from the paid app, or local backups.
- NVIDIA DLLs, SDK files, model files, or other vendor runtime files.
- AMD proxy binaries, installers, configs, or copied source from references that do not permit redistribution.
- User files, logs, crash dumps, traces, Windows wallpaper images, movie frames, or browser captures.

Only the approved analytical comparison crops under `docs/images/` are intended for the GitHub page.

## License And Attribution

Original source and documentation in this repository are MIT licensed. See [LICENSE](LICENSE).

This repo references several upstream projects and private runtime experiments. Their licenses still apply. See [Licensing And Attribution](docs/licensing.md) before redistributing binaries or copied source.
