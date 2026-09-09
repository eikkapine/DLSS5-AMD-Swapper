# Performance

The current checkpoint is **v0.1.0-pre.3-dev.14**. Its main performance choice is unchanged: keep the visible frame at the captured application's native resolution while limiting only the expensive neural branch to **480 pixels high**.

For a 2560×1440 source, the bridge therefore uses roughly **854×480** for Neural Rendering and composites the resulting neural correction back onto the untouched 2560×1440 source.

## Performance history

| Checkpoint | Source | Neural working size | Manual observation |
| --- | ---: | ---: | --- |
| v0.1.0-pre.2 | 2560×1440 | 2560×1440 | ~19 FPS base |
| v0.1.0-pre.3-dev.3 | 2560×1440 | 1920×1080 | ~30 FPS base; ~60 FPS with 2× LSFG |
| dev.4 | 2560×1440 | ~854×480 | ~60 FPS base, frame generation off |
| v0.1.0-pre.3-dev.6 | native visible output | max 480p neural branch | neural behavior manually accepted |
| **v0.1.0-pre.3-dev.14** | native visible output | max 480p neural branch | stationary flicker fixed; moving flicker barely noticeable; performance still felt good |

These are user-observed gameplay results from different sessions, not controlled A/B benchmarks. I did not record a fresh numeric game-FPS value for dev.14. The bridge also does not count generated LSFG frames.

## What dev.14 changes

Dev.14 keeps the same low-resolution neural workload and changes only how the asynchronous neural correction is stabilized and presented:

1. keep the current native source as the visible base
2. downsample only the neural input to at most 480p
3. interpret the asynchronous neural backbuffer as a residual over the current reduced feed
4. remove broad unstable color/luminance residual components spatially
5. estimate movement from the previous reduced input
6. suppress stale chroma and stale residual magnitude as motion rises
7. reuse a previous correction only for effectively unchanged native pixels
8. keep duplicate visible presents flowing at the fast feed cadence so Lossless Scaling does not collapse to capture rate

No previous native source frame is blended over a changed pixel. This avoids the wet-paint/ghosting failure mode seen in earlier anti-flicker experiments.

## Why 480p helps

At a 2560×1440 source, 854×480 is about 11% of the native pixel count. The neural network therefore processes far fewer pixels while the user still sees the original native geometry/base image.

This is different from rendering the whole visible output at 480p and upscaling it. Only the neural branch is reduced.

## Quality boundary

The Lossless Scaling bridge receives a final color frame, not the game's engine depth and motion-vector buffers. The AMD DLSS-NR compatibility runtime can use those guide buffers when hooked to a real FSR pipeline, but this color-only bridge cannot recreate them.

Dev.14 therefore stabilizes the asynchronous color residual spatially and with source-change rejection. It does not claim to equal a native in-game guided DLSS-NR integration.

## Runtime settings kept unchanged

The accepted dev.14 path keeps the same model/weights, neural precision/settings, `Inline=0`, `Interop=1`, and full `1.0` baseline effect strength. `Ctrl+Alt+F8` can boost the visible neural correction above the baseline up to `4.0` without changing the neural model itself.

Raw runtime logs remain private. Public release metadata records only sanitized version/build information and exact artifact hashes.
