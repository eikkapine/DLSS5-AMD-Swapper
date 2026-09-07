# Performance progress and manual-run ledger

## Current checkpoint: v0.1.0-pre.2

Published checkpoint of the manually exercised inference-aware feed build on
7 September 2026. The user reports **19 FPS**, up from approximately **18 FPS**.
With the selected 2x frame-generation multiplier, the nominal arithmetic is
**38 output FPS**. That is not an independently measured LSFG/display rate and
does not mean 38 fresh neural evaluations per second. The target remains at
least 60 FPS with native resolution and the accepted image quality preserved.

The released bridge executable is the tested build:

```text
d902dc89c6977a9012028844da6acd788a2386e2c1bb43da71fa805cd230a85f
```

The AMD runtime used in these runs is v0.2.15, build f47996ae. It is supplied
separately by the user. Release artifacts contain only project-built binaries,
scripts and documentation; [RELEASE.json](../RELEASE.json) identifies them.

## Recorded evidence

Both recordings use the RX 9070 XT at 2560x1440 with GPU-shared transport.
The selected comparison uses only whole cadence intervals contained between
20 and 60 seconds after visible output begins. HIP intervals are independently
selected between 20 and 60 seconds after the HIP observer starts: the two
clocks do not have the same origin.

| Measurement | Earlier host-timing build | v0.1.0-pre.2 |
| --- | ---: | ---: |
| User-reported base FPS | about 18 | about 19 |
| Selected cadence duration | 39.244 s | 39.104 s |
| Changed RGB submissions in those intervals | 688 | 731 |
| Changed RGB submissions/s | 17.53 | 18.69 |
| GPU feed/check iterations/s | 105.49 | 26.08 |
| Mean observed HIP device-wait call | 52.12 ms | 51.60 ms |
| Launch API wall time per observed device wait | 0.157 ms | 0.160 ms |
| Observed launches per device wait | 156 | 156 |

Source-hashed, machine-readable summaries:
[earlier run](measurements/20260907-host-timing-baseline.json) and
[current run](measurements/20260907-native-pacing.json).

The current complete cadence log records **1,112 changed submissions over
60.238 seconds**, or **18.46/s** across the entire recording, including startup
variation. It reports 1,541 image checks, 429 duplicate submissions suppressed,
1,111 completion-hint feeds, 431 publication rechecks and zero keepalive feeds.
The header confirms worker-wait hints are active. The observed runtime samples
after the first three jobs are 49-57 ms. Neither selected log reports an
occluded submission; the parsed runtime has no `FAULT:` or `job ... GPU errors:`
lines. This is not a comprehensive stability certification.

The previous run's late intervals vary substantially and its last row is not a
shutdown row. Its full-run average is therefore not used as the before value.
Neither old cadence schema records per-row effect strength or LSFG output.
The two trials are not controlled A/B measurements: scene, focus, GPU load and
hotkey changes can differ. The approximately 6.6% change in the selected RGB
cadence and 75.3% reduction in feed iterations are **observations**, not an
isolated proof of how much speed the code change caused. The user's rounded
18-to-19 report is approximately 5.6% and is recorded separately.

## Implemented work

The published sources include native capture and 1:1 forwarding across live
Lossless Scaling profile changes, reusable CPU buffers for compatibility,
same-adapter D3D11/D3D12 shared textures and fences, exact RGB duplicate checks,
direct full-strength output, bounded HIP host timing, and inference-aware feed
scheduling. The last optimization avoids repeated GPU copies/comparisons while
the worker is busy, then resumes through the normal fenced output path using
the latest available capture.

Native dimensions, model and weights, neural precision/settings, full-strength
output, `Inline=0`, the existing profile and producer/consumer fences were not
changed for publication. The release packages the existing tested executable;
this publication pass adds versioning, research/progress documentation and an
offline log analyzer, not another claimed FPS improvement.

