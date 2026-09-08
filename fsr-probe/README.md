# FidelityFX input probe

This standalone D3D12 harness checks the `UseFsrInputs=1` route of the external AMD DLSS-NR compatibility proxy.

It creates a real FidelityFX upscaler context, supplies color/depth/motion/exposure resources, dispatches it, and then checks whether the proxy can observe that path and produce neural work.

## Build-only check

```powershell
powershell -ExecutionPolicy Bypass -File .\fsr-probe\run_probe.ps1 -SkipExecution -Frames 2 -Seconds 1
```

## Runtime check

Run the FidelityFX-only pass first:

```powershell
powershell -ExecutionPolicy Bypass -File .\fsr-probe\run_probe.ps1 -FsrOnly -Frames 700 -Seconds 25
```

Then run the proxy path:

```powershell
powershell -ExecutionPolicy Bypass -File .\fsr-probe\run_probe.ps1 -Frames 700 -Seconds 25
```

`-Visible` creates a visible probe window. `-VersionSource` and `-NrSource` can point at alternate private runtime files for local testing.

## What a useful result looks like

- FidelityFX dispatches succeed in the FSR-only pass.
- FidelityFX dispatches also succeed with the proxy loaded.
- The proxy log shows completed neural jobs, not only hook installation.
- Captured output is valid and visibly/pixel-wise different from the source.
- No GPU/runtime error or watchdog timeout is present.

Run output is stored under ignored local `runs\` folders.

## FidelityFX headers

The minimal header snapshot under `third_party/FidelityFX-SDK` comes from:

<https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK>

Pinned revision:

```text
60f4ea81909200d8542eca14dccb2628b763a9a3
```

The upstream notices are kept with the vendored headers. See [Licensing](../docs/licensing.md) for the public-package rules.
