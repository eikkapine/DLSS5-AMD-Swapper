# Performance

Performance is the main unfinished part of NR Auto Scale. I am trying to improve throughput without reducing processing resolution, neural strength, model/weights, or precision.

## Current native checkpoint

The published `v0.1.0-pre.2` run on my RX 9070 XT used native **2560×1440** processing at full effect strength.

| Measurement | Result |
| --- | ---: |
| Changed RGB submissions | 1,112 |
| Recorded interval | 60.238 s |
| Changed RGB submissions / s | 18.46 |
| Observed base output | about 19 FPS |
| Nominal 2× FG multiplier | about 38 FPS |

The last line is just `19 × 2`. The bridge did not independently measure LSFG/display FPS, so I do not present it as measured output.

## What improved

The current checkpoint includes work aimed at removing host-side overhead:

- shared GPU transport for the native path
- exact duplicate-output suppression
- reduced redundant GPU feed/check work
- direct full-strength neural presentation where possible
- bounded HIP host timing
- inference-aware feed pacing based on observed worker waits

In selected intervals, changed RGB submissions moved from about `17.53/s` to `18.69/s` while GPU feed/check iterations dropped from roughly `105.49/s` to `26.08/s`. That shows less redundant work, but it is not a 4× neural speedup. HIP device waits remained around 52 ms in the sampled data.

## Historical fixed-size tests

Earlier bridge-only work used smaller fixed processing sizes to validate the path and separate host overhead from neural cost. The current fresh-install default is 1280×720, while native mode stays available for the quality target.

Those smaller-resolution numbers should not be compared directly with the native 1440p checkpoint as if they were the same workload.

## Image-path checks

In the approved frozen 2560×1440 CS2 comparison:

- captured input and the original frame sent into processing matched with max error `0`
- neural output was non-black
- mean absolute RGB difference from original was about `2.87/255`
- maximum channel difference was `55/255`
- about 97.7% of pixels changed
- sampled neural jobs settled around `63 ms/job`

That job time is runtime/bridge timing, not complete input-to-display latency or game FPS.

## Where the time appears to be going

The host side now does substantially less repeated work than the earliest builds. The remaining measurements point more strongly at neural GPU execution, synchronization, and runtime scheduling as the next areas to understand.

I do not treat repeated presentations, lower resolution, reduced effect strength, lower precision, or approximate image matching as valid performance gains for the native-quality target.

## Measurement files

Sanitized records for the published checkpoint are under [`docs/measurements/`](measurements/). `RELEASE.json` records the release artifact hashes and the exact verification flags attached to that checkpoint.

See [Development](development.md) for the optimization rules and [Verification](verification.md) for the practical limits of these numbers.