Earlier investigations are preserved as historical pass notes:
[async recovery](async-recovery.md), [duplicate suppression](fresh-output.md),
[GPU sharing](gpu-handoff.md), [backend investigation](neural-inference-plan.md),
[runtime update](neural-speedup.md), [HIP timing](neural-host-timing.md) and
[inference-aware feeding](inference-feed.md). Statements that publication or
manual validation was pending refer to the state at the time of those passes.

## Remaining bottleneck and next optimization decisions

Feed overhead has fallen considerably while the observed HIP device wait stays
near 52 ms. The remaining evidence points toward GPU execution and outstanding
work/contended scheduling rather than time spent calling the host launch API.
A device wait is not a per-kernel timer; no specific neural kernel has yet been
proven to dominate, and the wait must not simply be removed.

The next performance work should separate kernel execution, internal memory
movement and queue contention during a normal user-initiated run, then target
the dominant operation while retaining the same tensor precision and model.
Record kernel identity, shapes and dependencies before considering graph replay
or kernel replacement. Fine-grained GPU timing is not implemented or enabled
by this checkpoint. Current logs lack per-row strength and executable identity;
future telemetry should record them to prevent mixed-mode or mixed-build
comparisons. These are open tasks, not completed optimizations.

The upstream [latest release](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.15)
was rechecked on 7 September 2026 and still resolves to v0.2.15. AMD's
[HIP execution documentation](https://rocm.docs.amd.com/projects/HIP/en/docs-7.2.0/how-to/hip_runtime_api/asynchronous.html)
explains asynchronous submission and completion waits, while its
[RDNA guide](https://gpuopen.com/learn/rdna-performance-guide/) recommends
reducing unnecessary submissions and evaluating contention. These support the
investigation direction, not a promised speedup or a verified 60 FPS result.

## Manual iteration workflow

The user runs each candidate normally and reports base FPS plus the selected
frame-generation setting. After scaling stops, analyze the settled cadence,
HIP and runtime logs and associate them with that candidate's deployment hash.
Do not infer an old run's version from whichever executable happens to be
installed later. Preserve full local logs privately; publish sanitized
aggregates and source hashes.

```powershell
python bridge/scripts/Analyze-Run.py `
  --cadence path/to/bridge-cadence-PID.log `
  --hip path/to/bridge-hip-timing-PID.log `
  --runtime-log path/to/that-runs-dlssnr_on_amd.log `
  --run-id unique-manual-run --version 0.1.0-pre.2 `
  --bridge-sha256 d902dc89c6977a9012028844da6acd788a2386e2c1bb43da71fa805cd230a85f `
  --since 20 --until 60 --hip-since 20 --hip-until 60 `
  --reported-base-fps 19 --framegen-multiplier 2 `
  --output docs/measurements/unique-manual-run.json
```

Replace the example paths, run ID, version, deployment hash and reported rate
for each actual run. Omit an unavailable HIP/runtime log or unknown reported
rate; do not invent missing values. Longer or shorter runs require appropriate
explicit time windows. Output is create-only, preserving earlier measurements.
The analyzer uses only Python's standard library and reads files; it does not
start capture, execute GPU work, launch a game or run a test suite.

Each next version should have a concrete change, version/hash record, precise
manual-run result and rollback. Analyze comparable intervals and distinguish
quality acceptance from FPS reporting. Build only production targets; do not
run automated tests, benchmarks or playback. Keep `Inline=0`, native size and
the user's current settings. Publish accepted checkpoints with observed versus
unmeasured results clearly separated. This is an on-demand workflow, not a
background monitoring service.

## Verification for this checkpoint

The bridge and wrapper production targets build successfully. Their build
outputs match the installed, manually exercised executable/DLL by SHA-256.
The log analyzer was used on these two existing recordings; no synthetic
test was created or run. Current observations establish activation and a modest
cadence improvement, not strict hardware pixel equivalence or guaranteed
frame-generation smoothness. No automated tests, playback, GPU benchmarks or
driver changes were performed for this publication.
