# Installed neural performance update — 7 September 2026

The next normal Scale activation uses the upstream AMD runtime v0.2.15 and a rebuilt bridge with scoped scheduling requests. This is an installed candidate for the user's manual test, not a measured speedup.

## Runtime update

The installed neural runtime was updated from v0.2.12 to v0.2.15. Upstream v0.2.13 reports a 2% performance gain and fixes to a disabled optimization and CPU-copy fallback. Later releases include compatibility fixes and a speculative queue-stall fix. These release notes are not measurements of this bridge.

The official release installer was downloaded and matched GitHub's published SHA-256. Its embedded x64 version-proxy DLL was extracted as file data; the installer was not executed. Static inspection confirmed the version exports, gfx1201 kernels, existing configuration key names and availability of all required HIP imports in the installed driver DLL. Runtime compatibility still requires manual playback.

## Bridge scheduling

Static inspection finds an idle `Sleep(1)` path in both runtime versions (v0.2.12 call RVA 0xcb22; v0.2.15 call RVA 0xebf8). Before loading the neural DLL, the bridge now requests the minimum supported timer period, normally 1 ms, and opts its own process out of execution-speed and timer-resolution throttling. The latter preserves its timer request when Lossless Scaling covers its window.

This targets wake-up and CPU-dispatch latency. It does not rewrite kernels, bypass GPU fences, suppress presentations, cap inference, or make the game wait for inline inference. In particular, the old 63/78/94 ms samples are quantized wall-clock readings: they do not prove that timer sleeps account for those durations or predict this change's gain.

The request is scoped to the bridge lifetime, with matching timer release and prior controlled policy restoration on orderly exit. Unsupported requests are nonfatal. `bridge-scheduling.log` records accepted timer period, policy success and error codes at the next manual activation. `--default-scheduling` opts out for a standalone manual comparison. No driver, global power plan, process priority class or security setting is changed.

## Installed files and checks

| File | SHA-256 |
| --- | --- |
| `nr-bridge/runtime/version.dll` | `8230DD8B4687914AD95FE1FC4D9DF7AADFCA8243D7DCDED87B02BA34DBFDE592` |
| `nr-bridge/runtime/DlssNrBridge.exe` | `F1D83BE9C995A2D71FB7453DE7D87E4C266F3C9EC0C54F143745FDC6F7A7F29D` |

The production Release build and source whitespace check passed. Both installed hashes and their installation-manifest entries matched. Lossless Scaling reopened and initialized with its existing wrapper; no bridge process or neural playback was started for verification.

Eight protected files remained unchanged at installation, including native-resolution configuration, both neural INIs, model DLL, weights, original Lossless Scaling library, wrapper and current app profile. Native resolution, `Inline=0` and the saved LSFG3 selection are retained. Output equivalence, frame-generation smoothness and achieved inference speed remain unmeasured. No test suite, GPU benchmark, commit or GitHub publication was performed.

Private backups, the downloaded asset, static compatibility evidence and `deployment-state.json` are under `backups/neural-speedup-20260907-205718/` in the installation. The `before/` directory holds the immediately preceding bridge, runtime and manifest for recovery after stopping scaling and closing the app. Model assets and user settings are not part of that rollback.

## References

- [Upstream release notes](https://github.com/danielblnc/DLSS-NR-on-AMD/releases)
- [Microsoft: scoped timer-resolution requests](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod)
- [Microsoft: process execution-speed and timer-resolution policies](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation)
