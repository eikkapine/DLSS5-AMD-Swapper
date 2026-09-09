# Neural Rendering upstream of the upscaler

This is the architecture I am using for the direct-game AMD path.

## Why the Lossless Scaling path cannot reproduce the full look

The screen-capture bridge receives a finished colour image. It does not receive the game engine's true motion vectors, depth, jitter, exposure buffer or pre-upscale colour input.

That matters because Neural Rendering is temporal. Broad changes to lighting, material response, skin structure and local tone have to stay attached to the same surfaces from frame to frame. In the Lossless Scaling path, preserving too much of that broad correction causes flicker, trails or stale colour because the bridge has to infer motion from colour alone.

The current bridge therefore keeps the stable part of the neural result and rejects or limits broad unstable residuals. That is why it can look closer to a strong detail/shading pass than the much larger changes visible in native DLSS 5 examples.

## Direct-game target

The direct-game route runs inside the game's existing temporal/upscaler flow. For AMD, the first supported route is a 64-bit DirectX 12 game with FSR, using the user's own `DLSS-NR-on-AMD` installation.

The important change is placement:

```text
game render-resolution colour
        + depth / motion / jitter / exposure from the game/upscaler path
        -> Neural Rendering
        -> game's normal FSR reconstruction
        -> native output
```

Running the neural pass before the final upscale also means the expensive model sees the render-resolution image instead of the final output-resolution image. That is the same high-level direction used by `matiasLombo/neural-upstream`.

The current `DLSS-NR-on-AMD` setup generated for this route enables `UseFsrInputs=1`, `UseDepth=1`, `Temporal=1`, `Interop=1` and inline gameplay mode. Runtime diagnostics then verify whether FidelityFX dispatches and real color/motion/depth staging actually appeared in the game's log. That distinction matters: a compatible-looking game folder is not proof that the rich temporal path ran.

For the intended native-output/low-processing-resolution setup, keep the game's output at native resolution and let its FSR quality mode choose a lower render resolution. The neural pass then works on the FSR input image and FSR reconstructs the native output. If the logged FSR input dimensions equal the swapchain dimensions, the neural pass is effectively running at output resolution and will cost more.

## Current research references

- `matiasLombo/neural-upstream` hooks the game's DLSS evaluate, runs Neural Rendering at render resolution, then gives the result back to the game's upscaler. It also documents why cadence must be anchored to game-owned temporal state and why reusing a previous full image causes ghosting: <https://github.com/matiasLombo/neural-upstream>
- `jlrouzies-fr/DLSS5-Feeder` demonstrates the opposite limitation clearly: when a game has no native DLSS contract, depth plus estimated motion vectors can create a synthetic one, but temporal quality depends directly on those estimated vectors: <https://github.com/jlrouzies-fr/DLSS5-Feeder>
- `Kizzuwatnaa/DLSS5-Autopilot` is a useful routing/installer reference for choosing native, neural-upstream, OptiScaler, bridge or feeder paths without bundling third-party vendor payloads: <https://github.com/Kizzuwatnaa/DLSS5-Autopilot>
- `danielblnc/DLSS-NR-on-AMD` supplies the AMD execution path used by the first direct-game route: <https://github.com/danielblnc/DLSS-NR-on-AMD>
- `Dagherbou/OptiScaler_DLSSNR` is relevant for future games where an upscaler interception layer gives cleaner access to render/output extents and temporal inputs: <https://github.com/Dagherbou/OptiScaler_DLSSNR>

## Performance evidence policy

External articles and upstream README benchmark numbers are research context, not DLSS5 AMD Swapper benchmark data. I do not copy those FPS claims into this repository as project results.

For this project, actual FPS and frame-time numbers are published only from PresentMon logs captured by `tools/Capture-Performance.ps1` and converted to sanitized JSON by `bridge/scripts/Analyze-Run.py`.

Bridge cadence, HIP job timing and actual game/display FPS remain separate metrics.
