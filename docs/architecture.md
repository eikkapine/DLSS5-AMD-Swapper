# Architecture

DLSS5 AMD Swapper has one Windows manager and two execution paths: Direct Game (with two backend routes) and Lossless Scaling. I keep them separate because a game-integrated temporal path and a desktop-capture path have very different input data and visual limits.

## Windows manager

The .NET 8 WPF app owns discovery, compatibility probing, reversible install/update/remove operations, hotkeys, settings and log-backed diagnostics. It does not bundle the third-party runtime files required by either path.

```text
DLSS5 AMD Swapper
├─ Game library / compatibility probe
├─ Direct-game installer (post-FSR runtime)
├─ OptiScaler pre-SR installer + package discovery
│   ├─ OptiScalerPackageService (PE x64, fork marker, SHA256SUMS)
│   ├─ OptiScalerInstallerService (reversible install & manifest)
│   ├─ OptiScalerIniWriter (Quality & Performance presets)
│   ├─ OptiScalerDiagnosticsService (log extraction)
│   └─ OptiScalerControlService (passes & layer adjustments)
├─ Runtime controls + diagnostics
└─ Lossless Scaling installer / status / layer control
```

Manifests are stored as `.dlss5-amd-swapper.json` (schema 3) with a top-level `route` field indicating either `amd-fsr-direct` (the post-FSR route) or `amd-optiscaler-presr` (the OptiScaler pre-SR route). The manifest records exact before/after file hashes for reversible rollback and safe restore.

## Direct-game AMD paths

Direct Game supports two execution backends:

### 1. Official post-FSR runtime (`amd-fsr-direct`)

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

### 2. OptiScaler pre-SR route (`amd-optiscaler-presr`)

```text
game render-resolution colour + motion + depth
                    │
                    ▼
          dlssnr_amd_pass*.dll
                    │
                    ▼
         Neural Rendering (pre-SR)
                    │
                    ▼
         OptiScaler (FSR (ffx))
                    │
                    ▼
         [optional DLSS-G / FFX FG]
                    │
                    ▼
             native output
```

The pre-SR route evaluates the neural model on the unscaled render buffer before super-resolution upscaling, reducing the pixel count fed into the neural network compared to a native-resolution pass.

OptiScaler hooks into the game's DirectX 12 super-resolution dispatch. The route uses `dxgi.dll` as proxy, `OptiScaler.ini`, up to three renamed pass DLLs (`dlssnr_amd_pass1/2/3.dll`), locally generated weights, and the `OptiScaler\` dependency directory.

Diagnostics parse `amd_presr.log` and `OptiScaler.log` to track pre-SR activation, HIP adapter, initialized and completed passes, model and target dimensions, and GPU evaluation times.

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

The compositor keeps the current native source as the visible base. It applies only the stable part of the asynchronous neural residual and rejects broad stale colour/luminance changes that caused earlier flicker, trails and wet-paint motion artifacts.

OptiScaler cannot be run inside Lossless Scaling because Lossless Scaling uses Direct3D 11 presentation for captured window frames, whereas OptiScaler pre-SR requires hooking a DirectX 12 super-resolution call.

## Why the routes differ visually

A finished desktop frame does not contain the engine's true motion vectors, depth, jitter, exposure state or pre-upscale render buffer. The Lossless Scaling route therefore cannot safely preserve every large temporal change the model produces.

The direct-game route has access to the temporal/upscaler contract and can keep much stronger neural changes while following surfaces through motion. That is why it is the preferred route when a game is compatible.

## Runtime boundary

Paid Lossless Scaling files, NVIDIA runtime/model files, third-party AMD compatibility binaries/installers, OptiScaler fork binaries, private manifests and raw logs stay outside the repository and release ZIP.
