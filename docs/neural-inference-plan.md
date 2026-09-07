# Neural inference acceleration: findings and implementation path

## Outcome and scope

Implementation follow-up: the [installed runtime and scheduling update](neural-speedup.md) now applies upstream v0.2.15 and scoped bridge timing requests. The investigation below describes the earlier static-audit state; its proposed kernel/graph rewrite remains separate and unimplemented.

The investigation identified concrete candidates inside the AMD inference backend. It did not implement a replacement backend or demonstrate an inference speedup. Capture/presentation optimizations are already separate from this work. Native dimensions, weights, per-operator precision, full-strength output and asynchronous mode remain the constraints.

This investigation reads files and existing logs only. No neural DLL was executed by the inspector, no playback or benchmark was started, and no installed executable, configuration or model asset was changed. The new inspector is `bridge/scripts/Inspect-NeuralRuntime.py`; it is independent of the installed bridge.

## Local evidence

The inspected AMD runtime is v0.2.12. Its SHA-256 is:

```text
16b30141fb63f0cee385e4683c50bec4b8482057b28de6fd30e3b5311664d8f2
```

The existing log reports 2560x1440, gfx1201 on the RX 9070 XT, async mode, and shared zero-copy input/output interop. The most recently inspected log contains only four sampled job durations, including startup: 125, 63, 78 and 94 ms. These are sparse wall-time samples, not isolated GPU execution times or a precise displayed-frame rate.

The binary contains a gfx1201 code object with 33 kernel entry points. This is a count of compiled functions, not launches per inference. Selected compiler metadata:

| Kernel name | Vector registers per thread | Fixed shared memory per workgroup | Private segment bytes |
| --- | ---: | ---: | ---: |
| `k_conv_res2` | 202 | 0 | 0 |
| `k_contract2` | 161 | 16,384 | 0 |
| `k_attention` | 144 | 28,928 | 0 |
| `k_pre_block_1h_32_fp8` | 58 | 64,640 | 0 |
| `k_swin_1h_32_fp8` | 58 | 62,592 | 0 |
| `k_post_block_1h_32_fp8` | 56 | 62,592 | 0 |

These kernels specify wavefront size 32 and maximum workgroup size 256. That maximum is not an observed launch size. The names above are the readable function-name portions of the mangled symbols retained in the JSON report.

High register/shared-memory demand can constrain resident work and latency hiding. It does not establish that these functions dominate this application's inference or quantify attainable speedup. None of these selected gfx1201 entries declares a nonzero fixed private segment, so this audit does not establish scratch spilling.

The runtime imports `hipLaunchKernel`, `hipDeviceSynchronize`, synchronous/asynchronous copies and HIP events. Its normal/delay import tables contain no HIP graph API entries. This supports investigating a graph backend, but cannot rule out dynamic symbol resolution or identify synchronization frequency. Simply deleting synchronization calls would be incorrect.

The runtime also imports `GetTickCount64` and performance-counter APIs. The sampled 63/78/94 ms pattern alone is not proof of a sleeping worker: Windows documents a typical 10-16 ms resolution for `GetTickCount64`. Attribution of the job timer and precise CPU/GPU timing are still needed. No timer or global scheduling settings were changed.

The inspected shell, user and machine environments have no explicit `HIP_LAUNCH_BLOCKING`, `AMD_SERIALIZE_KERNEL`, `AMD_SERIALIZE_COPY`, `HSA_CU_MASK` or `ROC_GLOBAL_CU_MASK` settings. This is not a snapshot of another running process's environment.

Private reproducible evidence is stored beneath the installation's `backups/neural-runtime-audit-20260907-204655/`: `runtime-metadata.json` and `installed-guards.json`. No vendor executable payload is written into the repository by the inspector.

## Same-model implementation path

### 1. Separate GPU execution from CPU submission and waiting

Instrument the inference backend with a GPU event before the first neural operation and after the last, on the actual inference stream. Record high-resolution CPU timestamps for submission and completed-output publication separately. Keep the Direct3D producer/consumer fences. Collect per-kernel durations and launch shapes before selecting a kernel to rewrite.

