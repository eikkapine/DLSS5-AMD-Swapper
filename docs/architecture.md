# Architecture

DLSS5 AMD Swapper has one Windows manager and two independent execution paths. I keep them separate because a game-integrated temporal path and a desktop-capture path have very different input data and visual limits.

## Windows manager

The .NET 8 WPF app owns discovery, compatibility probing, reversible install/update/remove operations, hotkeys, settings and log-backed diagnostics. It does not bundle the third-party runtime files required by either path.

```text
DLSS5 AMD Swapper
├─ Game library / compatibility probe
├─ Direct-game installer + rollback manifest
├─ Runtime controls + diagnostics
└─ Lossless Scaling installer / status
```

## Direct-game AMD path

```text
game render-resolution colour + motion + depth
                    │
                    ▼
        DLSS-NR-on-AMD integration
                    │
                    ▼
            Neural Rendering
                    │
                    ▼
       game's FSR reconstruction
                    │
                    ▼
             native output
```

The manager validates x64/FSR/DX12 evidence, blocks common anti-cheat markers, verifies the user-supplied official upstream setup, verifies the generated rich configuration and keeps a hash-backed manifest for safe restore/update behavior.

Runtime diagnostics look for FidelityFX dispatches, colour/motion/depth staging, zero-copy interop, HIP selection, neural job timing and fault/error markers. A compatible folder alone is not enough to claim the rich temporal path ran.

## Lossless Scaling path

```text
captured window
      │ Windows Graphics Capture
      ▼
native source texture ──────────────────────┐
      │                                      │
      └─ GPU resize → capped neural input    │
                         │                   │
                         ▼                   │
                  Neural Rendering           │
                         │                   │
                         ▼                   │
                 stabilized residual         │
                         └──────────────┬─────┘
                                        ▼
                           native-resolution composite
                                        │
                                        ▼
                         project Lossless.dll proxy
                                        │
                                        ▼
                             Lossless Scaling output
```

The project-built `Lossless.dll` privately forwards to the locally preserved original Lossless Scaling DLL and starts/stops the bridge with a normal Scale session. The bridge captures through WGC; it does not inject into the source application.

`NeuralMaxHeight=480` keeps the presentation texture at the source size while capping only the neural input. `WorkingScale` and full 1:1 neural processing remain available as development/reference modes.

The compositor keeps the current native source as the visible base. It applies only the stable part of the asynchronous neural residual and rejects broad stale colour/luminance changes that caused the earlier flicker, trails and wet-paint motion artifacts.

## Why the routes differ visually

A finished desktop frame does not contain the engine's true motion vectors, depth, jitter, exposure state or pre-upscale render buffer. The Lossless Scaling route therefore cannot safely preserve every large temporal change the model produces.

The direct-game route has access to the temporal/upscaler contract and can keep much stronger neural changes while following surfaces through motion. That is why it is the preferred route when a game is compatible.

## Runtime boundary

Paid Lossless Scaling files, NVIDIA runtime/model files, third-party AMD compatibility binaries/installers, private manifests and raw logs stay outside the repository and release ZIP.
