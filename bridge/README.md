# DlssNrBridge

`DlssNrBridge.exe` captures one selected window with Windows Graphics Capture, feeds a reduced color image to the AMD DLSS-NR compatibility runtime, and presents a native-resolution D3D11 bridge window for Lossless Scaling.

The current main checkpoint is **v0.1.0-pre.3-dev.15**. With `--neural-max-height 480`, the visible source stays native-sized while only the neural branch is reduced. A 2560×1440 source therefore uses about 854×480 for Neural Rendering while the bridge output remains 2560×1440.

The bridge keeps the accepted dev.14 compositor: the current native source is the image base, the asynchronous neural output is treated as a residual over the current reduced feed, broad unstable residual color/luminance is filtered, and stale chroma/magnitude is reduced as motion rises. Accepted correction history is reused only where the native source is effectively unchanged, avoiding the earlier long-lived trails and wet-paint smearing.

The native source itself is never temporally blurred over a changed pixel. Duplicate visible presents can continue at the fast feed cadence so Lossless Scaling does not collapse to capture cadence while neural work is still in flight.

## Keyboard shortcuts

- `Ctrl+Alt+F6` toggles original/processed output.
- `Ctrl+Alt+F7` decreases strength.
- `Ctrl+Alt+F8` increases strength.

Native neural-residual mode starts at `1.0` and supports up to `4.0`. Above `1.0`, strength changes in 0.25 steps; from `0..1`, it changes in 0.1 steps. Legacy modes remain capped at `1.0`.

## Main options

- `--source-hwnd <HWND>` selects the captured window.
- `--neural-max-height <N>` caps only the neural branch while keeping native visible output.
- `--working-scale <0..1>` selects the legacy source-relative neural size when the neural-height cap is disabled.
- `--native-resolution` enables full 1:1 neural processing when both reduced modes are disabled.
- `--hip-kernel-timing` enables optional HIP timing instrumentation.

Fresh setup uses `WorkingScale=0` and `NeuralMaxHeight=480`.

## GPU path

The normal path keeps capture conversion, neural input resize, neural handoff, composition and presentation on GPU resources. D3D11/D3D12 shared textures and fences avoid steady-state full-frame CPU readback. Small status/timing buffers are used for diagnostics and pacing.

The bridge still uses the color-only compatibility path for Lossless Scaling. It does not receive the game's real engine motion vectors, depth, jitter, exposure or pre-upscale color buffer. Dev.15 therefore keeps the stable residual filter rather than claiming full guided temporal parity with an in-game integration.

The separate `direct-game/` route exists for games where those temporal/upscaler inputs can be obtained inside the FSR/DX12 path.

## Runtime files

Proxy mode expects locally supplied runtime files beside the private bridge installation, including the AMD compatibility proxy and a legally obtained NVIDIA DLSS-NR DLL. Those files are not part of this repository or release package.

The Lossless Scaling route keeps its asynchronous runtime configuration (`Inline=0`) because the bridge is designed around the compatibility runtime's completed-output cadence. The direct-game route uses its own upstream-generated rich temporal configuration separately.

## Build

```powershell
.\bridge\build.ps1
```

The Windows SDK shader compiler embeds the transport shaders into the production bridge build. Optional HIP timing support uses the locally available HIP headers when present; normal direct-game use does not require installing a separate ROCm SDK.

## Measurement boundary

`bridge/scripts/Analyze-Run.py` separates bridge cadence, HIP/runtime timing and actual game/display timing. PresentMon CSV input is required for a real game/display frame-rate measurement. Public analyzer JSON records SHA-256 hashes of the raw sources; raw logs stay private.

## Verification boundary

For dev.15 I verify the Release bridge build, bridge pixel tests, Release proxy build, publication provenance gate, release artifact hashes and allowlisted package contents before publishing. No new screenshot is added for this checkpoint.
