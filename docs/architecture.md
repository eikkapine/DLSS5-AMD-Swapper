# Architecture

NR Auto Scale has two independent integration paths. The direct-game path is preferred when a supported game exposes the temporal/upscaler data Neural Rendering expects. The Lossless Scaling bridge remains the compatibility path for arbitrary capturable windows.

## Direct-game AMD path

```text
game render-resolution color + temporal guide data
    │
    ▼
DLSS-NR-on-AMD inside the game's FSR / DX12 path
    │
    ▼
Neural Rendering at the game's FSR input resolution
    │
    ▼
game FSR reconstruction
    │
    ▼
native output
```

`direct-game/amd_dlss5.py` does not implement or redistribute the third-party compatibility runtime. It verifies the user-supplied official setup file, checks the target for x64/FSR/DX12 evidence, blocks common anti-cheat markers, invokes the unchanged upstream installer, validates the generated rich configuration and records a reversible local hash manifest.

The helper requires the generated upstream config to enable FSR inputs, depth, temporal history, interop and inline gameplay mode. `--diagnose` then checks the runtime log for FidelityFX dispatches, color/motion/depth staging, zero-copy state, HIP selection, timing samples and fault/error markers.

This path can preserve larger neural changes because it has data that no desktop screenshot can reconstruct reliably: render-resolution color, motion vectors, depth, jitter/exposure context and game-owned temporal cadence.

## Lossless Scaling bridge

```text
source window
    │ Windows Graphics Capture
    ▼
native source texture
    │                         │
    │ keep for visible base   │ GPU resize
    │                         ▼
    │                  capped neural input
    │                         │
    │                         │ D3D12 / AMD HIP compatibility runtime
    │                         ▼
    │                  asynchronous neural output
    │                         │
    └──────────────┬──────────┘
                   │ filtered neural residual
                   ▼
        native-resolution composite
                   │
                   ▼
project Lossless.dll proxy → original Lossless Scaling
                   │
                   ▼
selected scaler / optional frame generation
```

The project-built `Lossless.dll` loads the privately preserved `Lossless_original.dll`, forwards the required exports and wraps activation so the bridge starts and stops with a normal Lossless Scaling Scale session.

The source application is captured through WGC. The bridge does not inject into the source process.

## Neural processing size

Fresh setup uses:

```ini
WorkingScale=0
NeuralMaxHeight=480
```

`NeuralMaxHeight` keeps the visible source at its real size and caps only the neural branch. For a 2560×1440 source:

```text
2560×1440 WGC capture
        ↓ GPU resize
~854×480 neural input
        ↓ Neural Rendering
~854×480 neural output
        ↓ filtered residual over current native source
2560×1440 bridge output
```

`WorkingScale` remains available as a legacy source-relative mode. Full 1:1 neural processing remains available for reference/quality work.

## Shared GPU transport

The normal bridge path shares textures and fences between the D3D11 capture/presentation device and the D3D12 neural device. A small status readback carries change/health information rather than continuously copying the full image to the CPU.

The bridge prefers the WGC capture surface directly as a shader resource. If that is unavailable, the fallback copy is still resized on the GPU. Full-frame CPU readback is reserved for diagnostics and compatibility fallback.

## Stable dev.14 compositor retained in dev.15

The Lossless Scaling path treats the neural output as an asynchronous residual over the current reduced input. Broad unstable residual color/luminance is removed spatially, stale chroma and magnitude are reduced when the source moves, and accepted correction history is reused only where the native source is effectively unchanged.

The current native source itself is never temporally blurred over a changed pixel. Duplicate visible presents can continue at the feed cadence without rewriting correction history.

This stabilizes the color-only path, but it does not recreate the true engine motion/depth/jitter/exposure contract of an in-game FSR hook. That is why the direct-game route exists separately.

## Controls

- `Ctrl+Alt+F6` toggles original/processed output.
- `Ctrl+Alt+F7` decreases strength.
- `Ctrl+Alt+F8` increases strength.

Native neural-residual mode supports `0.0..4.0` strength.

## Runtime boundary

External AMD compatibility runtimes/installers, NVIDIA runtime/model files, HIP/vendor DLLs and paid Lossless Scaling files are not part of this repository. Setup works only with user-supplied local files and keeps those files in private runtime locations.

See [Licensing](licensing.md) for the release boundary and [Verification](verification.md) for the exercised paths.
