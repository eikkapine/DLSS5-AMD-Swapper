# Bridge

`DlssNrBridge.exe` is the capture, neural-feed, live-blend and presentation process used by NR Auto Scale.

It captures a normal visible source window through Windows Graphics Capture, feeds the external D3D12 DLSS-NR compatibility runtime, and exposes a visible output window that Lossless Scaling can scale/present.

## Live hotkeys

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle original / processed output |
| `Ctrl+Alt+F7` | Reduce output blend by `0.1` |
| `Ctrl+Alt+F8` | Increase output blend by `0.1` |

## Build

```powershell
.\bridge\build.ps1
```

or:

```powershell
cmake -S .\bridge -B .\bridge\build -G "Visual Studio 17 2022" -A x64
cmake --build .\bridge\build --config Release --target DlssNrBridge
```

## Useful command-line options

- `--source-hwnd` selects a source window by HWND.
- `--source-title` selects a visible window by partial title.
- `--working-scale 0.75` derives the neural working size from the source.
- `--native-resolution` uses the captured source size directly when working scale is disabled.
- `--width` / `--height` set fixed bounds when both source-relative and native modes are off.
- `--ready-file` publishes the validated visible bridge HWND for the auto-scale proxy.
- `--stop-event` lets the wrapper stop the bridge cleanly.
- `--parent-pid` ends the bridge when its owning process exits.
- `--cpu-transport` disables the shared-GPU transport path.
- `--max-fps` applies an explicit feed limit.
- `--hip-kernel-timing` enables bounded kernel timing diagnostics.
- `--no-hip-kernel-timing` explicitly keeps those diagnostics disabled.
- `--no-hip-timing` disables HIP host observation/feed hints too.
- `--no-proxy` runs capture/presentation without loading the external neural proxy.

Diagnostic-only switches such as `--capture-dir` and `--freeze-source` are not needed for normal use.

## GPU path

The normal live path shares textures/fences between the D3D11 capture/presentation device and the D3D12 neural device.

When source and neural dimensions match, input conversion uses exact texel loads. When `WorkingScale` intentionally chooses a smaller neural size, the bridge samples the source with a hardware bilinear shader directly into the shared neural input texture. Shader-capable WGC surfaces are read directly; unsupported surfaces use a source-sized GPU copy before the same resize stage.

A 16-byte status readback carries change/non-black information. Accepted full-strength output is retained across two alternating shared output slots so the common full-strength path avoids an additional full-frame history copy.

Normal performance builds leave bounded HIP kernel sampling off. The existing host timing/work-progress hooks remain available for inference-aware feed pacing.

## Runtime files

Proxy mode expects external runtime files beside the bridge executable, including the user-provided compatibility proxy and NVIDIA Neural Rendering DLL. They are not distributed by this repository.

See [Architecture](../docs/architecture.md), [Performance](../docs/performance.md), and [Verification](../docs/verification.md).
