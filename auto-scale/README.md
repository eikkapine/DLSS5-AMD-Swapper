# NR Auto Scale proxy

This folder builds a native `Lossless.dll` proxy that lets Lossless Scaling scale the DLSS-NR bridge window automatically instead of asking the user to choose the bridge manually.

The default path processes within 1280×720 bounds, preserves source aspect ratio, and lets Lossless Scaling upscale the bridge output. Native source-size processing and 1:1 presentation remain available with `NativeResolution=1`.

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
NativeResolution=0
Width=1280
Height=720
StartupDelayMs=2000
WarmupFrames=320
ReadyTimeoutMs=180000
DefaultScalingTypeIfOff=1
ForceCaptureApi=1
```

Relative paths resolve from the folder containing `Lossless.dll`.

The proxy applies only the settings needed for the bridge target before it calls the real `Activate`: resize-before-scaling off, clip cursor off, multi-display mode on, and WGC capture (`ForceCaptureApi=1`). The default `NativeResolution=0` uses `Width` and `Height` and preserves the selected scaler. If the scaler is Off, `DefaultScalingTypeIfOff=1` selects LS1. With `NativeResolution=1`, the proxy forwards `--native-resolution` and requests 1:1 presentation: custom scaling mode, scaling type off, and scale factor `1.0`.

`CaptureDirectory` and `FreezeSource` are private diagnostics for verification runs. Leave both at their defaults for normal use. When `CaptureDirectory` is non-empty, the proxy forwards it as `--capture-dir` so the bridge can save comparison frames and its report. When `FreezeSource=1`, the proxy forwards `--freeze-source` so the bridge can reuse the first valid captured source frame for same-frame comparison.

## IPC contract with the bridge

The proxy starts:

```text
DlssNrBridge.exe --source-hwnd 0x... --width 1280 --height 720 --startup-delay-ms 2000 --warmup-frames 320 --ready-file <ready.txt> --stop-event Local\DlssNrStop-<pid>-<generation> --parent-pid <pid>
```

The bridge writes the decimal bridge window handle to the ready file after the first healthy visible present. The proxy verifies that the handle is still a visible window owned by the bridge child process before forwarding activation.

## Notes for release

Keep this source separate from the installed Steam folder until the integration verifier confirms a real Lossless Scaling end-to-end run. Publishing should include this proxy source, build instructions, install/uninstall scripts, attribution for referenced MIT sources, and a clear note that users must supply the NVIDIA DLSS runtime files themselves where licenses require it.
