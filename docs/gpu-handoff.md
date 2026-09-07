# Native GPU frame handoff — 7 September 2026

## Follow-up: GPU path active, approximately 18 changed images/s

The next manual run, `bridge-cadence-4952.log`, confirms `transport=gpu_shared` with the D3D11 NT-handle route. It recorded 1,407 changed RGB submissions in 77.283 seconds (approximately 18.2/s), 6,860 identical submissions skipped and no occluded submissions. Its average feed rate was approximately 107/s and capture rate approximately 51/s. Sampled runtime jobs were 49–55 ms. These are real session observations, not a controlled benchmark; changed RGB submissions are not an independent measurement of neural evaluations or LSFG output.

The remaining neural-job time is much larger than the bridge's average per-iteration host timings: approximately 0.92 ms in neural present and 0.97 ms in output handoff/composition. Those host values include waits and must not be added to a job's time as disjoint GPU costs. The 60-FPS goal remains unmet. With fixed x2 interpolation, 60 output frames/s would require 30 usable base frames/s; 60 fresh neural evaluations/s would require about 16.7 ms per job. Neither result is established.

The current follow-up removes additional bridge work competing with inference:

- At strength 1, draw the existing neural output texture directly. At strength 0, draw the current source. Only intermediate strengths write the full-size composite. RGB comparison and output-health checks still cover every pixel. Display alpha remains opaque and the source/model resolution does not change.
- Replace contended per-pixel shared-memory atomics with register accumulation and a 64-lane tree reduction over the same 16x16 tile. Every lane reaches every reduction barrier, including partial edge tiles. Global sums retain exact 64-bit carry accounting for 4K. There is no approximate hashing or skipped changed pixel.
- Reuse the exact source black classification only while its input-upload sequence is unchanged. New WGC input invalidates that reuse; neural nonblack checks and visible comparisons still run for every candidate.
- Remove the intermediate CPU wait after the D3D12 output copy. The D3D11 output-ready wait and bounded consumer-completion wait already establish that the copy and its allocator use completed before the next reset. Input ownership waits and failure cleanup remain. Direct visible/history reads occur later, so a new flushed consumer signal now covers those reads, including occluded draws, before the next D3D12 output write.

The normal 120-Hz feed ceiling, presentation mode, native dimensions, runtime, model, neural settings, `Inline=0` and user LSFG profile are unchanged. This is a bridge optimization, not a rewritten neural kernel or a claim of a threefold speedup. The checked public upstream latest release remains v0.2.15 and its repository does not provide the HIP implementation source.

New logs identify `gpu_transport_revision=tree64_direct_endpoints_deferred_copy_wait`. Cumulative `total_direct_image_checks`, `total_blended_images` and `total_source_analyses` expose actual work paths; none is a display or inference FPS counter. The backup is `backups/gpu-contention-20260907-221701/`. Its deployment record is the authority for build/install status. Only production compilation, static review and file verification are permitted; no automated tests, GPU/playback runs or publication are performed. Performance and hardware image equivalence require manual verification.

Relevant API contracts: [D3D11 queue wait](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_3/nf-d3d11_3-id3d11devicecontext4-wait), [D3D11 queue signal](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_3/nf-d3d11_3-id3d11devicecontext4-signal), [uniform group barriers](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/groupmemorybarrierwithgroupsync), and [upstream release](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.15).

## Follow-up: GPU initialization failure in the user's manual runs

The subsequent manual report was approximately 15 FPS without frame generation and 30 FPS with it. The corresponding `bridge-cadence-31056.log` and `bridge-cadence-33036.log` both report `transport=cpu`, zero GPU-transport frames, and `cpu_fallback: Open shared GPU texture in D3D11 failed with HRESULT 0x80070057`. Their changed-image totals were 266 over 17.467 seconds and 209 over 13.714 seconds, respectively. These are changed RGB submission rates, not independently measured display or neural-evaluation rates. The new GPU path did not initialize in either session; the reported improvement cannot be attributed to GPU-only frame transfers. The reduced feed ceiling and other differences between runs remain possible contributors.

