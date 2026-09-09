# Performance

The biggest performance change so far is **v0.1.0-pre.3-dev.3**. It moves the expensive Neural Rendering work to a smaller source-relative working resolution and leaves the final enlargement to Lossless Scaling.

## Current RX 9070 XT checkpoint

The exercised setup used a **2560×1440 source** with `WorkingScale=0.75`, giving the neural runtime a **1920×1080** working image.

| Measurement | Result |
| --- | ---: |
| Source resolution | 2560×1440 |
| Neural working resolution | 1920×1080 |
| Relative neural pixel count | 56.25% of native |
| Effect strength | 1.0 / full |
| Transport | shared GPU |
| GPU-resized captures | active |
| Kernel sampling | off |
| User-observed base output | ~30 FPS |
| User-observed output with Lossless Scaling 2× FG | ~60 FPS |
| Selected non-FG changed RGB submissions | 1,417 |
| Selected non-FG interval | 49.127 s |
| Selected non-FG changed RGB submissions / s | ~28.84 |

The separate 2× frame-generation session used the same 2560×1440 → 1920×1080 neural path. The bridge does not count generated LSFG frames, so the 60 FPS value is recorded as the observed Lossless Scaling output from the manual test.

## Compared with the previous public checkpoint

The previous `v0.1.0-pre.2` run processed the full **2560×1440** image through Neural Rendering and produced about **19 FPS** base output, with 18.46 changed RGB submissions/s over the complete recording.

| Checkpoint | Neural size | Base FPS | Changed RGB/s |
| --- | ---: | ---: | ---: |
| v0.1.0-pre.2 | 2560×1440 | ~19 | 18.46 |
| v0.1.0-pre.3-dev.3 | 1920×1080 | ~30 | ~28.84 |

The base-output improvement is roughly 58%, and the selected full-effect changed-RGB cadence is roughly 56% higher than pre.2's complete-run 18.46/s figure. This is a deliberate workload change, not a same-resolution micro-optimization: dev.3 evaluates only 56.25% as many neural pixels before the final upscale.

## What changed

- Added `WorkingScale` as a source-relative neural-resolution control.
- New installs use `WorkingScale=0.75`.
- Generalized the shared GPU transport so reduced/fixed neural sizes do not fall back to full-frame CPU capture/readback.
- Kept WGC capture at the source resolution.
- Added a GPU bilinear resize into the shared neural input texture when the dimensions differ.
- Kept the exact texel-load path for 1:1 transport.
- Retained the two-slot accepted-history path and direct shader-capable WGC capture from the previous local candidate.
- Disabled bounded HIP kernel sampling by default; it remains available with `--hip-kernel-timing`.

The neural model, weights, precision/settings and full effect strength are unchanged.

## Why this helps

The previous measurements already showed that host submission overhead was small while the neural GPU wait stayed around 52 ms at native 1440p. Recent NVIDIA-side DLSS 5 work and AMD community tests both point to neural pixel count as the largest practical lever available without rewriting the private neural kernels.

At 0.75 scale, each axis is 75% of the source, so the neural pixel count is:

```text
0.75 × 0.75 = 0.5625
```

That means about 43.75% fewer pixels go through the neural pass. Fixed capture, presentation and upscaling costs still remain, so total FPS does not scale perfectly with pixel count.

## Quality tradeoff

This mode does change the neural working resolution. It keeps the same model and full effect strength, but it should not be described as pixel-identical to native NR. In the manual dev.3 test the result was good enough to keep using, while the performance improvement was large enough to reach about 30 FPS base / 60 FPS with 2× frame generation.

The older approved before/after screenshots were captured in native 1:1 mode and are kept only as evidence that the neural effect is visible. They are not presented as a dev.3 quality comparison.

## Measurement files

Sanitized records are under [`docs/measurements/`](measurements/). Raw runtime and cadence logs stay private. `RELEASE.json` records the exact tested binary hashes and verification limits for the public checkpoint.

See [the pre-upscale design note](neural-upstream-performance.md) for the architecture and research references, and [Verification](verification.md) for what has actually been exercised.
