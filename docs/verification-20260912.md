# Direct Game verification — 12 September 2026

I verified the direct-game route for Crimson Desert on the development machine following the game's latest patch to determine whether upstream Neural Rendering remains functional.

## Summary

On 12 September 2026, I re-tested Crimson Desert on the development machine following its 11 September 2026 Steam update (buildid 25246367). Although the official DLSS-NR-on-AMD runtime loads into the game executable, Neural Rendering activity cannot start because three required render detours fail during initialization. This is an upstream compatibility break introduced by the updated game binary rather than a defect in this manager. Because the runtime is a closed upstream binary, this project cannot patch the detour failures directly; instead, I updated DLSS5 AMD Swapper to parse fresh runtime sessions, detect failed render hooks, and report the stalled state truthfully in the UI and CLI.

## Evidence

I tested the updated game build on the development machine under two controlled launch conditions:

| Test | Configuration | Result |
| --- | --- | --- |
| Launch test A | Proxy removed (clean `<game folder>\bin64`) | The game started and stayed running for the full 100 s watch window, window title "Crimson Desert". |
| Launch test B | Official DLSS-NR-on-AMD v0.2.17 proxy as `winmm.dll`, rich config (`Enabled=1`, `UseFsrInputs=1`, `UseDepth=1`, `Temporal=1`, `Interop=1`, `Inline=1`) | The game started and stayed running for 100 s, but the fresh runtime session in `dlssnr_on_amd.log` recorded three failed render detours and stalled before neural engine initialization. |

The sanitized log excerpt from a fresh launch session shows the exact point of failure:

```text
dlssnr_amd v0.2.17 (build 976a3fa0) loaded into CrimsonDesert.exe as winmm.dll from <game folder>\bin64\; log <game folder>\bin64\dlssnr_on_amd.log; settings <game folder>\bin64\dlssnr_on_amd.ini
detour of ID3D12CommandQueue::ExecuteCommandLists failed (5)
hooked IDXGIFactory2::CreateSwapChainForHwnd
detour of IDXGIFactory::CreateSwapChain failed (5)
detour of IDXGISwapChain::Present failed (5)
hooked IDXGISwapChain1::Present1
dlssnr_amd v0.2.17 (build 976a3fa0) loaded into crashpad_handler.exe as winmm.dll from <game folder>\bin64\; log <game folder>\bin64\dlssnr_on_amd.log; settings <game folder>\bin64\dlssnr_on_amd.ini
hooked ID3D12CommandQueue::ExecuteCommandLists
hooked IDXGIFactory2::CreateSwapChainForHwnd
hooked IDXGIFactory::CreateSwapChain
hooked IDXGISwapChain::Present
hooked IDXGISwapChain1::Present1
swapchain 0000000071031300 created on queue 0000000070A75760 (device 0000000070776F40)
swapchain 000000010F3E1170 created on queue 00000000DD119140 (device 0000000070776F40)
```

After the two swapchain creation lines, nothing further is logged:
- No `device ... (from the first presented swapchain)`
- No `present queue`
- No `engine init ok`
- No `first ffxDispatch`
- No `network job` lines

The last runtime sessions with verified Neural Rendering activity (`engine init ok`, `first ffxDispatch`, network job lines) date from 2026-09-09, prior to the 2026-09-11 update. Earlier sessions today with upstream v0.2.18 (build b7e0bab9) and with the proxy named `version.dll` or `dxgi.dll` show the same three failed detours.

### Process mitigation check

I checked the process mitigation policy of the running game using `Get-ProcessMitigation`:
- `BlockDynamicCode`: OFF
- `ControlFlowGuard` (CFG): OFF
- `UserShadowStack`: OFF
- `ExtensionPoints`: not disabled
- `DEP`: ON

### Environment stability

The environment outside the game build did not change:
- Development machine: RX 9070 XT, AMD driver 32.0.31041.1004 unchanged since 2026-08-17, Windows 11 build 26200.
- Steam client files: unchanged since 2026-09-03.
- Game directory: only `CrimsonDesert.exe` changed in `<game folder>\bin64`.

Because the OS, display driver, Steam client, and proxy configuration were identical to the verified 2026-09-09 runs, the change that broke the render hooks is the updated game executable itself.

## What this means

