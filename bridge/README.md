# DLSS NR Bridge

Current checkpoint: **v0.1.0-pre.2**, the manually exercised inference-aware
feed build. The latest native 2560x1440 run averages 18.46 changed RGB/s and
the user reports about 19 FPS. See [the progress ledger](../docs/progress.md)
and [artifact manifest](../RELEASE.json). The 60 FPS goal is not yet met.

`DlssNrBridge.exe` captures one selected window with Windows Graphics Capture, feeds that image into a private D3D12 swapchain for the AMD proxy, and displays a visible D3D11 output window titled `DLSS NR Bridge`. Normal native-resolution proxy sessions attempt a GPU-shared transport after the existing startup health gate. Fixed-size, diagnostic and compatibility modes retain CPU frame transport.

The visible output is blended by the bridge itself so the shortcuts work live, even though the proxy only reads its INI at startup. In proxy mode, the visible output window is not created until warmup has produced completed-job log evidence, no fatal runtime markers, and a non-black NR readback when the source image itself is nonblack.

## Keyboard shortcuts

- `Ctrl+Alt+F6` toggles the visible output between bypass and processed blend.
- `Ctrl+Alt+F7` decreases effect strength by `0.1`.
- `Ctrl+Alt+F8` increases effect strength by `0.1`.

Strength is clamped from `0.0` to `1.0`. The visible window title shows the current state.

## Command line

```powershell
DlssNrBridge.exe --source-title "NR Static Image Test" --width 1280 --height 720 --seconds 25 --capture-dir runs\bridge-proof
```

Useful options:

- `--list-windows` prints capturable top-level windows with hwnd, pid, size, and title.
- `--source-hwnd 0x123456` selects a source window by handle.
- `--source-title "partial title"` selects the first visible, non-minimized window whose title contains the text.
- `--width 1280 --height 720` sets the default processing/output bounds, also used by `Start-Bridge.ps1` and auto-scale setup. The source is scaled to fit while preserving aspect ratio.
- `--native-resolution` waits for the first valid WGC frame, creates NR/visible swapchains at that exact source size, bypasses scaling, and fails clearly if the source dimensions change. Native mode errors if the captured source is larger than 3840x2160.
- `--seconds 25` exits after a bounded run.
- `--startup-delay-ms 2000` waits after loading `version.dll` before creating the D3D12 swapchain.
- `--warmup-frames 301` feeds the private D3D12 NR swapchain before the health gate checks the proxy log and NR readback. The visible D3D11 output window is created only after that gate passes.
- `--capture-dir path` saves proof frames and `bridge-report.txt`.
- `--freeze-source` diagnostic mode reuses the first valid captured frame for deterministic same-frame on/off proof. The source window is still polled for closed/minimized state, and the bridge title/report mark frozen-source diagnostic mode.
- `--ready-file path` writes the visible bridge HWND as decimal plus a newline, using a temp file and rename, after the first healthy visible present. Any stale ready file at that path is removed during startup and is not rewritten on startup failure.
- `--stop-event name` opens an existing named event with `SYNCHRONIZE`; if it cannot be opened, startup fails clearly. Signaling the event exits the bridge cleanly.
- `--parent-pid pid` opens the parent process with `SYNCHRONIZE`; if it cannot be opened, startup fails clearly. Parent exit stops the bridge.
- `--no-proxy` runs the capture/display path without loading `version.dll`.
- `--cpu-transport` keeps the existing CPU capture/readback/display path instead of attempting shared GPU textures. Unsupported GPU sharing also falls back during setup and records its reason; device loss or a failure after GPU processing starts stops the session.
- `--default-scheduling` opts out of the neural bridge's scoped precise timer and foreground-work scheduling requests. Normal proxy launches request the minimum supported timer period (normally 1 ms), preserve it while the bridge is covered, and opt this process out of execution-speed throttling. Results are written to `bridge-scheduling.log`. The timer request is released and prior controlled policies restored on orderly exit. No system-wide power plan, priority class or driver setting is changed.
- `--fixed-neural-feed` opts out of inference-aware feeding and retains the earlier polling policy. Normal full-strength native GPU sessions defer repeated feed/copy/compare work while the observed asynchronous HIP worker is waiting for its GPU work, then wake on its return. Explicit `--max-fps` still applies.

The GPU path keeps native images on the GPU through capture conversion, neural handoff, exact RGB comparison, composition and visible display. Shared textures and fences connect the capture/visible D3D11 device to the separate neural D3D12 device. A 16-byte status readback replaces the continuous full-frame CPU readback. GPU code retains the existing 8-bit channel conversion, integer blend formula, opaque display alpha and black-output thresholds. No filtering, reduced resolution or approximate duplicate comparison is used. These are implementation constraints; hardware pixel equivalence and achieved performance still require manual validation. See the [GPU handoff notes](../docs/gpu-handoff.md).

Both paths suppress only RGB-identical visible submissions while continuing capture, the asynchronous neural feed, health checks and hotkeys. The ordinary polling budget is 120 iterations/s (8.333 ms start-to-start), reduced from the preceding 500/s budget. Inference-aware GPU feeding now defers redundant iterations while an observed worker wait is active, with a 100 ms keepalive and immediate completion hints. Hints can bypass the implicit polling interval, but never an explicit `--max-fps`. These are scheduling policies, not neural completion or display rates. The CPU path retains reusable capture/upload storage. The [earlier duplicate-suppression notes](../docs/fresh-output.md) describe the preceding build.

