# DLSS NR Bridge

`DlssNrBridge.exe` captures one selected window with Windows Graphics Capture, feeds a reduced color image to the AMD DLSS-NR compatibility runtime, and presents a native-resolution D3D11 bridge window for Lossless Scaling.

This branch is **v0.1.0-pre.3-soft-cheat.4**. With `--neural-max-height 480`, the source remains native-sized for visible output while only the neural branch is reduced. A 2560×1440 source uses an approximately 854×480 neural texture.

The experimental compositor uses the dev.14 spatial residual filter, retains extra local neural structure/luminance, then adds a current-frame local-contrast and neutral-veil adjustment. Soft-cheat.3 tightens motion rejection around native-resolution edges and prevents duplicate frames from indefinitely holding an old corrected image.

## Keyboard shortcuts

- `Ctrl+Alt+F6` toggles processed output on/off.
- `Ctrl+Alt+F7` decreases strength.
- `Ctrl+Alt+F8` increases strength.

Native clarity mode starts at `1.0` and supports up to `4.0`. Above `1.0`, strength changes in 0.25 steps; from `0..1`, it changes in 0.1 steps. Legacy modes remain capped at `1.0`.

## Main options

- `--source-hwnd 0x...` selects a source window by handle.
- `--source-title "partial title"` selects a visible source by title.
- `--neural-max-height 480` keeps the visible source resolution and caps only neural processing height.
- `--working-scale 0.75` enables the older reduced source-relative mode.
- `--native-resolution` enables full 1:1 neural processing when no neural-height cap is set.
- `--width` / `--height` configure the fixed fallback mode.
- `--startup-delay-ms` and `--warmup-frames` control startup health gating.
- `--ready-file` publishes the visible bridge HWND after healthy output is available.
- `--stop-event` and `--parent-pid` provide bounded shutdown ownership.
- `--cpu-transport` forces the compatibility fallback path.
- `--hip-kernel-timing` enables optional diagnostic kernel sampling; it is off by default.

## GPU path

The normal path keeps capture conversion, neural input resize, neural handoff, composition and visible display on the GPU. Shared D3D11/D3D12 textures and fences preserve producer/consumer ownership. Only the small status counters are read back continuously.

The visible compositor always keeps the captured native source as the base in `NeuralMaxHeight` mode. It does not upscale the 480p neural output and replace the entire frame.

The bridge still uses `UseFsrInputs=0` for the Lossless Scaling color-only path. That means it does not receive engine depth or motion-vector buffers. This branch does not try to reconstruct those missing guides; it uses the neural output only as a spatial detail layer.

## Runtime files

Proxy mode expects locally supplied runtime files beside the private bridge installation, including the AMD proxy and a legally obtained NVIDIA DLSS-NR DLL. Those files are not part of this repository or release package.

The exercised runtime configuration keeps:

```ini
Enabled=1
UseFsrInputs=0
Inline=0
Interop=1
LocalStructure=1
```

## Build

```powershell
cmake -S bridge -B bridge/build -G "Visual Studio 17 2022" -A x64
cmake --build bridge/build --config Release --target DlssNrBridge
```

The Windows SDK shader compiler embeds the transport shaders into the production executable during the build.

## Verification boundary

The soft-cheat.4 bridge compiles successfully with HIP 7 pacing support and the bridge pixel tests pass. The accepted branch state keeps the motion clarity of soft-cheat.3; some flicker remains. No new screenshot was added for this checkpoint.
