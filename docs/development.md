# Development

The project currently has three useful processing paths:

1. **native visible output + capped neural branch** using `NeuralMaxHeight` — current default
2. **legacy reduced source-relative processing** using `WorkingScale`
3. **full 1:1 neural processing** for quality/reference work

The current public checkpoint, **v0.1.0-pre.3-dev.6**, focuses on the first path. The captured source stays at its native visible resolution while only the neural branch is capped at 480 pixels high. On a 2560×1440 source that means about 854×480 for Neural Rendering and 2560×1440 for the final bridge image.

Dev.6 keeps the native source as the image base and applies a temporally matched neural correction over it. This replaces the dev.5 high-frequency-only compositor that made the result look mostly like sharpening.

## Invariants for normal performance work

- keep the same neural model/weights and precision/settings
- keep the normal baseline effect at `1.0`
- keep `Inline=0` for the current asynchronous Lossless Scaling path
- preserve GPU ownership/fence ordering and failure-drain lifetime rules
- keep the visible source at native resolution in `NeuralMaxHeight` mode
- keep paid/vendor/private runtime files outside Git

The F7/F8 strength control can amplify the already-produced neural correction up to `4.0`; it does not change the model or neural inference settings.

## Current optimization direction

The work so far has reduced host-side overhead with shared GPU transport, duplicate suppression, inference-aware feeding, direct WGC SRV use and output/history copy reduction. The biggest practical gain then came from reducing only the neural pixel count while preserving the native visible source.

The remaining quality/performance work is mainly about:

- retaining more useful neural reconstruction without showing the low-resolution neural frame as the base image
- improving temporal alignment without real engine motion/depth buffers
- reducing composition and synchronization overhead
- investigating upstream/runtime kernel improvements when they can be applied without changing model quality

A Lossless Scaling screen-capture bridge does not have the same guide buffers as an in-game FSR hook. The project therefore treats full guided temporal parity as a separate problem instead of pretending those inputs exist.

## Validation discipline

I keep these observations separate:

- user-observed base FPS
- user-observed LSFG/display output
- changed RGB submissions
- bridge feed/presentation rate
- HIP waits and runtime timing
- manual image-quality acceptance

Raw logs remain private. Before publishing a checkpoint I build the production bridge/wrapper, verify hashes, inspect the staged files, create the allowlisted ZIP, and confirm it contains no paid Lossless Scaling file, external vendor runtime, private config/log or unreviewed screenshot.
