# D3D12 FidelityFX DLSS-NR Probe

This is an isolated D3D12 test harness for the `UseFsrInputs=1` path in the AMD DLSS-NR proxy. The older swapchain probe only presented an app-owned backbuffer. This probe creates a real AMD FidelityFX upscaler context, dispatches it each frame with color, depth, motion-vector, exposure, and output resources, then presents the upscaled output.

The runner has two phases:

1. `fsr-only` loads `amd_fidelityfx_loader_dx12.dll` and `amd_fidelityfx_upscaler_dx12.dll` directly. This proves the native AMD FSR API contract is valid before involving the proxy.
2. `proxy-fsr-inputs` also loads the local `version.dll` proxy and writes `UseFsrInputs=1` in `dlssnr_on_amd.ini`. This is the run that can prove whether the proxy observes a real FSR dispatch and starts DLSS-NR network jobs.

The script extracts only these AMD runtime DLLs from a local OptiScaler archive into ignored per-run folders:

- `amd_fidelityfx_loader_dx12.dll`
- `amd_fidelityfx_upscaler_dx12.dll`

It does not extract NVIDIA DLLs, OptiScaler DLLs, or model/runtime binaries into source.

## Build-Only Check

```powershell
powershell -ExecutionPolicy Bypass -File .\run_probe.ps1 -SkipExecution -Frames 2 -Seconds 1
```

## Bounded Runtime Check

Run this only when global overlays such as Special K are disabled for a clean GPU test:

```powershell
powershell -ExecutionPolicy Bypass -File .\run_probe.ps1 -FsrOnly -Frames 700 -Seconds 25
```

If the FSR-only sanity pass succeeds, run the proxy path:

```powershell
powershell -ExecutionPolicy Bypass -File .\run_probe.ps1 -Frames 700 -Seconds 25
```

Use `-Visible` for a visible window. Each per-run folder contains `SpecialK.deny.dlssnr_fsr_probe` and `SpecialK.deny.dlssnr_fsr_probe.exe`. Each launched probe has an external watchdog of `-Seconds + 20`; a timeout writes `timeout-report.txt` and terminates only the launched probe process.

Use `-VersionSource` and `-NrSource` to point at alternate local proxy DLLs:

```powershell
powershell -ExecutionPolicy Bypass -File .\run_probe.ps1 -VersionSource C:\path\to\version.dll.bak -NrSource C:\path\to\nvngx_dlssnr.dll
```

## Evidence

`runs/<timestamp>/compare-summary.txt` combines:

- `report.txt` from the FSR-only run.
- `report.txt` from the proxy run.
- the DLSS-NR proxy log tail, if the proxy generated one.
- the FidelityFX debug log tail, if the SDK emitted one.

Success requires all of these:

- FSR-only run reports nonzero `ffx_dispatch_ok_count` and zero `ffx_dispatch_fail_count`.
- Proxy run reports nonzero `ffx_dispatch_ok_count` and zero `ffx_dispatch_fail_count`.
- Proxy log shows completed DLSS-NR network jobs, not just hook installation.
- Captures show a pixel difference outside overlays.

Reject the run if any GPU stage reports errors such as invalid kernel files, if the output is fully black or all zeroes, if the watchdog fires, or if the proxy log only proves hooks loaded. A completed job plus a pixel difference is not enough when the GPU output is invalid.

## Upstream Headers

The headers under `third_party/FidelityFX-SDK` are a minimal source snapshot from:

https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK

Pinned source revision used for this probe:

```text
60f4ea81909200d8542eca14dccb2628b763a9a3
```

The copied AMD headers include AMD's MIT-style permission notice in each file. The upstream third-party notice file is kept at `third_party/FidelityFX-SDK/3rdpartynotice.md`.
