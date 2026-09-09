# DLSS NR Bridge

`DlssNrBridge.exe` captures one selected window with Windows Graphics Capture, feeds a reduced color image to the AMD DLSS-NR compatibility runtime, and presents a native-resolution D3D11 bridge window for Lossless Scaling.

The current checkpoint is **v0.1.0-pre.3-dev.6**. With `--neural-max-height 480`, the source remains native-sized for visible output while only the neural branch is reduced. A 2560×1440 source uses an approximately 854×480 neural texture.

Dev.6 keeps one low-resolution input history texture. Because the color-only runtime can publish an asynchronous full neural frame from an earlier feed, the compositor compares the neural output against the current and previous low-resolution inputs, chooses the better match, extracts the neural delta, and adds that delta to the untouched native source. A simple temporal-confidence gate fades broad stale corrections in moving regions.

This path needs three filtered low-resolution samples per visible pixel. The previous dev.5 high-pass compositor used five neural samples and discarded most low/mid-frequency neural reconstruction.

## Keyboard shortcuts

- `Ctrl+Alt+F6` toggles processed output on/off.
- `Ctrl+Alt+F7` decreases strength.
- `Ctrl+Alt+F8` increases strength.

Native neural-delta mode starts at `1.0` and supports up to `4.0`. Above `1.0`, strength changes in 0.25 steps; from `0..1`, it changes in 0.1 steps. Legacy modes remain capped at `1.0`.

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

The bridge still uses `UseFsrInputs=0` for the Lossless Scaling color-only path. That means it does not receive engine depth or motion-vector buffers, so it cannot reproduce the full guided temporal behavior of an in-game FSR hook. Dev.6's temporal color matching is deliberately lightweight and does not claim to recreate missing engine data.

## Runtime files

Proxy mode expects user-supplied runtime files beside the private bridge installation, including the AMD proxy and the user's legally obtained NVIDIA DLSS-NR DLL. Those files are not part of this repository or release package.

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

The dev.6 production bridge compiled successfully and the installed binary matched the built SHA-256. The user then manually accepted the dev.6 visual behavior through Lossless Scaling. No new automated gameplay, GPU benchmark or screenshot capture was run for this release.
