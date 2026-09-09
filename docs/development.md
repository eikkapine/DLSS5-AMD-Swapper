# Development

NR Auto Scale now has two integration targets:

1. **direct-game AMD Neural Rendering** for supported x64 DX12/FSR games
2. **Lossless Scaling compatibility bridge** for arbitrary capturable windows

The current main checkpoint, **v0.1.0-pre.3-dev.15**, keeps the accepted dev.14 Lossless Scaling compositor and adds the verified direct-game route plus log-backed performance/provenance tooling.

## Lossless Scaling modes

The bridge keeps three processing modes:

1. **native visible output + capped neural branch** using `NeuralMaxHeight` — current default
2. **legacy reduced source-relative processing** using `WorkingScale`
3. **full 1:1 neural processing** for quality/reference work

With `NeuralMaxHeight=480`, a 2560×1440 source stays 2560×1440 for visible output while Neural Rendering works at about 854×480. Dev.14's compositor remains the stable base: broad residual color/exposure swings are filtered, stale chroma is reduced during motion, and accepted correction history is reused only where the source is effectively unchanged.

## Direct-game route

`direct-game/amd_dlss5.py` is intentionally an installer/orchestration layer rather than a redistributed compatibility runtime. It verifies the user-supplied upstream setup file, checks the target for x64/FSR/DX12 evidence, blocks common anti-cheat markers, invokes the unchanged upstream installer, verifies the generated rich temporal configuration, and records a hash-safe local manifest for remove/update/rollback.

The direct route is the quality-focused path because it can consume game/upscaler color, motion, depth, jitter/exposure context and render-resolution dimensions that are unavailable after the game has already produced a final desktop frame.

## Invariants

- keep the same neural model/weights and precision/settings unless a change is explicitly being evaluated
- keep the normal baseline effect at `1.0`
- keep `Inline=0` for the asynchronous Lossless Scaling path
- preserve GPU ownership/fence ordering and failure-drain lifetime rules
- keep the visible source at native resolution in `NeuralMaxHeight` mode
- keep vendor, paid, private, and machine-specific runtime files outside Git
- never turn bridge cadence into a game/display FPS claim
- never publish a performance number unless it comes from a hashed source log

The F7/F8 strength control can amplify the already-produced Lossless Scaling correction up to `4.0`; it does not change the neural model or inference settings.

## Performance work

The Lossless Scaling path already uses shared GPU transport, direct WGC SRV access where available, inference-aware pacing, duplicate visible presents, and a low-resolution neural branch over a native-resolution source base. Future work should preserve the accepted motion behavior while reducing synchronization/composition overhead or improving the quality of the stable neural residual.

The direct-game route should keep the final output at the game's native/display target while allowing the game's FSR quality mode to choose a lower render-resolution input for the neural pass. Runtime diagnostics must confirm the actual FSR color/motion/depth dimensions before treating that optimization as active.

## Validation discipline

I keep each metric tied to the subsystem that produced it:

- PresentMon game/display frame timing
- changed RGB submissions
- bridge feed/presentation cadence
- HIP/runtime timing
- neural runtime fault/error state
- visual acceptance

`tools/Capture-Performance.ps1` records PresentMon data and `bridge/scripts/Analyze-Run.py` produces sanitized JSON with SHA-256 hashes of every raw source. Raw logs remain private.

Before publishing a checkpoint I build the production bridge/proxy, run the relevant tests, verify release hashes, run `py tools/Check-Publication.py`, inspect staged files, create the allowlisted ZIP, and confirm it contains no paid Lossless Scaling file, third-party runtime/installer, private config/log, personal file, or unreviewed screenshot.
