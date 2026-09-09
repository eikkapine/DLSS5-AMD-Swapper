# Verification

This page records what I actually exercised for **v0.1.0-pre.3-dev.14** and keeps unverified claims out of the release.

## Verified for v0.1.0-pre.3-dev.14

- The production bridge and proxy builds completed successfully.
- `BridgePixelTests` passed, including blend bytes, SIMD tails, channel order, alpha, bypass, native identity and black-guard cases.
- The auto-scale harness passed on a fresh rerun, including setup defaults/custom/native cases.
- The installed `DlssNrBridge.exe` matched the final dev.14 build by SHA-256.
- Fresh setup defaults remain `WorkingScale=0` and `NeuralMaxHeight=480`.
- Automatic activation still forwards the source into the bridge when Lossless Scaling scales it.
- `Ctrl+Alt+F6` toggles the effect; F7/F8 change strength; native residual mode supports up to `4.0`.
- A 2560×1440 source used an approximately 854×480 neural feed while the visible output stayed native-sized.
- The AMD compatibility runtime completed real neural jobs on the RX 9070 XT and reported the color-only path as asynchronous, with no motion/depth/exposure inputs and runtime history off.
- The standalone dev.14 probe kept high-rate visible submissions instead of regressing to capture-rate duplicate suppression. Bridge cadence is diagnostic and is not a game-FPS measurement.
- After installing dev.14, I manually confirmed that stationary flicker was fixed, flicker during movement was barely noticeable, and performance still felt good.
- No new screenshots were added.

## Performance evidence

Earlier gameplay sessions established the large performance gain from capping only the neural branch at 480p while keeping native visible output. I did not record a fresh numeric game-FPS value for dev.14, so this release makes no new FPS claim from the standalone bridge cadence.

## Not established

- full equivalence to an in-game DLSS-NR integration with real engine depth/motion vectors
- pixel-identical output versus full-resolution Neural Rendering
- universal compatibility across AMD GPUs, games or protected-content capture paths
- competitive-game latency suitability
- a fresh measured dev.14 game-FPS value
- legal permission to redistribute every possible third-party runtime component

## Approved images

The only public gameplay images remain the previously reviewed files:

- `docs/images/cs2-native-off.png`
- `docs/images/cs2-native-on.png`

They are historical native-mode comparison crops. No dev.14 screenshot was added or changed.

## Release boundary

The public repo and ZIP exclude paid Lossless Scaling files, `Lossless_original.dll`, AMD proxy/runtime binaries, NVIDIA DLLs/models/weights, private INIs, raw logs, backups and machine-specific development files.
