# NR Auto Scale proxy

This folder builds a native `Lossless.dll` proxy that lets Lossless Scaling scale the DLSS-NR bridge window automatically instead of asking the user to choose the bridge manually.

The default path is native resolution. The bridge captures and presents at the source window size, and the proxy tells Lossless Scaling to use a 1:1 target so the visible effect comes from DLSS-NR rather than an extra upscaling pass.

The proxy loads `Lossless_original.dll`, forwards Lossless Scaling exports, and wraps:

- `Init`
- `ApplySettings`
- `Activate`
- `UnInit`

When scaling starts, the proxy launches the bridge with a source window handle, waits asynchronously for the bridge to publish its visible output window in a readiness file, then calls the original `Activate(bridgeHwnd)` on the Lossless Scaling UI thread. It does not inject into the source application and does not load the AMD/NVIDIA runtime DLLs inside the Lossless Scaling process.

## Build

Run from this folder:

```powershell
.\build.ps1
```

The main artifact is `build\Release\Lossless.dll` when using Visual Studio.

## Runtime config

`NrAutoScale.ini` lives beside the installed proxy DLL. Missing values use these defaults:

```ini
[AutoScale]
Enabled=1
BridgeExe=nr-bridge\runtime\DlssNrBridge.exe
RuntimeDirectory=nr-bridge\runtime
CaptureDirectory=
HipVisibleDevices=1
FreezeSource=0
NativeResolution=1
Width=960
Height=540
StartupDelayMs=2000
WarmupFrames=320
ReadyTimeoutMs=180000
DefaultScalingTypeIfOff=0
ForceCaptureApi=1
```

Relative paths resolve from the folder containing `Lossless.dll`.

The proxy applies only the settings needed for the bridge target before it calls the real `Activate`: resize-before-scaling off, clip cursor off, multi-display mode on, and WGC capture (`ForceCaptureApi=1`). With `NativeResolution=1`, it also forwards `--native-resolution` to the bridge and applies 1:1 Lossless Scaling target settings: custom scaling mode, scaling type off, and scale factor `1.0`. Set `NativeResolution=0` only when you want the older fixed-size bridge path that uses `Width` and `Height`.

`CaptureDirectory` and `FreezeSource` are private diagnostics for verification runs. Leave both at their defaults for normal use. When `CaptureDirectory` is non-empty, the proxy forwards it as `--capture-dir` so the bridge can save comparison frames and its report. When `FreezeSource=1`, the proxy forwards `--freeze-source` so the bridge can reuse the first valid captured source frame for same-frame comparison.

## IPC contract with the bridge

The proxy starts:

```text
DlssNrBridge.exe --source-hwnd 0x... --width 960 --height 540 --startup-delay-ms 2000 --warmup-frames 320 --ready-file <ready.txt> --stop-event Local\DlssNrStop-<pid>-<generation> --parent-pid <pid> --native-resolution
```

The bridge writes the decimal bridge window handle to the ready file after the first healthy visible present. The proxy verifies that the handle is still a visible window owned by the bridge child process before forwarding activation.

## Notes for release

Keep this source separate from the installed Steam folder until the integration verifier confirms a real Lossless Scaling end-to-end run. Publishing should include this proxy source, build instructions, install/uninstall scripts, attribution for referenced MIT sources, and a clear note that users must supply the NVIDIA DLSS runtime files themselves where licenses require it.
