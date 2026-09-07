# Async recovery and native-resolution overhead

The inline frame-generation experiment was reverted after the user reported severe stalling and a driver timeout. Its runtime log recorded neural jobs taking 5,437, 5,938 and 5,906 milliseconds. These observations associate the regression with the experiment; they do not independently establish the cause of the driver reset.

The installed configuration retains asynchronous inference (`Inline=0`), automatic native source dimensions, the existing neural runtime and weights, full effect strength, and the user's image-quality settings. The runtime configuration is private and is not distributed with this document. The rejected duplicate-presentation suppression and its inline-mode recommendation are removed.

## Changes after the rollback

Capture rotates two reusable CPU buffers instead of allocating and clearing a new full-resolution vector for every captured frame. Native-size input is passed by reference; fixed-size scaling retains the previous algorithm and cached prepared image. Warmup uses the same preparation cache.

When capture provides no new input, the bridge retains the already uploaded bytes. It still copies input to the neural backbuffer, calls the neural runtime, reads back its output and presents the visible frame. This does not suppress presentations, classify images as duplicates, drop neural evaluations or introduce a new frame cap. Source black-frame classification is cached only until the source changes; the classification threshold and neural-output checks are retained.

The D3D12 fence wait explicitly handles device removal before reading back a frame or reusing resources. The timeout, command ordering and GPU fences are unchanged. This reports a reset as an error instead of accepting its fence value as successful work; it does not prevent a driver timeout or claim to fix the driver's cause.

The auto-scale proxy retains WGC capture and native 1:1 geometry when frame-generation/profile settings change on a ready or active bridge. It uses the snapshot belonging to that settings call and forwards the user's frame-generation choices unchanged. The original settings function runs outside the proxy mutex. Effective handoff settings are logged for the user's manual check.

## Quality and verification boundary

The changes preserve processing dimensions, channel conversion, resizing and blending arithmetic, model assets and neural-runtime configuration. They reduce redundant host work; no new neural-inference throughput, gameplay FPS, interpolation improvement or visual-equivalence result has been measured.

Production-only compilation, static source review, installed-file hashes and inactive app initialization are the permitted checks for this update. No automated tests, playback, GPU benchmarks or synthetic scenes are authorized. GitHub publication remains pending the user's manual verification. The earlier results in [performance.md](performance.md) are historical and do not validate this update.

After installation, manually activate Scale on the source application, check image quality and responsiveness, then compare motion with the chosen frame-generation setting. Source-resolution changes still require stopping and restarting scaling. A responsive settings window establishes initialization only, not stable playback.

## Completed local checks, 7 September 2026

Both production Release targets compiled successfully: `Lossless` and `DlssNrBridge`. The source diff passed whitespace inspection. No test targets, test suites, playback or benchmarks were run.

The two binaries were installed with verified backups and matching SHA-256 hashes. The installation manifest now records those binaries. Lossless Scaling reopened, responded, loaded the updated wrapper and its original library, and wrote a fresh initialization log entry. There were no active bridge processes, and the neural-runtime log remained unchanged.

Eight protected files matched their pre-installation hashes: auto-scale configuration, both neural INIs, original Lossless Scaling library, saved app profile, neural weights, neural model DLL and AMD runtime DLL. The saved frame-generation choice remains Off. Native resolution and `Inline=0` remain selected. Source changes are local and uncommitted; nothing was published to GitHub.

The private installation backup and verification record are in `backups/async-overhead-20260907-202245/` under the Lossless Scaling installation, with the record named `deployment-state.json`. The rejected experimental patch and binaries remain archived separately in the earlier recovery backup. Neither archive is part of the public repository.

## API references

- [Microsoft: D3D12 resource mapping](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12resource-map) documents persistent upload mapping and the requirement to complete CPU writes before GPU consumption. Existing synchronization is retained when upload bytes are reused.
- [Microsoft: readback heaps](https://learn.microsoft.com/en-us/windows/win32/direct3d12/readback-data-using-heaps) requires fence completion before CPU access. Those waits remain in place.
- [Microsoft: GetCompletedValue](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12fence-getcompletedvalue) defines `UINT64_MAX` as device removal, rather than successful completion.
