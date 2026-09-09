# Performance

My performance target is simple: keep the final visible output at the game's/window's normal resolution while reducing the pixels and synchronization work spent on the expensive neural pass.

## Lossless Scaling

The default path captures the source at native size, creates a reduced neural input, runs Neural Rendering asynchronously and applies the stable residual back over the untouched current native frame.

With `NeuralMaxHeight=480`, a 2560×1440 source uses about 854×480 for the neural branch while the visible bridge remains 2560×1440.

The current compositor also avoids the earlier long-lived trails by refusing to temporally blur the current source frame. Correction history is only reused where the source is effectively unchanged, and stale chroma/magnitude is reduced as motion rises.

## Direct game

When a supported game exposes FSR inputs, the neural runtime can work on the render-resolution colour/motion/depth data before the game's final FSR reconstruction. The game can therefore keep a native final output while the hidden neural work follows the lower FSR input size.

Runtime diagnostics report both FSR input and swapchain/output resolution. If those sizes are equal, the neural pass is effectively running at the final output resolution.

## Measurement policy

I publish performance numbers only from hashed logs. The project keeps these measurements separate:

- PresentMon game/display frame timing
- bridge feed/presentation cadence
- HIP/runtime neural timing
- runtime fault/error state

Capture real game/display timing with:

```powershell
.\tools\Capture-Performance.ps1 -ProcessName Game.exe -Seconds 30
```

Analyze it with:

```powershell
py .\bridge\scripts\Analyze-Run.py --help
```

The analyzer writes sanitized JSON containing SHA-256 hashes of the raw inputs. Raw CSV/log files and machine paths stay private.

`tools/Check-Publication.py` rejects concrete FPS numbers in normal prose and measurement JSON that lacks the required schema/source-hash provenance.

## Quality boundary

The desktop Lossless Scaling bridge still lacks true engine motion/depth/jitter/exposure inputs, so it deliberately filters unstable broad temporal corrections. The direct-game route is the preferred path for the strongest Neural Rendering look because it operates where the game-owned temporal data exists.
