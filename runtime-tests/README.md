# Runtime integration checks

Run from PowerShell on Windows with the existing .NET 8 SDK, CMake and MSVC x64 tools:

```powershell
.\runtime-tests\Run-RuntimeTests.ps1
.\runtime-tests\Run-RuntimeTests.ps1 -Package 'C:\path\OptiScaler-AMD-PreSR-Multipass-v1.2' -Weights 'C:\path\dlssnr_on_amd_weights.bin'
.\runtime-tests\Run-RuntimeTests.ps1 -Package 'C:\path\OptiScaler-AMD-PreSR-Multipass-v1.2' -Weights 'C:\path\dlssnr_on_amd_weights.bin' -OfficialSetup 'C:\path\dlssnr_on_amd_setup.exe' -NeuralDll 'C:\path\nvngx_dlssnr.dll' -RequireNeural
```

The first command builds and runs a D3D11 shader triangle and the existing D3D12 deterministic GPU readback probe. The second also uses **the application's actual `GameProbeService`, `OptiScalerPackageService`, `OptiScalerInstallerService` and diagnostics**, with real locally supplied binaries, for install, update, FFX input dispatch and restore. No payloads are downloaded or bundled.

Each run creates fresh synthetic game folders below `runtime-tests/runs/`. An existing nonempty output directory is rejected. Windows stay hidden and never receive focus; there is no mouse or keyboard automation. Each child process has a 60 second timeout and the harness stops its own process tree on expiry. GPU captures and logs are retained for diagnosis; binary and capture outputs are gitignored.

`summary.json` separates rendering, proxy loading, FFX dispatch and neural evaluation. Successful rendering and loading do **not** prove that the neural model ran. The pre-SR neural check requires a completed AMD pass and the application's current-session `PreSrActive` diagnostic. An unverified result is reported explicitly and is not a neural-success claim. Image hashes compare the same synthetic input frame, but image differences alone do not identify the responsible component. Rendering, process-exit, installer and restore failures cause a nonzero exit code. `-RequireNeural` also fails unverified neural checks. `-OfficialOnly` skips the already independent baseline/pre-SR checks when investigating the official route.

The official checks use the application's `DirectGameInstallerService` with an upstream-hash-verified setup, then check real FFX input dispatch and neural diagnostics before update and restore. The official setup runs from an isolated temporary directory with its explicit target argument: launching it beside an installed `dxgi.dll` makes setup load that proxy itself, preventing update. No payload is patched. This target argument was also checked with the supported v0.2.17 and v0.2.18 installers.

The D3D11 probe validates real native GPU drawing/readback, then checks that the current D3D12-only AMD route rejects its unsupported target without file changes. Vulkan integration is explicitly skipped: the current neural route requires D3D12. Hidden-window `Present` may be occluded; readback verifies GPU work rather than visible presentation. The synthetic FFX inputs are suitable for dispatch/load testing, not temporal reconstruction quality assessment in a game.

## Observed integration result (2026-09-13)

On an RX 9070 XT, the complete run produced **19 passing checks, one failure, and one Vulkan skip**. The OptiScaler v1.2 route completed one and two neural passes, reported current activity even without timing samples, disabled dispatch when switched off, reproduced identical disabled GPU output, and changed output when enabled. Install, update and restore passed. Special K was absent from the probe's module lists; it is not a dependency.

The official v0.3.0 route passed verified install, update, restore and neural evaluation. Its real FFX path rendered 180 frames, used zero-copy color/depth/motion/exposure inputs, and logged completed network jobs at render resolution. Its asynchronous hook startup missed this tiny probe's swapchain with a two-second delay; the harness allows ten seconds before graphics creation. This is a test initialization allowance, not a fix to the upstream hook bootstrap.

**The official runtime still failed normal process shutdown with `0xC0000409`, subcode 7 (`FAST_FAIL_FATAL_APP_EXIT`).** A debugger trace located the failure inside the upstream proxy during `LdrShutdownProcess`, after the probe returned from its normal rendering and resource cleanup. The harness retains this failed check and returns nonzero; it does not suppress the crash. This external-runtime limitation was reproduced independently of the Swapper's WPF shutdown fixes. The synthetic results do not establish Assetto Corsa Rally or Cyberpunk gameplay compatibility or image quality.

The local run report and raw logs are retained under the ignored `runs/final-integration-20260913-01/` directory. They are not part of source or release packages.
