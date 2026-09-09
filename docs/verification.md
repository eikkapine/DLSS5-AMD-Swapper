# Verification

This page records what has actually been checked for `v0.1.0-pre.3-soft-cheat.3` on the `experimental-soft-cheat` branch.

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

## Visual acceptance status

Manual testing of soft-cheat.3 found that the previous smothering and long-lived transparent motion trails were fixed. Some flicker returned, especially as the image changes, so this checkpoint is preserved as an improvement to motion clarity rather than a final flicker solution.

## Not established

- full equivalence to an in-game DLSS-NR integration with real engine depth/motion vectors
- pixel-identical output versus full-resolution Neural Rendering
- universal compatibility across AMD GPUs, games or protected-content capture paths
- a new controlled FPS benchmark for this branch
- a measured haze/fog reduction amount

## Approved images

The only public gameplay images inherited by this branch are the two previously reviewed files:

- `docs/images/cs2-native-off.png`
- `docs/images/cs2-native-on.png`

No new screenshot was added or changed for this branch.

## Public boundary

The public branch excludes paid Lossless Scaling files, `Lossless_original.dll`, AMD proxy/runtime binaries, NVIDIA DLLs/models/weights, private INIs, raw logs, backups, secrets and machine-specific development files.