- **Upstream runtime vs game build**: The upstream runtime relies on detouring DirectX 12 and DXGI presentation calls. In the 2026-09-11 game build, `ExecuteCommandLists`, `CreateSwapChain`, and `Present` fail to detour (error code 5). Because the runtime is a closed upstream binary, this project cannot modify or recompile it.
- **Both v0.2.17 and v0.2.18 affected**: The detour failures reproduce consistently on both upstream v0.2.17 and v0.2.18 across all tested proxy names (`winmm.dll`, `version.dll`, `dxgi.dll`).
- **OptiScaler pre-SR proxy blocked**: The OptiScaler AMD pre-SR v1.2 proxy (SHA-256 `07a1e2ca3fbf6c9c9a2923a755603c69fabf115b0904c92f10efe95fdb2b0caa`) causes Crimson Desert to exit during startup with access-violation faults logged by the runtime. DLSS5 AMD Swapper blocks that specific proxy build for `CrimsonDesert.exe` and migrates such installs back to the official post-FSR route.
- **Pinned runtime version**: For `CrimsonDesert.exe`, the post-FSR setup pins upstream v0.2.17 (7,538,418 bytes, SHA-256 `4fcd167d07bc4964eaf9162aa8f4f11e852b91bf866b28cb48d45934022440bc`), validated against the GitHub release tag metadata, because v0.2.17 was the last version with log-verified neural activity on the previous build. Every other game uses the latest release. However, pinning v0.2.17 does not make the current game build work.
- **No performance claim**: I make no performance claims for this configuration. While the game runs smoothly without crashes, Neural Rendering does not execute.

## What the manager now reports

I updated the manager and CLI diagnostics to enforce a strict rule: fresh session evidence only. Having `Enabled=1` in the configuration file or high rendering performance is not proof of Neural Rendering activity. Only logged engine initialization, FidelityFX dispatch, and network job executions constitute proof.

The updated manager behavior includes:

- **Session-scoped diagnostics**: Only the newest `dlssnr_amd ... loaded into <game exe>` session is analyzed. Older successful sections from earlier launches can no longer make a failed launch appear active.
- **Render hook failure reporting**: When detour failures are detected without engine initialization, the diagnostics report:
  `AMD runtime <version> loaded, but N render hook(s) failed to install (<failed hooks>); no frame reached the runtime, so Neural Rendering cannot activate in this launch`.
- **Truthful status indicators**:
  - `Neural engine stalled - render hooks failed this launch`
  - `Neural engine stalled - effect inactive this launch`
  - `Neural engine initialized - waiting for FidelityFX`
  - `FidelityFX hooked - waiting for neural jobs`
  - `Runtime loaded - Neural Rendering not active yet`
  - `Neural Rendering active - log verified`
- **Single-flight evidence refreshes**: The automatic evidence inspection is guarded against overlapping calls, preventing concurrent duplicate inspections.
- **Explicit on/off state saving**: Pressing `Ctrl+Alt+F6` toggles and saves the on/off setting in `dlssnr_on_amd.ini` and displays a toast confirming that the file was saved. Because the post-FSR runtime has no external live-toggle API, the in-game `End` overlay remains the authoritative live control.
- **Unmanaged proxy conflict guard**: Install and update workflows refuse to proceed if an unexpected third-party proxy DLL is present alongside a managed install.
- **OptiScaler package validator enhancements**: The validator accepts `dlss-enabler-headless.dll` and a real `dlssnr_on_amd_weights.bin` placed beside a package folder whose name starts with `OptiScaler-AMD-PreSR-Multipass`.
- **Extended diagnostics export**: Exported reports include `hookFailures`, `failedHooks`, `swapchainsCreated`, `hooksInstalled`, `engineInitialized`, `fidelityFxDispatchObserved`, `presentQueueObserved`, and `startupStalled`.
- **CLI parity**: The CLI `--diagnose` command outputs matching session-scoped fields (`session_scoped`, `runtime_version`, `engine_initialized`, `hook_failures`, `failed_hooks`, `swapchains_created`, `hooks_installed`, `hooks_failed`, `present_queue_observed`, `startup_stalled`).

## How to re-test after a game or runtime update

When a new Crimson Desert patch or DLSS-NR-on-AMD runtime release becomes available, re-test using these steps:

1. Launch Crimson Desert from Steam.
2. Wait one minute to ensure the game has finished loading and presenting frames.
3. Open DLSS5 AMD Swapper and click **Refresh evidence** on the Crimson Desert entry.
4. Alternatively, run the direct-game CLI diagnostic:
   ```powershell
   py .\direct-game\amd_dlss5.py --game "<game folder>\bin64\CrimsonDesert.exe" --diagnose
   ```
5. Check the `runtime_log` object: Neural Rendering is only verified when `session_scoped` is true, `hooks_failed` is false, `engine_initialized` and `fidelityfx_dispatch_detected` are true, and `timed_job_samples` is greater than zero in that same session.

## Reporting upstream

If you want to report this compatibility break to the upstream project, you can attach the sanitized log excerpt above to an issue on the [DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD) repository. I have not filed an upstream issue myself.
