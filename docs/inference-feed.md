# Inference-aware feeding at native resolution

> Follow-up: the user manually exercised this build and reported about 19 FPS.
> The complete log averages 18.46 changed RGB submissions/s. This code is now
> included in v0.1.0-pre.2; see [progress.md](progress.md) for the recorded
> comparison, source hashes and publication status. The original pass notes
> below preserve the pre-validation state and do not imply a verified 60 FPS.

## Implemented change and acceptance status

This pass changes production scheduling, rather than only collecting more
timing data. Normal full-strength GPU sessions defer redundant Direct3D feed,
output-copy and image-comparison passes while the neural worker is inside its
existing HIP device wait. The bridge wakes when that wait returns, obtains the
newest captured input, and runs the established neural Present and output path.

The change targets competing GPU work and the delay between completed work and
the next feed. It does not replace the neural kernels, reduce resolution,
change tensor precision, weaken the effect, or reinterpret generated/repeated
frames as newly evaluated neural images. Actual FPS improvement and the 60 FPS
target remain unverified pending the user's normal playback. No automated
tests, synthetic scenes, gameplay, benchmarks, or shader probes are run.

## Evidence from the user's latest run

Source files are `bridge-hip-timing-49556.log`, `bridge-cadence-49556.log`, and
`dlssnr_on_amd.log` in the installed runtime directory. Immutable copies were
saved under `backups/inference-feed-20260907-230037/` before editing.

For worker thread 46984, timing rows with `elapsed_s >= 20` contain:

| Measurement | Observed total / derived value |
| --- | --- |
| Recorded worker windows | 84,590.8193 ms |
| Kernel launches | 229,164 |
| Device synchronization calls | 1,469 |
| Launches per synchronization call | 156 |
| Time inside launch APIs per synchronization call | 0.15615 ms |
| Time inside device synchronization per call | 52.37672 ms |
| Asynchronous copy calls | 5,876 |
| Non-default-stream launches | 0 |

These are host API measurements. Device synchronization includes outstanding
GPU work and contention, and is not isolated kernel execution time. One wait
must not be relabelled a neural job solely from an API counter.

Representative steady cadence rows show approximately 106 feed iterations/s
against 17-18 changed RGB submissions/s. The last available cadence row records
9,549 image checks and 7,552 identical submissions suppressed. It is not a final
shutdown row, and rates vary later in the run; its whole-run average is not a
controlled full-strength benchmark. These counters establish that large
amounts of GPU transport/comparison work rediscover unchanged images. They do
not measure the GPU time recoverable by eliminating that work.

## Online research and implementation decisions

Research was checked on 7 September 2026 against primary project, AMD, and
Microsoft documentation.