`--repeat-presentations` restores repeated visible submissions and removes the implicit feed budget for a manual comparison. Explicit `--max-fps` limits still apply. `--no-proxy` also retains repeated presentation. Normal runs write `bridge-cadence-<pid>.log` with the actual transport mode and fallback reason, fresh-capture/feed/changed-RGB/visible-submission rates, duplicate and occlusion counts, GPU-transport iteration counts and per-stage elapsed times. These are not LSFG output or neural inference FPS. Capture-saving and frozen-source diagnostics use CPU transport and therefore do not validate the GPU shaders.

Builds with the optional HIP 7 headers also collect bounded host API timing in
`bridge-hip-timing-<pid>.log` during normal user-initiated playback. This observes
existing neural submission/copy/wait calls; it is diagnostic work, not an FPS
improvement or GPU kernel timer. `--no-hip-timing` disables it. See
[neural host timing](../docs/neural-host-timing.md) for measurement limits and
the unchanged-quality constraints. The same observer now exposes worker-wait
hints for the [inference-aware feed optimization](../docs/inference-feed.md).
That scheduling optimization remains active after the bounded timing capture
ends. It uses no additional HIP calls. Missing HIP observation, CPU transport,
bypass/intermediate strength, or incompatible runtime settings retain ordinary
feeding. `--no-hip-timing` disables observation and therefore also these hints.

At full effect strength, GPU mode displays the existing neural texture directly; bypass similarly uses the current source texture. Intermediate strengths retain the exact integer blend. The statistics shader uses a 64-lane tree reduction without per-pixel shared atomics and caches the source black classification until the input upload changes. An additional consumer fence covers direct display and history reads before the shared output is reused. `total_direct_image_checks`, `total_blended_images`, and `total_source_analyses` are cumulative work counters, not output FPS. See the latest follow-up in the [GPU handoff notes](../docs/gpu-handoff.md).

## Runtime files

The executable expects the AMD proxy runtime files beside it when proxy mode is used. On proxy startup, any existing `dlssnr_on_amd.log` in the working directory is moved aside with a `.previous.<pid>` suffix or cleared; if the active log path cannot be cleared, startup fails so readiness cannot be based on stale completed-job lines:

- `version.dll`
- `nvngx_dlssnr.dll`
- `dlssnr_on_amd.ini`

The bridge does not include third-party runtime DLLs or NVIDIA assets. Auto-scale launchers should wait for `--ready-file` before activating Lossless Scaling on the reported bridge HWND. Keep runtime DLLs and run outputs ignored; users should supply private runtime files from their own legally obtained archives.

The INI used by the launcher should keep the proxy enabled and in the proven NR path:

```ini
Enabled=1
UseFsrInputs=0
LocalStructure=1
```

## Build

```powershell
cmake -S nr-development/bridge -B nr-development/bridge/build -G "Visual Studio 17 2022" -A x64
cmake --build nr-development/bridge/build --config Release --target DlssNrBridge
```

The implementation uses the Windows SDK Windows Graphics Capture interop path: `IGraphicsCaptureItemInterop::CreateForWindow` for HWND capture and `Direct3D11CaptureFramePool::CreateFreeThreaded` for capture frames without a dispatcher queue. The Windows SDK `fxc.exe` compiles the transport shaders into embedded headers during the production build; no runtime shader compiler is needed. The GPU path requires same-adapter native D3D11/D3D12 resource and fence sharing.

## Frame pacing and performance diagnostics

The visible D3D11 swapchain uses synchronized presentation while the private D3D12 feed uses an unsynchronized present. Normal identical-output suppression uses 120/s polling plus inference-aware completion hints when available; `--max-fps 30` explicitly limits all feeds, including hints. `--max-fps 0` restores the default policy. Work that exceeds the interval receives no additional post-work delay. This option does not change image resolution, neural strength or model settings.

Capture reports now include `bridge_present_fps`, `visible_elapsed_seconds`, and per-stage host timing. These measure bridge presentation and host costs, not unique neural frames, game FPS or end-to-end input latency. See [the measured performance report](../docs/performance.md) for how completed neural jobs were measured separately.

## Acceptance criteria

A successful functional test must show all of these:

- WGC captures the selected source window and the source is not minimized.
- The private D3D12 NR feed presents frames before the visible D3D11 output is created.
- Proxy logs show healthy D3D12 processing jobs before visible output starts, with no GPU errors, invalid-kernel errors, `FAULT`, `CRASH`, or 100% zero-output markers.
- Saved proof frames include corresponding `original`, `nr`, and `display` PPMs.
- Pixel comparison shows a real image difference between original and NR/display outside overlays. Use a static source image for deterministic same-content comparison.
- `Ctrl+Alt+F6`, `Ctrl+Alt+F7`, and `Ctrl+Alt+F8` change the visible output live while the process continues running, and hotkey proof snapshots are saved after the next visible present so filenames match pixels.

Jobs without a visible pixel difference, GPU errors, invalid kernel failures, or all-zero/black output from a nonblack source are failures even if the process reports frames presented. For dynamic sources, the proxy can process asynchronously, so the displayed NR frame may trail the captured source by a frame; deterministic verification should use `--native-resolution --freeze-source` when proving same-frame on/off behavior without scaling. The bridge uses ordinary 8-bit UNORM SDR swapchains and does not override DXGI color space.
