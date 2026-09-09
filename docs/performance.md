# Performance

The `experimental-soft-cheat` branch keeps the same fast layout that made the 480p-neural/native-output path practical: the source stays at its native visible resolution while only the neural branch is capped to **480 pixels high** by default.

For a 2560×1440 source, the neural working image is roughly **854×480** while the visible bridge remains 2560×1440.

## Historical manual results

| Checkpoint | Source | Neural working size | Manual observation |
| --- | ---: | ---: | --- |
| v0.1.0-pre.2 | 2560×1440 | 2560×1440 | ~19 FPS base |
| v0.1.0-pre.3-dev.3 | 2560×1440 | 1920×1080 | ~30 FPS base; ~60 FPS with 2× LSFG |
| dev.4 | 2560×1440 | ~854×480 | ~60 FPS base, frame generation off |
| dev.5-style compositor | native visible output | max 480p neural branch | mainly sharpening/detail separation; distant fog/haze could look weaker |

These are observations from different gameplay sessions, so they are not controlled A/B benchmarks.

## High-frequency compositor

This branch keeps the dev.5-style spatial detail path:

1. keep the captured native frame as the visible base
2. downsample only the neural input to at most 480p
3. run the external neural compatibility runtime
4. sample the neural output with a five-point cross filter
5. subtract that local blur from the neural center sample
6. add the remaining high-frequency detail to the native frame

The visible operation is:

```text
native + (neural - neural_blur) * strength
```

Strength starts at `1.0` and can be raised to `4.0`.

## Why the branch is fast

At 2560×1440, an 854×480 neural texture is about 11% of the native pixel count. The neural network therefore processes far fewer pixels while the user still sees the source application's native geometry and base image.

The compositor is also intentionally simple. It does not keep the dev.6 temporal input-history texture or run the broader neural-delta matching logic.

## Quality boundary

Lossless Scaling provides this bridge with the final color frame. It does not provide the game's engine depth or motion-vector buffers. This branch therefore behaves like a spatial neural detail filter instead of a full in-engine guided Neural Rendering integration.

That tradeoff is deliberate for this branch. It preserves the earlier visual behavior that looked mostly like sharpening and could weaken fog/haze in some scenes.

Raw runtime logs, private configuration and vendor binaries stay outside the repository.
