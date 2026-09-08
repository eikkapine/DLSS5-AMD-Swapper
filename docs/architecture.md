# Architecture

NR Auto Scale is split into two pieces: a small Lossless Scaling proxy and a separate bridge process.

```text
source window
    │
    │ Windows Graphics Capture
    ▼
DlssNrBridge.exe
    │
    ├─ capture / format conversion
    ├─ neural runtime feed
    ├─ AMD HIP execution
    ├─ exact RGB change checks
    └─ original ↔ neural output blend
    │
    ▼
project Lossless.dll proxy
    │
    ▼
original Lossless Scaling DLL
```

## Auto-scale proxy

The project-built `Lossless.dll` loads the locally preserved `Lossless_original.dll`, forwards the Lossless Scaling exports, and wraps the calls needed to start/stop the bridge around a scaling session.

When scaling starts, the proxy launches `DlssNrBridge.exe` with the source HWND and current bridge configuration. The bridge publishes a ready file only after it has a valid visible output window. The proxy validates that HWND and passes it to the original Lossless Scaling activation path.

The source application is captured through WGC. The bridge does not need to inject itself into that source process.

## Bridge

The bridge owns the live effect controls and the visible output window. Its main stages are:

1. Capture the selected source with Windows Graphics Capture.
2. Prepare the frame for the D3D12 neural runtime.
3. Feed the external DLSS-NR compatibility runtime.
4. Read/use the completed neural output.
5. Present original, neural, or a blend between them.
6. Let Lossless Scaling capture that bridge output.

The current native GPU path uses shared D3D11/D3D12 resources and fences to reduce full-frame CPU transfers. A small status readback is used for change/health information instead of continuously copying the whole output to the CPU.

## Native mode

With `NativeResolution=1`, the bridge waits for the first valid WGC frame and creates its processing/output resources at the captured source size. No geometry scaling is performed inside the bridge in that mode.

Native mode currently supports source dimensions up to 3840×2160. A source-size change ends the session instead of silently reallocating into a different geometry.

## Fixed-size mode

The default preview configuration uses 1280×720 processing bounds and preserves source aspect ratio. Lossless Scaling can then apply the scaler selected in its profile.

## Live blend

The bridge keeps original and neural images available for presentation:

- `Ctrl+Alt+F6` toggles original/processed output.
- `Ctrl+Alt+F7` reduces the neural blend by `0.1`.
- `Ctrl+Alt+F8` increases the neural blend by `0.1`.

Intermediate strengths use the same byte-domain integer blend used by the bridge's CPU path. Full strength can present the neural texture directly on the GPU path.

## Runtime boundary

The external AMD compatibility proxy, NVIDIA runtime DLL/model material, HIP runtime, and paid Lossless Scaling files are not part of this repository. Setup copies only user-supplied external files into the private local runtime folder.

See [Licensing](licensing.md) for the boundary and [Verification](verification.md) for the tested parts of this architecture.
