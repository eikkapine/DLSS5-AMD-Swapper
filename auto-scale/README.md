# NR Auto Scale proxy

This folder builds the project-owned `Lossless.dll` proxy that makes the bridge start automatically when Lossless Scaling activates a source.

The current default keeps the visible bridge at the captured source resolution and reduces only the neural workload:

```ini
NativeResolution=0
WorkingScale=0
NeuralMaxHeight=480
```

For a 2560×1440 source, the visible bridge stays 2560×1440 while the neural branch is about 854×480. `NeuralMaxHeight` takes precedence over the legacy `WorkingScale` and fixed-size controls.

The proxy loads the privately preserved `Lossless_original.dll`, forwards the original exports, and wraps `Init`, `ApplySettings`, `Activate`, and `UnInit`. When scaling starts it launches the bridge, waits for the bridge to publish a healthy visible output HWND, then forwards activation to the original Lossless Scaling DLL using that bridge window.

It does not inject the bridge into the source application and does not redistribute or load the AMD/NVIDIA runtime files from the public package.

## Build

```powershell
.\build.ps1
```

The main artifact is `build\Release\Lossless.dll`.

## Runtime config

`NrAutoScale.ini` lives beside the installed proxy DLL. The relevant defaults are:

```ini
[AutoScale]
Enabled=1
BridgeExe=nr-bridge\runtime\DlssNrBridge.exe
RuntimeDirectory=nr-bridge\runtime
HipVisibleDevices=1
NativeResolution=0
WorkingScale=0
NeuralMaxHeight=480
Width=1280
Height=720
StartupDelayMs=2000
WarmupFrames=320
ReadyTimeoutMs=180000
DefaultScalingTypeIfOff=1
ForceCaptureApi=1
```

Relative paths resolve from the folder containing `Lossless.dll`.

Resolution precedence is:

1. `NeuralMaxHeight > 0`: keep visible output at source size and cap only the neural height.
2. `WorkingScale=0.25..0.998`: legacy reduced source-relative neural/output path.
3. `NativeResolution=1`: full 1:1 neural processing.
4. Otherwise `Width`/`Height` are fixed fallback bounds.

The proxy preserves the user's selected Lossless Scaling scaler and frame-generation settings. It changes only the target/capture settings required to scale the bridge window.

## IPC contract

A normal dev.14 launch includes the source handle and `--neural-max-height 480`. The bridge writes its visible HWND to the ready file only after the runtime health gate passes. The proxy verifies that HWND belongs to the bridge process before calling the original `Activate`.

## Release boundary

The public source/release may contain the project-built proxy and bridge. It must not contain the paid Lossless Scaling original DLL, `Lossless_original.dll`, NVIDIA runtime/model files, AMD proxy binaries, private INIs, logs, backups or machine-specific paths.
