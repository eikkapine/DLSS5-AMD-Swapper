# Upstream refresh — 19 September 2026

This update integrates current AMD runtime configuration and diagnostics, improves Lossless Scaling startup evidence, and adds **Settings → Check runtime updates**. Runtime release availability is separate from compatibility verified on a particular game or GPU.

## DLSS-NR-on-AMD

[v0.3.1](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.3.1) is the latest official release checked. Its release notes describe a revised wait method, fixes for frozen frame-generation output in slow scenes, RE Engine fixes, and a memory leak fix. These are upstream changes; this project makes no measured performance claim from the release notes.

[v0.3.0](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.3.0) introduced pre-upscaling. The official installer uses `PreUpscale=1`, `PreHistory=0` and `Async=0` for the Direct Game path. `Temporal=1` remains in its generated configuration: pre-upscale history is controlled separately. The manager and CLI validate the actual modern schema, retain legacy validation, and continue using the narrow Crimson Desert compatibility pin until newer compatibility is demonstrated.

The official v0.3.1 installer is 7,598,347 bytes with SHA-256 `cf7ada1486b499700a84846b342ca2b1defdb4db622843f812151f255f2ad63c`. The download was verified against GitHub metadata without installing it into a game. Third-party binaries and neural weights are not distributed by this project.

Lossless Scaling receives finished colour frames and has no FSR dispatch. Its setup now writes both the legacy and modern asynchronous settings:

```ini
[DlssNrOnAmd]
Enabled=1
UseFsrInputs=0
Inline=0
Async=1
PreUpscale=0
```

This prevents modern defaults from selecting synchronous or pre-upscale behavior for the desktop bridge. Native visible output and the capped neural branch are retained.

## OptiScaler

[v0.9.4](https://github.com/optiscaler/OptiScaler/releases/tag/v0.9.4), published 18 July 2026, remains the latest stable release checked. It includes FSR 4.1.1 integration and official INT8 support for supported RDNA 3 desktop cards, plus fixes and game-specific compatibility settings. Unsupported cards can fall back to FSR 3; the upstream watermark is the recommended confirmation of the active upscaler. See the release notes for exact hardware restrictions.

The [nightly release](https://github.com/optiscaler/OptiScaler/releases/tag/nightly) had no downloadable assets at the time of this check. Master continues to change; its [checked revision](https://github.com/optiscaler/OptiScaler/commit/5d8273609722b8dd07c0e70ea3f11f14016fd9d9) is not a tested binary update.

Stock OptiScaler does not satisfy this manager's custom `amd-presr` package contract. Keep a complete compatible fork package with matching neural pass DLLs and dependencies; replacing only its proxy with stock v0.9.4 would remove required functionality. The update check reports official availability without overwriting a selected custom package.

## NODIX DLSS 5 Manager

Reviewed [DLSS 5 Manager](https://github.com/NODIX-TECH/DLSS-5-MANAGER) at [23a5d13](https://github.com/NODIX-TECH/DLSS-5-MANAGER/commit/23a5d134216003bfc9fee7090088a6174c7edfb3) and its [1.3.1 release](https://github.com/NODIX-TECH/DLSS-5-MANAGER/releases/tag/1.3.1). Relevant ideas are explicit payload selection, reversible installation, release checking, and clear separation of AMD and OptiScaler routes. Our manager already has manifest-backed restore and route checks; this update adds an independent official runtime release check.

The project's copyright notice does not grant unrestricted reuse. No code or bundled payload was copied. NVIDIA-specific overlays, MFG and ReShade routes do not establish AMD bridge support and are not advertised here as new capabilities.

## Issue #3 and validation limits

[Issue #3](https://github.com/eikkapine/DLSS5-AMD-Swapper/issues/3) describes initialization without a completed neural job on an RX 9060 XT, using legacy v0.2.17/v0.2.18. The attachment confirms initialization and adapter matching; it does not isolate the runtime stall's cause.

The old bridge only flushed buffered HIP timing after readiness, so a failed startup could leave timing headers without call rows. Startup diagnostics now preserve evidence before readiness, distinguish initialization from completed neural work, and report feed presentation and configuration state. The ready file still requires healthy completed neural work; initialization alone cannot make the route appear successful.

To retest: close Lossless Scaling, open the new manager, and use its Lossless Scaling setup/update with your chosen compatibility `version.dll` and legitimate neural DLL. Reinstalling through setup applies the modern configuration; replacing the manager executable alone does not update an installed bridge. Keep the selected adapter and ordinary Scale workflow unchanged. If readiness still fails, review and attach `bridge-startup.log`, `dlssnr_on_amd.log`, and the HIP timing log from `nr-bridge/runtime`, plus the final `NrAutoScale.log` error. Remove personal paths from runtime/auto-scale logs and never attach DLLs or weights. `bridge-startup.log` contains structured startup fields and is replaced on each launch, so save the failed run before trying again.

The modern configuration correction prevents a separate v0.3.x integration problem. Neither that correction nor the upstream release notes prove that the reporter's original v0.2.18 stall is resolved. A fresh run on the affected machine remains necessary, and the issue stays open for that confirmation. See the release notes for build and regression-test results.
