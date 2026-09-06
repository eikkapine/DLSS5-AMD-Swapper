# D3D12 DLSS-NR-on-AMD Probe

This is a small isolated renderer used to test a private AMD/NVIDIA runtime path without modifying the Lossless Scaling installation.

The probe uses a generated deterministic source pattern. Do not use Windows wallpaper images, movie frames, browser screenshots, or user desktop captures as public fixtures.

## What It Does

`run_probe.ps1` builds the executable, creates private per-run directories, copies the local runtime files needed by the test, and writes a per-run `dlssnr_on_amd.ini`.

Then it runs:

- `off` with `Enabled=0`.
- `on` with `Enabled=1`, `UseFsrInputs=0`, `Inline=0`, and stronger structure settings.

Each run writes `report.txt`, `expected.ppm`, and captured swapchain buffers under `runs\<run-id>\off` and `runs\<run-id>\on`.

The useful signal is in `compare-summary.txt` and `evidence.json`. These files report whether the proxy log appeared, whether Present was occluded, which render-hook modules were mapped, whether neural jobs completed, and whether captured presented buffers differ from the known static source pattern.

Run output is private test evidence and must stay out of the public repository.

## Run

From the repository root:

```powershell
.\probe\run_probe.ps1 -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1
```

Use at least 700 frames and 25 seconds. The probe samples late frames after the initial idle period, including frame 300 and later captures.

For a visible-window test of the exact desktop Present path:

```powershell
.\probe\run_probe.ps1 -Visible -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1
```

If visible/default settings hook but do not produce a backbuffer job, run one controlled interop variant:

```powershell
.\probe\run_probe.ps1 -Visible -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1 -Interop 1
```

For a synchronous same-frame neural path check, run one controlled inline variant:

```powershell
.\probe\run_probe.ps1 -Visible -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1 -Inline 1
```

The runner defaults to a bounded startup delay after loading the proxy and before creating the D3D12 device. Override it only when hook timing needs a different value:

```powershell
.\probe\run_probe.ps1 -Visible -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1 -StartupDelayMs 1000
```

On systems where the proxy initializes but the HIP kernel path selects the wrong AMD device, set HIP visibility only for the launched probe process:

```powershell
.\probe\run_probe.ps1 -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1
```

This is a local child-process environment override. The runner does not call `setx` and does not change the system or user environment.

Validate an existing run without building or launching the GPU probe:

```powershell
.\probe\run_probe.ps1 -ValidateRun .\probe\runs\<run-id>
```

## Accepted Standalone Evidence

Accepted standalone probe evidence currently available showed:

```text
HIP_VISIBLE_DEVICES=1 was set only in the child process.
Startup delay after proxy load was long enough for hook initialization.
Engine initialization was observed.
About 400 neural jobs completed.
Approximate job time was 15-16 ms/job.
Self-check zero-output rate was 0.132%, accepted as healthy.
Off image matched the generated source exactly.
On image was nonblack.
On RGB mean absolute difference was 8.89.
On max channel delta was 55.
```

This proves the isolated D3D12 runtime path for the tested configuration. It does not verify the full Lossless Scaling app.

## Acceptance Rules

`run_probe.ps1` writes `evidence.json` and exits `2` when validation fails.

A standalone candidate requires:

- Completed neural jobs.
- No GPU, fault, crash, or zero-output error markers.
- `off` readied-buffer delta equal to `0`.
- Late `off` captures matching `expected.ppm`.
- `on` readied-buffer delta greater than `0`.
- Late `on` PPM payloads that are nonzero and nonconstant.

Reject the result if the output is black, constant, missing, mismatched, or backed only by a loaded DLL or changed INI value.

## Release Boundary

Release acceptance requires more than a passing standalone probe. The real Lossless Scaling Scale button must start the native path automatically, and the final comparison must show a visible on/off difference on a static desktop image.

The requested native integration is not an upscaler. Final validation must use native WGC resolution and keep Lossless Scaling geometry resampling off, or use custom scale factor `1`.

Native 1440p performance has not been tested yet. Do not make a native-1440p performance claim until that test exists.
