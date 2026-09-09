# Verification

This page separates what I have actually exercised from what is still unknown.

## Verified on the current dev.3 checkpoint

- The bridge version in the manual run was `0.1.0-pre.3-dev.3`.
- The source was 2560×1440 and the neural working size was 1920×1080 with `WorkingScale=0.75`.
- The cadence log reported `transport=gpu_shared`.
- Direct shader-capable WGC capture was used; the tested run did not fall back to copied capture surfaces.
- GPU resize was active for the reduced neural path.
- Bounded HIP kernel sampling was off during the performance run.
- The neural effect stayed enabled at full strength in the captured cadence rows.
- The selected full-effect non-FG window recorded 1,417 changed RGB submissions in 49.127 seconds, about 28.84/s.
- I observed about 30 FPS base output in the exercised setup.
- In a separate session with Lossless Scaling 2× frame generation enabled, I observed about 60 FPS output.
- The production dev.3 bridge and wrapper hashes match the locally installed binaries used for the checkpoint.

The bridge does not count generated LSFG frames. The 60 FPS figure is therefore a manual Lossless Scaling observation, while the ~28.84/s figure comes from the selected full-effect bridge cadence window.

## Also verified from earlier checkpoints

- The bridge can capture a normal visible source window through Windows Graphics Capture.
- The neural runtime can produce non-black output on the tested RX 9070 XT path.
- The bridge can show original, neural and blended output.
- `Ctrl+Alt+F6` toggles original/processed output live.
- `Ctrl+Alt+F7` and `Ctrl+Alt+F8` change the live blend in `0.1` steps.
- The auto-scale wrapper can start the bridge, wait for readiness and activate Lossless Scaling.
- Unscale can stop the bridge cleanly in the exercised integration path.
- Native bridge capture at 2560×1440 can preserve the source frame exactly before neural processing (`max error = 0`).
- The approved native comparison shows a real neural image difference with no geometry resize in that frozen comparison.
- The public package boundary excludes paid Lossless Scaling files and external vendor/runtime files.

## Not established

- 60 FPS **base** Neural Rendering on the RX 9070 XT
- pixel-identical quality between 0.75 working scale and native NR
- broad AMD GPU compatibility outside the hardware tested so far
- competitive-game latency suitability
- compatibility with every protected-content/game capture scenario
- legal permission for every possible third-party runtime use case

## Approved image comparison

The only public gameplay images are:

- `docs/images/cs2-native-off.png`
- `docs/images/cs2-native-on.png`

They are matching 640×750 crops from the same frozen 2560×1440 native-mode frame. The crop was not resized, sharpened, color-adjusted or fabricated. PNG metadata was cleared and the crop excludes account details, usernames, chat and desktop/browser UI.

No new screenshots are added by the dev.3 publication.

## Reading the numbers correctly

I keep these measurements separate:

- game/source FPS
- changed RGB submissions
- bridge presentation rate
- neural job duration
- HIP waits
- LSFG output
- displayed FPS

See [Performance](performance.md) for the current numbers and [Licensing](licensing.md) for the release boundary.
