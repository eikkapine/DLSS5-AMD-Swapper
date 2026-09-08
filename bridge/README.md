# Bridge

`DlssNrBridge.exe` is the capture, neural-feed, live-blend, and presentation process used by NR Auto Scale.

It captures a normal visible source window through Windows Graphics Capture, feeds the external D3D12 DLSS-NR compatibility runtime, and exposes a visible output window that Lossless Scaling can scale/present.

## Live hotkeys

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle original / processed output |
| `Ctrl+Alt+F7` | Reduce output blend by `0.1` |
| `Ctrl+Alt+F8` | Increase output blend by `0.1` |

Blend strength is clamped to `0.0–1.0`.

## Build

From the repository root:

```powershell
.\bridge\build.ps1
```

or with CMake directly:

```powershell
cmake -S .\bridge -B .\bridge\build -G "Visual Studio 17 2022" -A x64
cmake --build .\bridge\build --config Release --target DlssNrBridge
```

The production executable is normally written to `bridge\build\Release\DlssNrBridge.exe`.

## Useful command-line options

```powershell
.\DlssNrBridge.exe --list-windows
.\DlssNrBridge.exe --source-title "window title" --width 1280 --height 720
.\DlssNrBridge.exe --source-hwnd 0x123456 --native-resolution
```

Key options:

- `--source-hwnd` selects a source window by HWND.
- `--source-title` selects a visible window by partial title.
- `--native-resolution` uses the captured source size directly.
- `--width` / `--height` set fixed processing bounds when native mode is off.
- `--ready-file` publishes the validated visible bridge HWND for the auto-scale proxy.
- `--stop-event` lets the wrapper stop the bridge cleanly.
- `--parent-pid` ends the bridge when its owning process exits.
- `--cpu-transport` disables the shared-GPU transport path.
- `--max-fps` applies an explicit feed limit.
- `--no-hip-timing` disables HIP host observation/feed hints.
- `--no-proxy` runs capture/presentation without loading the external neural proxy.

Diagnostic-only switches such as `--capture-dir` and `--freeze-source` are intended for controlled comparison runs and are not needed for normal use.

## GPU path

The native GPU path shares textures/fences between the D3D11 capture/presentation device and the D3D12 neural device. It keeps full-resolution images on the GPU through conversion, neural handoff, exact RGB checks, blend/direct selection, and visible presentation where the driver path allows it.

The project keeps exact RGB duplicate detection and the same byte-domain blend math. It does not use approximate matching or lower-resolution filters as part of the performance path.

## Runtime files

Proxy mode expects these external files beside the bridge executable:

- `version.dll`
- `nvngx_dlssnr.dll`
- `dlssnr_on_amd.ini`

They are not distributed by this repository. The normal installer copies user-supplied versions into the private local runtime folder.

See [Architecture](../docs/architecture.md), [Performance](../docs/performance.md), and [Verification](../docs/verification.md) for the higher-level picture.
