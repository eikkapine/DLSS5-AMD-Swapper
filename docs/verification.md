# Verification

This page records what has actually been checked for the `experimental-soft-cheat` branch.

## Verified locally

- `VERSION` is `0.1.0-pre.3-soft-cheat.1`.
- The Release bridge build completes successfully.
- The Release proxy build completes successfully.
- The existing bridge pixel tests pass.
- The full auto-scale harness and setup/installer test suite pass.
- The GPU compositor uses the recovered dev.5 five-sample high-frequency filter.
- The CPU fallback uses the matching high-frequency filter.
- `Ctrl+Alt+F6` toggles the effect.
- `Ctrl+Alt+F7` decreases strength.
- `Ctrl+Alt+F8` increases strength up to `4.0` in the native high-frequency mode.
- `NeuralMaxHeight=480` keeps the visible output at source size while capping only the neural branch.
- The branch does not add any new screenshots.

## Manual visual evidence behind this branch

The earlier manually exercised dev.5 behavior was perceived mainly as sharpening and stronger distant-detail separation. In some scenes, distant fog or haze appeared reduced. That is the behavior this branch is intended to preserve.

The reconstructed source uses the exact recovered dev.5 implementation edits. The newly compiled executable is not claimed to be byte-identical to the previously preserved dev.5 binary.

## Not established

- full equivalence to an in-game DLSS-NR integration with real engine depth/motion vectors
- pixel-identical output versus full-resolution Neural Rendering
- universal compatibility across AMD GPUs, games or protected-content capture paths
- a new controlled FPS benchmark for this branch
- permission from every game/service to use external post-processing in competitive play

## Approved images

The only public gameplay images inherited by this branch are the two previously reviewed files:

- `docs/images/cs2-native-off.png`
- `docs/images/cs2-native-on.png`

No new screenshot was added or changed for this branch.

## Public boundary

The public branch excludes paid Lossless Scaling files, `Lossless_original.dll`, AMD proxy/runtime binaries, NVIDIA DLLs/models/weights, private INIs, raw logs, backups, secrets and machine-specific development files.