1. **Existing backend:** the latest upstream release still resolves to
   [v0.2.15](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.15),
   already installed here. Its listed fixes concern FSR3 double capture and a
   startup crash. The [project](https://github.com/danielblnc/DLSS-NR-on-AMD)
   reports roughly 33 FPS at 1080p on a 9070 XT; that is not evidence for native
   1440p performance. No newer same-model drop-in improvement was established.

2. **HIP graphs:** AMD's [graph guide](https://rocm.docs.amd.com/projects/HIP/en/docs-7.2.0/how-to/hip_runtime_api/hipgraph.html)
   describes dependency graphs and reduced repeated dispatch overhead. The
   measured host launch cost here is small relative to the device wait. This
   does not rule out GPU-side graph benefits, but it does not support a large
   claimed speedup. The current legacy/default-stream path and its copies need
   a complete dependency audit before capture; no blind graph wrapper or
   removed synchronization call was installed.

3. **GPU contention and submission:** AMD's [RDNA performance guide](https://gpuopen.com/learn/rdna-performance-guide/)
   recommends minimizing command submissions, avoiding excessive small command
   buffers, and choosing overlapping workloads with care. This supports
   removing avoidable full-frame work during neural execution. It does not
   quantify the benefit on this machine. That is the optimization implemented.

4. **Synchronization semantics:** AMD's [asynchronous execution documentation](https://rocm.docs.amd.com/projects/HIP/en/docs-7.2.0/how-to/hip_runtime_api/asynchronous.html)
   distinguishes host submission, stream work, and completion waits. Microsoft's
   [multi-engine synchronization guidance](https://learn.microsoft.com/en-us/windows/win32/direct3d12/user-mode-heap-synchronization)
   requires producer/consumer ordering. HIP wait return is therefore used only
   as a scheduling hint; all existing Direct3D transitions and fences remain.

5. **Environment tuning:** AMD's [documented HIP variables](https://rocm.docs.amd.com/projects/HIP/en/latest/reference/env_variables.html)
   describe serialization, device isolation, argument placement and queue
   controls. These are not interchangeable performance switches, and no
   documented setting was established as a fix for the measured 52 ms wait.
   No global/process environment, compute mask, driver or power settings were
   changed in this pass.

6. **Recent large-gain reports:** upstream [issue 111](https://github.com/danielblnc/DLSS-NR-on-AMD/issues/111)
   proposes neural processing before upscaling, at a lower render resolution.
   That is a user suggestion rather than a measured compatible AMD backend.
   Its linked [Autopilot v1.7.1 release](https://github.com/Kizzuwatnaa/DLSS5-Autopilot/releases/tag/v1.7.1)
   does not itself establish a native-resolution AMD inference speedup. This
   research did not substitute lower-resolution rendering or another model.

## Scheduling and ownership

The existing named-import observer records whether a successful kernel launch
occurred on the calling thread. A following `hipDeviceSynchronize` on a worker
thread increments a host-side active-wait count. On return it advances a hint
counter and signals one process-local event. The original function is still
called exactly once, with its original result and Windows last-error value
preserved. No new HIP kernel, stream, event, allocation, copy or wait is added.
The hints continue after the 120-second timing window ends.

The main loop enables deferral only for the GPU transport, stable full effect
strength, successful readiness, and explicit asynchronous backbuffer settings
(`Enabled=1`, `Inline=0`, `UseFsrInputs=0`, `Interop=1`). Those settings are
reread alongside normal health checks. Missing/incompatible settings,
unavailable observation, CPU/diagnostic paths, bypass, intermediate strength,
and pending hotkey changes retain ordinary feeding.

While deferring, the loop continues to drain capture frames without copying
their pixels on the GPU. It retains at most one newest unsubmitted frame and
releases its predecessor. At a feed, the newest available frame wins; ownership
then moves into the existing try/catch that retains it until the producer fence
completes and drains GPU work before releasing resources on failure. The frame
pool cannot recycle a submitted surface early. This follows the ownership
model in Microsoft's [screen capture documentation](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture).

Waits use [MsgWaitForMultipleObjectsEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-msgwaitformultipleobjectsex)
with stop, parent-exit, progress, and window-message wakeups and a maximum
five-millisecond timeout. A 100 ms keepalive bounds feed deferral independently
of progress hints. A completion hint may bypass the implicit 120 Hz poll budget
but never a user's explicit `--max-fps` interval.

The upstream ready flag can be published after the observed HIP wait returns.
Consequently, a hint always runs the normal Present/copy/exact-comparison path.
If no new worker wait is visible after that feed, one near-term follow-up is
scheduled, still respecting the explicit FPS cap. It cannot repeatedly rearm
without another returned-wait hint. Normal polling remains the fallback.

## Verification and rollback

Only the production `DlssNrBridge` target is compiled. Static review covers
argument/result forwarding, hint lifetime, bounded waits, fresh capture
ownership, explicit caps, early-completion/publication races, fallback modes,
and unchanged GPU fences/shaders. Compilation does not demonstrate hardware
output equivalence, stable runtime activation, or achieved FPS.

New cadence counters are `busy_feed_deferrals`, `completion_hint_feeds`,
`publication_rechecks`, `keepalive_feeds`, and `prefetched_captures`. These
describe scheduling work, not neural completions or displayed/generated FPS.
`--fixed-neural-feed` restores ordinary polling; `--no-hip-timing` disables the
observer and therefore also progress hints. Both are manual comparison options.

The deployment record preserves the current ten protected-file hashes and a
rollback executable. Rollback replaces only the inactive bridge executable;
it never restores an older Lossless Scaling profile. Native dimensions,
weights/model files, shader code, strength/settings, `Inline=0`, the wrapper,
and Git HEAD remain protected. No commit, push, release or publication is made.
