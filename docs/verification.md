# Verification

This page records what has actually been checked for `v0.1.0-pre.3-soft-cheat.4` on the `experimental-soft-cheat` branch.

## Verified locally

- The Release bridge build completes successfully with HIP 7 timing/pacing support enabled.
- The Release proxy build completes successfully.
- The bridge pixel tests pass.
- The full auto-scale harness and setup/installer suite pass on a clean rerun.
- The branch contains the accepted dev.14 GPU transport, pacing, motion rejection, exact-static correction reuse and high-rate visible-presentation changes.
- The experimental GPU compositor applies a uniform clarity/dehaze-style filter over the whole frame.
- `Ctrl+Alt+F6` toggles the effect.
- `Ctrl+Alt+F7` decreases strength.
- `Ctrl+Alt+F8` increases strength up to `4.0` in native neural mode.
- `NeuralMaxHeight=480` keeps visible output at source size while capping only the neural branch.
- No new screenshot was added or changed.

The direct-game helper was also exercised against a locally installed x64 DX12/FidelityFX title with the official `DLSS-NR-on-AMD` v0.2.17 setup. The live runtime log showed FidelityFX dispatch interception, full color/motion/depth staging, zero-copy input/output interop and completed Neural Rendering jobs with no logged fault/GPU-error markers in the sampled run. A PresentMon capture and the runtime log were hashed and sanitized with `Analyze-Run.py`; raw logs remain private.

## Visual status

At the current checkpoint, the previous smothering and long-lived transparent motion trails are no longer present in the accepted branch state. Some flicker remains as the image changes, so this checkpoint is preserved for its motion clarity rather than treated as the final temporal solution.

## Not established

- full equivalence to an in-game DLSS-NR integration with real engine depth/motion vectors
- pixel-identical output versus full-resolution Neural Rendering
- universal compatibility across AMD GPUs, games or protected-content capture paths
- a controlled direct-game A/B performance benchmark; the current PresentMon capture is a runtime smoke measurement rather than a like-for-like benchmark
- a measured haze/fog reduction amount

## Approved images

The only public gameplay images inherited by this branch are the two previously reviewed files:

- `docs/images/cs2-native-off.png`
- `docs/images/cs2-native-on.png`

No new screenshot was added or changed for this branch.

## Public boundary

The public branch excludes paid Lossless Scaling files, `Lossless_original.dll`, AMD proxy/runtime binaries, NVIDIA DLLs/models/weights, private INIs, raw logs, backups, secrets and machine-specific development files.