The follow-up replaces the failed D3D12-to-D3D11 texture-opening route. The existing D3D11 capture/consumer device now allocates the RGBA8 textures with explicit shader/render-target bindings and `D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE`. `IDXGIResource1::CreateSharedHandle` exports an unnamed read/write handle, and the same-adapter D3D12 device imports it with `OpenSharedHandle`. Both APIs retain the shared allocation; the temporary NT handle is closed after import. Imported dimensions, format, mip count, array size and sample count are checked before use. Shared fences and the existing COMMON-state handoffs retain the ownership protocol; no keyed mutex is added.

Successful setup identifies `d3d11_nt` in `transport_detail`. Failures identify the operation, input/output resource, native dimensions and sharing route. The CPU fallback remains available if setup is unsupported. The shader, blend arithmetic, exact RGB comparison, 120-Hz feed ceiling, neural DLL, weights, INIs and LSFG profile are unchanged by this fix.

This follows the D3D11-to-D3D12 NT-handle route described in [Slint's implementation report](https://slint.dev/blog/servo-with-slint-update), together with Microsoft's [shared-handle API](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgiresource1-createsharedhandle) and [D3D12 import API](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-opensharedhandle). The change removes the observed failing call; it does not establish driver acceptance or a new achieved frame rate without another manual session.

The follow-up backup is `backups/gpu-sharing-fix-20260907-220615/` under the installation. Its `deployment-state.json` records the actual build/install stage and protected-file verification. No automated tests, GPU initialization probe, playback or GitHub publication is performed. The historical sections below describe the preceding GPU-handoff build.

## Evidence and objective

The user's latest manual runs still feel below 20 FPS with frame generation on or off. Existing cadence logs recorded 656 changed RGB submissions in 50.989 seconds with LSFG enabled and 229 in 18.058 seconds with it disabled: approximately 12.9 and 12.7 changed submissions/s. There were respectively 10,256 and 3,827 identical submissions skipped, with no reported occlusion. The producer frequently executed over 200 feed iterations/s. These quantities are not measured LSFG output or unique neural-job rates.

The latest v0.2.15 runtime log records native 2560x1440, asynchronous shared interop, and sparse job times of 54, 52, 74, 80 and 78 ms. Sixty fresh neural evaluations/s requires approximately 16.7 ms per evaluation. Removing duplicate visible presentations did not establish that target. The fixed x2 profile would need 30 usable base frames/s to supply 60 output frames/s under ideal interpolation; actual downstream interpolation was not measured.

The previous bridge still downloaded capture pixels, uploaded neural input, downloaded neural output and conditionally uploaded visible pixels through CPU memory. This update replaces those steady-state transfers for native sessions. It does not replace the neural kernels, alter their precision, or claim to have achieved 60 FPS.

## Implementation

Normal native proxy sessions first use the existing CPU startup and runtime-health gate, then attempt the GPU path. The source and visible output share one native D3D11 device. A separate D3D12 device owns the private neural swapchain. Their adapter LUIDs must match.

WGC frames remain GPU textures. A shader-readable BGRA copy and integer-coordinate pixel shader convert the source into shared RGBA8 input without filtering. D3D11 signals and flushes its input-ready fence before the D3D12 queue waits, copies that input into its backbuffer and calls the existing hooked Present. The captured WGC frame remains owned until that input copy completes. The asynchronous neural runtime remains responsible for inference.

The processed backbuffer is copied into a shared GPU output texture. A separate output-ready fence orders the D3D11 consumer. The D3D12 copies retain their existing allocator/completion waits, and each shared resource returns to COMMON state before changing API ownership. Previous D3D11 output reads must complete before the next D3D12 output write. Fence waits remain bounded and device-removal errors stop processing.

On a GPU-path exception, the captured frame and shared resources stay owned while the D3D12 producer drains before the D3D11 consumer. A failed drain is not treated as completion: the bridge logs the failure and terminates only its own isolated process before C++ resource destruction, leaving pending allocations owned until OS process teardown. The captured application and Lossless Scaling process are not terminated. This failure path was reviewed statically, not exercised against the GPU.

A compute shader performs the existing integer RGB blend, exact visible RGB comparison and black-output checks. Its four 32-bit status words hold changed-image and nonblack flags plus a two-word source RGB sum. The 64-bit sum handles the fully-white 4K case without overflow. Only those 16 bytes are mapped back to the CPU per composition. The current composite and last accepted image stay on the GPU. Changed images are drawn at native dimensions into the existing BGRA8 visible swapchain; identical images skip visible presentation. Alpha remains opaque. Occluded presentations retain repaint retries and do not advance accepted history.

The integer blend remains `(original * (256 - alpha) + neural * alpha + 128) >> 8`, with alpha derived by the existing CPU rounding rule. Shaders use integer-coordinate texture loads and byte-domain RGB comparison. No resizing, filtering, lower-precision neural operators, model changes or approximate matching were introduced. Hardware output equivalence has not been tested, so mathematical/source equivalence is not a substitute for the user's visual check.

The normal producer ceiling changes from 500 to 120 feed iterations/s to reduce repeated work while retaining asynchronous polling above the 60-FPS objective. A lower explicit `--max-fps` limit wins. `--repeat-presentations` removes the implicit limit as before. A feed ceiling does not guarantee that many new neural outputs or displayed frames.

## Compatibility and diagnostics

`--cpu-transport` retains the prior CPU transport. Fixed-size, bypass, capture-saving and frozen-source modes also use it. Unsupported resource/fence sharing falls back during setup only when both devices remain healthy. Runtime GPU failures stop the session rather than silently restarting through a different path.

Normal `bridge-cadence-<pid>.log` output records `transport=gpu_shared` or `transport=cpu` and a setup/fallback explanation. It adds actual WGC capture rate, GPU-transport iteration count and per-stage elapsed times to the existing feed/change/presentation/duplicate/occlusion counters. These elapsed times include synchronization and are not isolated per-kernel GPU timings. No screenshots or image files are saved by normal diagnostics.

In GPU mode, the CPU source buffers retained from startup are not used for the running composition. Diagnostic capture mode deliberately remains on the previous CPU path and cannot establish GPU pixel equivalence. The next ordinary user-run native session must establish whether GPU sharing initialized, whether output remained correct, and whether changed-image cadence and perceived smoothness improved.

## Build, deployment and verification boundary

Production shaders are compiled with the installed Windows SDK FXC into embedded bytecode. The C++ Release target is built with:

```powershell
cmake --build bridge/build --config Release --target DlssNrBridge
```

This target does not execute the application, shaders, test suite or neural runtime. The allowed checks are production compilation, static review, whitespace/diff checks, installed-file and rollback hashes, protected configuration/model hashes and inactive process state. No automated tests, playback or benchmarks are authorized for this update. Achieved speedup, 60-FPS output, hardware pixel equivalence and runtime GPU-sharing initialization remain unverified.

The scoped backup is `backups/gpu-handoff-20260907-214019/` under the Lossless Scaling installation. It contains the preceding executable and manifest, original source files, the user's evidence logs and protected-file hashes. Its `deployment-state.json` records the actual deployment stage and validation results; this document alone is not proof of installation.

Native resolution, model files, weights, neural settings, `Inline=0`, Lossless Scaling profile and its LSFG selection remain protected. No driver/TDR, global power, security or Special K settings are changed. GitHub publication remains pending user verification.

## API references

- [Microsoft shared heaps](https://learn.microsoft.com/en-us/windows/win32/direct3d12/shared-heaps)
- [D3D11 shared fences](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_4/nf-d3d11_4-id3d11device5-opensharedfence)
- [D3D11 context Signal](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_3/nf-d3d11_3-id3d11devicecontext4-signal)
- [D3D12 resource-state synchronization](https://learn.microsoft.com/en-us/windows/win32/direct3d12/using-resource-barriers-to-synchronize-resource-states-in-direct3d-12)
- [D3D12 command-list reference counting](https://learn.microsoft.com/en-us/windows/win32/direct3d12/recording-command-lists-and-bundles)
- [WGC frame lifetime and ownership](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
