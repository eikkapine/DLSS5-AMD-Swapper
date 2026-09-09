# Architecture

NR Auto Scale has two independent integration paths. The direct-game path is preferred when a supported game exposes the right temporal/upscaler data. The Lossless Scaling bridge remains the compatibility path for arbitrary capturable windows.

## Direct-game AMD path

```text
game render-resolution colour + temporal guide data
    │
    ▼
DLSS-NR-on-AMD integration inside the FSR/DX12 game path
    │
    ▼
Neural Rendering at the game's render resolution
    │
    ▼
game FSR reconstruction
    │
    ▼
native output
```

The direct-game installer logic lives in `direct-game/amd_dlss5.py`. It detects x64, FSR, DirectX 12 evidence and common anti-cheat markers, verifies the user's own upstream installer against GitHub's published SHA-256 digest, and records a reversible local manifest. Third-party binaries are never part of this repository.

The direct path can preserve broader neural changes because the integration has the temporal/upscaler data that a screen-capture bridge cannot reconstruct from final colour alone. The helper also verifies that the generated runtime config has `UseFsrInputs=1`, depth and temporal history enabled before it accepts an install.

## Lossless Scaling bridge

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

## Lossless Scaling processing size

The current experimental default keeps the visible source at its real size and caps only the neural branch with `NeuralMaxHeight=480`. `WorkingScale` remains available as a legacy source-relative mode.

For a 2560×1440 source with the current neural-height cap:

```text
2560×1440 WGC capture
        ↓ GPU resize
~854×480 neural input
        ↓ Neural Rendering
native-resolution residual/composite
        ↓ Lossless Scaling presentation
2560×1440 visible target
```

The WGC surface stays at source resolution. The bridge prefers using the capture surface directly as a shader resource. If that is unavailable, the fallback copy is allocated at source size and is still resized on the GPU. The reduced path therefore avoids steady-state full-frame CPU capture/readback.

## Shared GPU transport

The normal path shares textures and fences between the D3D11 capture/presentation device and the D3D12 neural device. A 16-byte status readback carries change/health information instead of continuously copying the whole image to the CPU.

The 1:1 path uses exact texel loads. Bilinear sampling is used only when the source and neural dimensions differ deliberately.

Accepted full-strength neural output is retained in alternating shared output slots, avoiding another full-frame history copy for the common full-strength case.

## Processing modes

- `NeuralMaxHeight>0`: current default; cap only the neural branch and keep native visible output.
- `WorkingScale=0.25..0.998`: source-relative pre-upscale neural mode.
- `WorkingScale=0`: disable the source-relative working scale.
- `WorkingScale=0` + `NativeResolution=1`: native 1:1 neural processing.
- `WorkingScale=0` + `NativeResolution=0`: older fixed `Width` / `Height` mode.

`NeuralMaxHeight` takes precedence when it is enabled. Disable it to use `WorkingScale` or the older native/fixed modes.

## Live blend

- `Ctrl+Alt+F6` toggles original/processed output.
- `Ctrl+Alt+F7` reduces strength.
- `Ctrl+Alt+F8` increases strength.

The experimental branch uses smaller steps up to the baseline and larger steps above it, with a maximum strength of `4.0`. Intermediate strengths use the same byte-domain integer blend as the CPU compatibility path where that path is active.

## Runtime boundary

The external AMD compatibility proxy, NVIDIA runtime/model material, AMD driver runtime and paid Lossless Scaling files are not part of this repository. Setup copies only user-supplied external files into the private local runtime folder.

See [Licensing](licensing.md) for the boundary and [Verification](verification.md) for the tested parts of the architecture.