This would identify whether the main opportunity is launch gaps, synchronization, memory movement inside the network, or matrix computation. Instrumentation and playback remain future work under the user's current manual-testing instruction. Do not substitute bridge presentation FPS for any of these quantities.

### 2. Replay a fixed-shape HIP graph

Keep weights and intermediates resident, and create an explicit inference stream. Build and instantiate the dependency graph once for a fixed resolution/model/layout, then replay it for new input. Keep the same kernels and precision in this first step. Graph replay is a way to reduce dispatch overhead without intentionally changing neural arithmetic.

Use stable per-slot buffers or update node arguments correctly when the input/output pointers change. Rebuild or select a different graph when dimensions, model, tensor layout or execution-affecting settings change. Preserve all input-ready and output-ready dependencies, and bound in-flight work so completed output does not accumulate behind old jobs.

The graph belongs inside the asynchronous worker. It does not require restoring `Inline=1` or making the game wait for same-frame inference. It also does not make interpolation equivalent to fresh neural evaluation.

This is not a transparent one-line switch around the current DLL: synchronous allocations/copies and default-stream behavior must be audited before capture. AMD explicitly documents restrictions on synchronous calls during stream capture. A blindly injected capture wrapper could break dependencies or deadlock. No such wrapper was installed.

### 3. Optimize the expensive measured kernels

For `k_conv_res2` and `k_contract2`, investigate smaller accumulator tiles, shorter register lifetimes and reuse of loaded data. Select the block/tile shape from actual tensor dimensions, not the compiler's maximum-workgroup metadata. Check whether existing matrix instructions are well utilized before replacing arithmetic.

For the pre/Swin/post block kernels, investigate reusing shared-memory storage across non-overlapping phases and changing staging tiles to reduce the approximately 61-63 KiB reservations. Preserve attention windows, shifts, padding, normalization, quantization scales and all intermediate rounding points. Do not change FP16/FP32 operators to FP8 merely because other kernels already use FP8.

Fuse adjacent layout/conversion operations only when their exact intermediate precision and dependencies can be preserved. Higher occupancy or fewer launches is not a successful result unless the full inference gets faster with accepted output quality.

## Source and performance boundaries

As checked on 7 September 2026, the public upstream repository exposes documentation and release binaries, not the HIP implementation needed for these changes. Implementation therefore needs the runtime sources or an independently reconstructed execution graph, tensor layouts and parameter ABI. Compiled entry-point metadata alone is insufficient to recreate the network faithfully. No usable alternative drop-in optimized backend was established by this search.

Upstream v0.2.15 is newer than the installed runtime. v0.2.13 advertises a 2% gain and fixes an optimization/CPU-copy fallback; v0.2.14 mentions a speculative queue-stall fix; v0.2.15 fixes FSR3 double capture and a startup crash. These are reasons for a separate compatibility evaluation, not evidence for a 100x speedup. The inspected local log already reports zero-copy. The runtime was not updated during this investigation.

Using 80 ms only as a representative example, a 100x speedup requires 0.8 ms per complete neural evaluation. A 100-neural-FPS target requires 10 ms, or 8x relative to that example. Neither target is demonstrated. If an unchanged part accounts for fraction p of total time, even eliminating all other work caps speedup at 1/p. Consequently graph replay alone cannot deliver 100x unless virtually all current time is removable scheduling overhead; the current evidence does not establish that.

Do not claim success from FPS counters, use lower resolution or a smaller model, prune work, reuse stale outputs as fresh frames, or re-enable the rejected inline experiment. Numerical/output comparison and completed-inference throughput must validate any later backend implementation; those checks were not run here.

## Primary references

- [Upstream repository and architecture description](https://github.com/danielblnc/DLSS-NR-on-AMD)
- [Upstream release notes](https://github.com/danielblnc/DLSS-NR-on-AMD/releases)
- [AMD HIP graphs: dependencies, replay and capture restrictions](https://rocm.docs.amd.com/projects/HIP/en/latest/how-to/hip_runtime_api/hipgraph.html)
- [AMD HIP performance guidelines: register pressure, shared memory and synchronization](https://rocm.docs.amd.com/projects/HIP/en/latest/how-to/performance_guidelines.html)
- [Microsoft GetTickCount64 timing resolution](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-gettickcount64)
