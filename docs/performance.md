# Performance

The current main checkpoint is **v0.1.0-pre.3-dev.15**. The Lossless Scaling path keeps dev.14's accepted performance layout: native visible output with only the neural branch capped to **480 pixels high** by default. A 2560×1440 source therefore uses about 854×480 for the neural pass while the bridge still presents 2560×1440.

The direct-game route adds a second optimization path. When a supported game exposes FSR render-resolution inputs, Neural Rendering can run before the game's final reconstruction instead of processing the full output-resolution frame. The final display target stays native; the hidden neural workload follows the game's actual FSR input dimensions.

## Lossless Scaling path

The bridge keeps the current native source as the visible base and applies a stabilized asynchronous neural residual over it:

1. capture the source at native size
2. downsample only the neural input when `NeuralMaxHeight` is active
3. run Neural Rendering on the reduced branch
4. reject broad unstable residual color/luminance
5. reduce stale chroma and correction magnitude as motion rises
6. reuse accepted correction history only where the source is effectively unchanged
7. composite the stable residual over the untouched current native source
8. keep visible submissions flowing independently from neural completion cadence

The neural network remains the dominant cost. Reducing only its pixel count avoids turning the final visible image itself into a low-resolution upscale.

## Direct-game path

For a supported game, the upstream runtime can receive render-resolution color plus temporal guide data from the FSR path. The helper verifies the generated configuration and `--diagnose` checks the runtime log for FidelityFX dispatch, color/motion/depth staging, interop mode, HIP selection, timing samples, and fault/error markers.

If the logged FSR input dimensions equal the swapchain/output dimensions, Neural Rendering is effectively running at full output resolution. Use an in-game FSR quality mode when the goal is to keep native final output while reducing the hidden neural workload.

## Measurement policy

I publish performance numbers only from hashed raw logs. Bridge cadence, HIP/runtime timing, and actual game/display timing are separate measurements.

For game/display timing:

```powershell
.\tools\Capture-Performance.ps1 -ProcessName Game.exe -Seconds 30
```

Then analyze the PresentMon CSV together with any bridge/HIP/runtime logs:

```powershell
py .\bridge\scripts\Analyze-Run.py --help
```

The analyzer writes sanitized JSON and records the SHA-256 of every raw source. Raw CSV/log files, process paths, private runtime configuration, and captures stay outside the repository.

The current direct-game validation includes a private PresentMon/runtime smoke capture proving the rich path executed on the tested AMD system. That run was a startup/runtime validation at full-resolution FSR input, not a controlled A/B benchmark, so it is not used as a public optimized-performance headline.

Historical sanitized measurement files remain under `docs/measurements/`. They are schema-versioned, hash-backed analyzer output rather than hand-entered performance notes.

`tools/Check-Publication.py` fails the release if public prose contains concrete FPS numbers or if measurement JSON lacks source hashes/schema provenance.

## Quality boundary

The Lossless Scaling bridge receives a finished color frame and cannot reconstruct true engine motion vectors, depth, jitter, exposure, or pre-upscale color. Its stable residual filter intentionally rejects broad temporal corrections that would otherwise flicker or trail.

The direct-game route is therefore the preferred path for the strongest Neural Rendering look. It places the model where the game/upscaler data exists and can preserve larger material, shading, and local-structure changes without relying on final-frame color alone.
