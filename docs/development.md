# Development

The main goal is straightforward: improve native-resolution throughput without reducing image quality.

For performance work I keep these fixed unless I am explicitly testing a separate quality tradeoff:

- native application dimensions when native mode is selected
- the same model and weights
- the same neural precision/settings
- full effect strength
- asynchronous inference (`Inline=0`)
- the current Lossless Scaling profile and frame-generation choice

## Current direction

The biggest work so far has been outside the neural model itself:

- shared GPU transport between capture/presentation D3D11 and neural D3D12 devices
- removal of unnecessary full-frame CPU readbacks
- exact duplicate-output suppression
- lower polling overhead
- inference-aware feed scheduling
- bounded HIP host timing to separate waiting from useful work

These changes are intended to remove host/transport overhead while keeping the input pixels and neural path intact.

## Published checkpoint

`v0.1.0-pre.2` is the latest public performance checkpoint. The manually exercised RX 9070 XT native 2560×1440 run recorded 1,112 changed RGB submissions in 60.238 seconds (`18.46/s`) and I observed roughly 19 FPS base output.

That number is useful as a baseline, but it is not a controlled GPU benchmark. Bridge submission rate, neural-job timing, game FPS, LSFG output, and displayed FPS are different measurements and I keep them separate.

## What I avoid during optimization

I do not count lower processing resolution, reduced effect strength, lower neural precision, model replacement, or approximate pixel matching as performance wins for the native-quality target.

I also keep runtime/vendor binaries, private settings, local logs, raw captures, backups, and machine-specific files out of Git.

## Measurement files

Sanitized checkpoint data is kept under `docs/measurements/`. Release metadata in `RELEASE.json` records the binary hashes and which checks apply to that release.

The private raw logs used to generate these summaries stay outside the repository.

## Release rule

I only describe a behavior as verified when I have a matching test/run for that exact path. A bridge-only result does not automatically prove the complete Lossless Scaling workflow, and a nominal frame-generation multiplier is not reported as measured display FPS.

See [Performance](performance.md) and [Verification](verification.md) for the current public baseline.
