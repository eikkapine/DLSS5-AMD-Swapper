# Verification

This page is the line between what I have actually checked and what still needs work.

## Verified

- The bridge can capture a normal visible source window through Windows Graphics Capture.
- The neural runtime can produce non-black output on the tested RX 9070 XT path.
- The bridge can show original, neural, and blended output.
- `Ctrl+Alt+F6` toggles original/processed output live.
- `Ctrl+Alt+F7` and `Ctrl+Alt+F8` change the live blend in `0.1` steps.
- The auto-scale wrapper can start the bridge, wait for readiness, and activate Lossless Scaling through the configured scaling shortcut path.
- Unscale can stop the bridge cleanly in the exercised app integration path.
- Native bridge capture at 2560×1440 can preserve the source frame exactly before neural processing (`max error = 0`).
- The approved native comparison shows a real neural image difference with no geometry resize in the crop.
- The `v0.1.0-pre.2` production binaries match the hashes recorded in `RELEASE.json`.
- The public release/package boundary excludes paid Lossless Scaling files and external vendor/runtime files.

## Partially verified

### Automatic Scale workflow

Automatic activation through the configured Lossless Scaling scaling shortcut has been exercised. Direct mouse-click testing of every current Scale-button/profile combination has not been completed for the published checkpoint.

### Native mode

Native-resolution bridge behavior has been checked independently, and the auto-scale proxy contains the native 1:1 forwarding path. The final native path through every real Lossless Scaling UI/profile combination has not been exhaustively exercised.

### GPU transport

The shared GPU path was observed in the native performance checkpoint. This does not establish pixel equivalence on every AMD driver/GPU combination.

## Not established

- 60 FPS native neural rendering
- broad AMD GPU compatibility outside the hardware I have tested
- competitive-game latency suitability
- compatibility with every protected-content/game capture scenario
- independently measured LSFG/display FPS for the quoted 2× multiplier
- legal permission for every possible third-party runtime use case

## Approved image comparison

The only public gameplay images are:

- `docs/images/cs2-native-off.png`
- `docs/images/cs2-native-on.png`

They are matching 640×750 crops from the same frozen 2560×1440 frame. The crop was not resized, sharpened, color-adjusted, or fabricated. PNG metadata was cleared and the crop excludes account details, usernames, chat, and desktop/browser UI.

The full private captures are intentionally not part of GitHub.

## Reading the numbers correctly

I keep these measurements separate:

- game/source FPS
- changed RGB submissions
- bridge presentation rate
- neural job duration
- HIP waits
- LSFG output
- displayed FPS

One cannot be substituted for another. A faster presentation loop does not prove faster neural inference, and multiplying a base rate by a frame-generation setting is not the same as measuring displayed FPS.

See [Performance](performance.md) for the current numbers and [Licensing](licensing.md) for the release boundary.
