# Verification

This page separates the exercised dev.6 behavior from things the project does not claim.

## Verified for v0.1.0-pre.3-dev.6

- The production bridge and proxy builds completed successfully before publication.
- The installed `DlssNrBridge.exe` exactly matched the final dev.6 build by SHA-256.
- The installed project proxy `Lossless.dll` exactly matched its production build by SHA-256.
- Fresh setup defaults are `WorkingScale=0` and `NeuralMaxHeight=480`.
- The automatic proxy forwards `--neural-max-height 480` when Lossless Scaling activates the bridge.
- `Ctrl+Alt+F6` toggles processed output.
- `Ctrl+Alt+F7` decreases strength.
- `Ctrl+Alt+F8` increases strength, with native neural-delta mode supporting up to `4.0`.
- Existing runtime evidence showed the 2560×1440 source using an approximately 854×480 neural feed with native-sized visible output.
- Existing runtime evidence also showed real completed neural jobs while the color-only path had no motion/depth guide buffers.
- After the dev.6 temporally matched neural-delta compositor was installed, the user manually confirmed that it now works as intended visually through Lossless Scaling.
- No new screenshots were added for this release.

## Performance evidence

The earlier dev.4 version of the same native-visible/480p-neural layout was manually reported at about 60 FPS without frame generation in World of Tanks. Dev.6 keeps the same neural-resolution cap but changes the lightweight compositor.

The acceptance message for dev.6 did not include a fresh numeric FPS value, so this release does **not** claim a newly measured 60 FPS dev.6 benchmark.

## Not established

- full equivalence to an in-game DLSS-NR integration with real engine depth/motion vectors
- pixel-identical output versus full-resolution Neural Rendering
- universal compatibility across AMD GPUs, games or protected-content capture paths
- competitive-game latency suitability
- a fresh measured dev.6 FPS value
- legal permission to redistribute every possible third-party runtime component

## Approved images

The only public gameplay images remain the previously reviewed files:

- `docs/images/cs2-native-off.png`
- `docs/images/cs2-native-on.png`

They are historical native-mode comparison crops. No dev.6 screenshot was added or changed.

## Release boundary

The public repo and ZIP exclude paid Lossless Scaling files, `Lossless_original.dll`, AMD proxy/runtime binaries, NVIDIA DLLs/models/weights, private INIs, raw logs, backups and machine-specific development files.
