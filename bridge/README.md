# DLSS NR Bridge

`DlssNrBridge.exe` is the bridge process for testing the AMD DLSS-NR proxy against an arbitrary on-screen window. It captures one selected window with Windows Graphics Capture, feeds that image into a private D3D12 swapchain for the AMD proxy, reads the processed backbuffer, and displays a visible D3D11 output window titled `DLSS NR Bridge`.

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
cmake --build nr-development/bridge/build --config Release
```

The implementation uses the Windows SDK Windows Graphics Capture interop path: `IGraphicsCaptureItemInterop::CreateForWindow` for HWND capture and `Direct3D11CaptureFramePool::CreateFreeThreaded` for capture frames without a dispatcher queue.

## Frame pacing and performance diagnostics

The default has no extra frame-delay cap: the visible D3D11 swapchain paces presentation, while the private D3D12 feed uses an unsynchronized present. An optional `--max-fps 30` sets a start-to-start frame budget; `--max-fps 0` restores default pacing. This option does not change image resolution, neural strength or model settings.

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
