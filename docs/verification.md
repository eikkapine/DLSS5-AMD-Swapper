# Verification

This page records the checks behind **v0.1.0-pre.3-dev.15** and keeps unsupported claims out of the release.

## Lossless Scaling path

The main branch retains the accepted dev.14 compositor and automatic Scale workflow:

- visible output stays at the captured source resolution while `NeuralMaxHeight=480` caps only the neural branch
- the current native source remains the visible base
- broad unstable residual color/luminance is filtered
- stale chroma and correction magnitude are reduced as source motion rises
- accepted correction history is reused only where the source is effectively unchanged
- duplicate visible submissions remain independent from neural completion cadence
- `Ctrl+Alt+F6` toggles the effect
- `Ctrl+Alt+F7` decreases strength
- `Ctrl+Alt+F8` increases strength up to `4.0` in native neural-residual mode

No new screenshot was added for dev.15. The only public gameplay images remain the two previously reviewed CS2 crops:

- `docs/images/cs2-native-off.png`
- `docs/images/cs2-native-on.png`

## Direct-game AMD path

The direct-game helper was exercised against a locally installed x64 DX12/FidelityFX title using the official `DLSS-NR-on-AMD` v0.2.17 setup.

The live runtime log showed:

- FidelityFX upscaler/loader dispatch interception
- FSR color, motion, and depth staging
- inverted-Z depth detection
- zero-copy input and output interop
- inline same-frame mode
- completed Neural Rendering jobs
- HIP selecting the RX 9070 XT (`gfx1201`)
- no logged fault/GPU-error markers in the sampled run

The same run produced a private PresentMon capture and hash-backed analyzer JSON. It was a startup/runtime smoke validation at full-resolution FSR input rather than a controlled A/B benchmark, so it is not promoted as an optimized-performance result.

The managed remove path deleted the files it could prove were created by the managed install. A post-install runtime log and manifest were intentionally preserved when their hashes no longer matched the install-time manifest; the public repository/package never contains those private files.

## Reference-image comparison

`bridge/tests/Compare-DlssReference.py` compares static reference pairs without publishing the source images. It always emits unaligned metrics and only emits aligned metrics when registration confidence is high.

The supplied references confirmed that the current color-only Lossless Scaling bridge explains only a small fraction of the large appearance change seen in full DLSS 5-style examples. That result is consistent with the architectural limitation: the desktop bridge lacks true game motion/depth/jitter/exposure and render-resolution color inputs.

## Performance evidence

Public performance values must come from hashed logs. `tools/Capture-Performance.ps1` records PresentMon data and `bridge/scripts/Analyze-Run.py` keeps game/display timing separate from bridge cadence and HIP/runtime timing.

`tools/Check-Publication.py` rejects concrete FPS numbers in prose and measurement JSON that lacks source-hash/schema provenance.

## Not established

- universal compatibility across AMD GPUs or games
- pixel-identical parity with a native NVIDIA DLSS 5 implementation
- a controlled direct-game A/B performance benchmark for dev.15
- competitive-game latency suitability for the injected direct-game route
- permission to redistribute third-party runtime binaries outside their own licenses

## Public release boundary

The public repository and ZIP exclude paid Lossless Scaling files, `Lossless_original.dll`, third-party AMD proxy/runtime binaries or installers, NVIDIA DLLs/models/weights, raw logs, private INIs/manifests, backups, captures, secrets, and machine-specific development files.
