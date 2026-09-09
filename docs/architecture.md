# Architecture

NR Auto Scale is split into a small Lossless Scaling proxy and a separate bridge process.

```text
source window
    │
    │ Windows Graphics Capture
    ▼
source-size D3D11 capture texture
    │
    ├─ exact copy when source == neural size
    └─ GPU bilinear resize when WorkingScale < 1
    ▼
shared neural input texture
    │
    ▼
D3D12 neural feed / AMD HIP compatibility runtime
    │
    ▼
processed bridge output
    │
    ▼
project Lossless.dll proxy → original Lossless Scaling
    │
    ▼
selected Lossless Scaling scaler / optional frame generation
```

## Auto-scale proxy

The project-built `Lossless.dll` loads the locally preserved `Lossless_original.dll`, forwards the required exports, and wraps the activation flow needed to start/stop the bridge around a normal scaling session.

When scaling starts, the proxy launches `DlssNrBridge.exe` with the source HWND and current configuration. The bridge publishes a ready file only after it has a valid visible output window. The proxy validates that HWND and passes it to the original Lossless Scaling activation path.

The source application is captured through WGC. The bridge does not inject into the source process.

## Working-scale path

`WorkingScale` derives the neural dimensions from the captured source. New installs use `0.75`.

For a 2560×1440 source:

```text
2560×1440 WGC capture
        ↓ GPU resize
1920×1080 neural input
        ↓ Neural Rendering
1920×1080 bridge output
        ↓ Lossless Scaling
2560×1440 display target
```

The WGC surface stays at source resolution. The bridge prefers using the capture surface directly as a shader resource. If that is unavailable, the fallback copy is allocated at source size and is still resized on the GPU. The reduced path therefore avoids steady-state full-frame CPU capture/readback.

## Shared GPU transport

The normal path shares textures and fences between the D3D11 capture/presentation device and the D3D12 neural device. A 16-byte status readback carries change/health information instead of continuously copying the whole image to the CPU.

The 1:1 path uses exact texel loads. Bilinear sampling is used only when the source and neural dimensions differ deliberately.

Accepted full-strength neural output is retained in alternating shared output slots, avoiding another full-frame history copy for the common full-strength case.

## Processing modes

- `WorkingScale=0.25..0.998`: source-relative pre-upscale neural mode.
- `WorkingScale=0`: disable the source-relative working scale.
- `WorkingScale=0` + `NativeResolution=1`: native 1:1 neural processing.
- `WorkingScale=0` + `NativeResolution=0`: older fixed `Width` / `Height` mode.

`WorkingScale` takes precedence when it is enabled.

## Live blend

- `Ctrl+Alt+F6` toggles original/processed output.
- `Ctrl+Alt+F7` reduces the neural blend by `0.1`.
- `Ctrl+Alt+F8` increases the neural blend by `0.1`.

Intermediate strengths use the same byte-domain integer blend as the CPU compatibility path. Full strength can present the neural texture directly on the GPU path.

## Runtime boundary

The external AMD compatibility proxy, NVIDIA runtime/model material, HIP runtime and paid Lossless Scaling files are not part of this repository. Setup copies only user-supplied external files into the private local runtime folder.

See [Licensing](licensing.md) for the boundary and [Verification](verification.md) for the tested parts of the architecture.
