# Auto-scale proxy

The auto-scale component builds the project-owned `Lossless.dll` wrapper used to start/stop `DlssNrBridge.exe` around a normal Lossless Scaling scaling session.

The wrapper loads the privately preserved original DLL (`Lossless_original.dll`), forwards the required exports, and wraps the activation flow so Lossless Scaling targets the bridge output automatically.

It does not inject the bridge or external neural runtime into the source application.

## Build

From the repository root:

```powershell
.\auto-scale\build.ps1
```

The main output is `auto-scale\build\Release\Lossless.dll`.

## Configuration

`NrAutoScale.ini` sits beside the installed wrapper.

Fresh setup currently uses:

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

`HipVisibleDevices` is machine-specific. The interactive setup asks for the AMD HIP device index instead of assuming the same GPU number works everywhere.

With `NativeResolution=0`, the bridge uses the fixed bounds and Lossless Scaling keeps the selected scaler. With `NativeResolution=1`, the wrapper starts the bridge in native mode and requests 1:1 geometry.

## Activation flow

```text
Lossless Scaling Activate(source HWND)
        │
        ▼
project Lossless.dll
        │
        ├─ start DlssNrBridge.exe
        ├─ wait for ready file
        ├─ validate bridge HWND/process
        ▼
original Lossless.dll Activate(bridge HWND)
```

The wrapper keeps the current frame-generation settings when forwarding profile changes and reapplies only the capture/geometry choices needed for the bridge target.

## Setup / uninstall

Source-tree setup:

```powershell
.\auto-scale\scripts\Setup.cmd
```

Release packages expose the same setup entry point at the ZIP root as `Setup.cmd`.

Uninstall uses the manifest/backups created during setup to restore the original local DLL. Close Lossless Scaling before installing or uninstalling.

See [Installation](../docs/install.md) for the main setup flow.
