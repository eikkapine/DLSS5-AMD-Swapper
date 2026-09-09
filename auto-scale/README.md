# Auto-scale proxy

The auto-scale component builds the project-owned `Lossless.dll` wrapper used to start/stop `DlssNrBridge.exe` around a normal Lossless Scaling scaling session.

The wrapper loads the privately preserved original DLL (`Lossless_original.dll`), forwards the required exports, and redirects activation to the bridge output. It does not inject the bridge or external neural runtime into the source application.

## Build

```powershell
.\auto-scale\build.ps1
```

The output is `auto-scale\build\Release\Lossless.dll`.

## Configuration

`NrAutoScale.ini` sits beside the installed wrapper. New installs use:

```ini
[AutoScale]
Enabled=1
BridgeExe=nr-bridge\runtime\DlssNrBridge.exe
RuntimeDirectory=nr-bridge\runtime
CaptureDirectory=
HipVisibleDevices=1
FreezeSource=0
NativeResolution=0
WorkingScale=0.75
Width=1280
Height=720
StartupDelayMs=2000
WarmupFrames=320
ReadyTimeoutMs=180000
DefaultScalingTypeIfOff=1
ForceCaptureApi=1
```

`WorkingScale=0.75` is the current performance preset. It takes 75% of each captured source axis for the neural working size and preserves the selected Lossless Scaling scaler for the final enlargement. `WorkingScale=0` disables this source-relative path.

With `WorkingScale=0`, `NativeResolution=1` requests 1:1 processing; `NativeResolution=0` uses `Width` / `Height` as the fixed fallback.

Missing `WorkingScale` keeps an older existing installation on its previous behavior instead of silently migrating it.

`HipVisibleDevices` is machine-specific. Interactive setup asks for the AMD HIP device index instead of assuming the same GPU number works everywhere.

## Activation flow

```text
Lossless Scaling Activate(source HWND)
        │
        ▼
project Lossless.dll
        │
        ├─ start DlssNrBridge.exe
        ├─ forward WorkingScale / geometry
        ├─ wait for ready file
        ├─ validate bridge HWND/process
        ▼
original Lossless.dll Activate(bridge HWND)
```

The wrapper keeps the current frame-generation settings when forwarding profile changes and reapplies only the capture/geometry choices needed for the bridge target.

## Setup / uninstall

```powershell
.\auto-scale\scripts\Setup.cmd
```

Release packages expose the same setup entry point at the ZIP root as `Setup.cmd`. Uninstall uses the local manifest/backups created during setup to restore the original private DLL.

See [Installation](../docs/install.md) for the main setup flow.
