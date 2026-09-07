# Neural submission and synchronization timing

> Historical implementation notes. Host timing activated successfully in the
> subsequent manual recordings. Results and the v0.1.0-pre.2 publication are
> recorded in [progress.md](progress.md); earlier pending-status statements
> below describe this diagnostic pass at the time it was made.

## Status

Follow-up: [inference-aware feeding](inference-feed.md) consumes the results of
the user's run and adds actual scheduling changes. The observer still forwards
the same HIP calls, but now also exposes worker-wait activity to the main loop.
The text below describes the original diagnostic installation and its limits.

This change adds diagnostics for the unresolved 18 FPS report. It is not an
inference optimization, and no FPS improvement or 60 FPS result is claimed.
Only production compilation and static inspection are authorized in this pass;
the user performs playback manually. Nothing is published to GitHub.

The latest inspected manual run uses the installed v0.2.15 runtime and shared
GPU transport at 2560x1440. Its sampled neural job durations are 48-53 ms. The
cadence log records 376 changed RGB submissions in 21.630 seconds, approximately
17.38 per second. Those are different measurements. Neither the feed rate nor
the game's frame counter establishes fresh neural throughput or displayed FPS.

The existing runtime contains detailed timing messages, but this inspection did
not establish a supported way to enable them in the current asynchronous
backbuffer route. Undocumented environment switches were not enabled. The log
also reports residual application off; replacing entire completed frames with
older residuals applied to newer inputs would not establish faster neural
evaluation or unchanged temporal quality, so that path was not substituted.

## What the next manual run records

A normal bridge launch attempts to observe six named HIP imports in the loaded
neural module, before creating its swapchain. If successful, the cadence log
contains `hip_host_timing=active_host_api_timing`, and the runtime directory
contains `bridge-hip-timing-<pid>.log`. The measurement window is approximately
the first 120 seconds from observer initialization, including warmup.

Each row covers an interval on one host thread. For each API category it records
the number of calls, accumulated elapsed wall time, maximum call duration, and
number of non-success results. The categories are kernel launch, device wait,
stream wait, event wait, synchronous copy, and asynchronous-copy submission.
`nondefault_launches` counts calls with a non-null stream argument; it is not a
count of distinct streams or proof that stream capture is valid.

Interpretation matters:

- `launch_ms` is time inside the launch API, including any driver blocking. It is
  not pure CPU execution time and is not GPU kernel time.
- Wait durations include pending work and contention. Device synchronization can
  cover multiple streams; its call count is not a neural-job count.
- Copy-API durations do not measure individual GPU transfer operations.
- Intervals from different threads overlap and must not be summed as one
  critical path. Warmup must be separated from steady state using the runtime
  log. Incomplete intervals at shutdown may not be recorded.

These measurements can establish whether much time is spent inside submission
APIs, explicit waits, or copies. They cannot identify an individual expensive
GPU kernel. A graph optimization still needs an audited stream and dependency
model; this observer does not implement capture or graph replay.

## Preservation and overhead

Signatures come from the existing HIP 7 SDK headers. Compile-time checks compare
every observer signature against the corresponding HIP declaration. The bridge
does not link another HIP runtime. It resolves the module already loaded by the
neural DLL, checks its named import slots against the original exported
functions, and atomically replaces only those six slots in that module.

Each observed call forwards the original arguments exactly once and returns the
original HIP result. The Windows last-error value is preserved across timing
and aggregation. No GPU event, stream, kernel, copy, allocation, wait, or graph
operation is added, removed, or reordered by the observer. No vendor DLL is
modified on disk. Original memory-page protection is restored after each import
replacement; an unexpected slot or protection failure during replacement stops
startup. Callback state remains valid until process exit.

Host timing has overhead; its effect on FPS is unmeasured. Workers accumulate
per-thread counters and offer at most one aggregate per interval to a bounded
32-entry queue, using a nonblocking lock attempt. They perform no file I/O and
never wait for queue space. The existing cadence-log update drains the queue.
Capture stops after approximately 120 seconds or an 8 MiB output limit. A
future manual comparison can disable interception with `--no-hip-timing`.

Native dimensions, model and weights, neural parameters, strength, asynchronous
mode, Direct3D fences and texture ownership remain unchanged. The user's
Lossless Scaling profile is neither rewritten nor restored from a backup.

## Build and review

The SDK is optional. Set `BRIDGE_HIP_INCLUDE_DIR` to the directory containing
`hip/hip_runtime_api.h` when configuring CMake. HIP 7 headers enable the
observer; otherwise the bridge builds with `unavailable_at_build` diagnostics.
The production target is `DlssNrBridge`; building it does not execute tests.

Static review covers signature compatibility, argument/result forwarding,
import bounds and ownership, memory-page restoration, lifetime, bounded
logging, and absence of additional GPU calls. Compilation and static review
do not establish that import interception activates in a live session or that
visual output is equivalent. Those remain manual-run verification items.

The installation's `backups/hip-host-timing-<timestamp>/deployment-state.json`
records the current protected-file hashes, previous executable, build hash,
source snapshots, and exact verification status. The previous executable is
`DlssNrBridge.rollback.exe` in that directory. Rollback replaces only the bridge
executable while inactive; it does not restore old profile settings.

## References

- AMD HIP API declarations: the existing SDK's `hip/hip_runtime_api.h` and
  `hip/hip_version.h` (7.2.26024 in this build environment).
- [AMD HIP synchronization and concurrency](https://rocm.docs.amd.com/projects/HIP/en/docs-6.4.3/how-to/hip_runtime_api/asynchronous.html)
- [Microsoft performance-counter guidance](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps)
- [Microsoft VirtualProtect](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualprotect)
- [Upstream runtime and release notes](https://github.com/danielblnc/DLSS-NR-on-AMD)
