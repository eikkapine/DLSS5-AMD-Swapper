# Performance

The current checkpoint is **v0.1.0-pre.3-dev.6**. Its main performance choice is to keep the visible frame at the captured application's native resolution while limiting only the expensive neural branch to **480 pixels high**.

For a 2560×1440 source, the bridge therefore uses roughly **854×480** for Neural Rendering and composites the resulting neural correction back onto the untouched 2560×1440 source.

## Performance history

| Checkpoint | Source | Neural working size | Manual observation |
| --- | ---: | ---: | --- |
| v0.1.0-pre.2 | 2560×1440 | 2560×1440 | ~19 FPS base |
| v0.1.0-pre.3-dev.3 | 2560×1440 | 1920×1080 | ~30 FPS base; ~60 FPS with 2× LSFG |
| dev.4 | 2560×1440 | ~854×480 | ~60 FPS base, frame generation off |
| **v0.1.0-pre.3-dev.6** | native visible output | max 480p neural branch | visual behavior manually accepted; no fresh numeric FPS recorded |

These are user-observed gameplay results from different sessions, not controlled A/B benchmarks. The bridge does not count generated LSFG frames.

## What dev.6 changes

Dev.4/dev.5 already established the fast native-output/480p-neural layout. Dev.5 then made the effect adjustable up to 4×, but its compositor deliberately kept only a high-frequency residual from the neural image. That made the result look mostly like sharpening.

Dev.6 replaces that with a temporally matched neural-delta compositor:

1. keep the current native source for presentation
2. downsample only the neural input to at most 480p
3. retain the previous low-resolution neural input
4. compare the asynchronous neural output against current/prior input
5. use the better match to estimate `neural output - neural input`
6. attenuate stale broad corrections where the scene changed strongly
7. add the matched neural correction to the untouched native frame

The compositor uses three filtered low-resolution samples per visible pixel. Dev.5 used five neural samples for its high-pass filter. Dev.6 also adds one small neural-resolution history copy per captured frame.

## Why 480p helps

At a 2560×1440 source, 854×480 is about 11% of the native pixel count. The neural network therefore processes far fewer pixels while the user still sees the original native geometry/base image.

This is different from rendering the whole visible output at 480p and upscaling it. Only the neural branch is reduced.

## Quality boundary

The Lossless Scaling bridge receives a final color frame, not the game's engine depth and motion-vector buffers. The AMD DLSS-NR compatibility runtime can use those guide buffers when hooked to a real FSR pipeline, but this color-only bridge cannot recreate them.

Dev.6 therefore uses lightweight temporal color matching. It improves the amount of actual neural reconstruction retained compared with dev.5, but it does not claim to equal a native in-game guided DLSS-NR integration.

## Runtime settings kept unchanged

The accepted dev.6 path keeps the same model/weights, neural precision/settings, `Inline=0`, `Interop=1`, and full `1.0` baseline effect strength. `Ctrl+Alt+F8` can boost the visible neural correction above the baseline up to `4.0` without changing the neural model itself.

Raw runtime logs remain private. Public release metadata records only sanitized version/build information and exact artifact hashes.
