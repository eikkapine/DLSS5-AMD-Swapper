# Release checklist

I run this before publishing a DLSS5 AMD Swapper update.

## Build

- [ ] `VERSION`, release title and tag match.
- [ ] `dotnet build` for the WPF manager passes with no warnings/errors I am accepting knowingly.
- [ ] Native Lossless Scaling wrapper and bridge builds pass.
- [ ] App smoke tests pass.
- [ ] `py .\tools\Check-Publication.py` passes.
- [ ] `app\Build-Package.ps1` completes and the ZIP opens normally.

## Package contents

- [ ] `Dlss5AmdSwapper.exe` is present.
- [ ] `SHA256SUMS.txt` matches every packaged file.
- [ ] Only project-owned bridge/wrapper/scripts are under `payload\`.
- [ ] `README.md`, `INSTALL.md`, `THIRD-PARTY.md` and `LICENSE` are included.
- [ ] No paid Lossless Scaling file or `Lossless_original.dll` is present.
- [ ] No `dlssnr_on_amd_setup.exe`, `nvngx_dlssnr.dll`, generated weights or third-party AMD runtime/proxy is present.
- [ ] No private INI, local direct-game manifest, raw log, backup, crash dump or machine-specific path is present.

## Repository

- [ ] Git diff/status contains no personal files, secrets, local settings or runtime artifacts.
- [ ] New direct installs use `.dlss5-amd-swapper.json`; the legacy manifest remains compatibility-only.
- [ ] Documentation reads from my project/owner perspective and does not describe manual reports as measured evidence.
- [ ] Numeric performance claims come only from hash-backed measurement JSON.

## Images

- [ ] Only screenshots I reviewed and approved are included.
- [ ] No username/account/chat/desktop/private information is visible.
- [ ] No new screenshot is added just because it was used for local QA.

## Publish

- [ ] Third-party licence/redistribution boundaries are still accurate for the current upstream versions.
- [ ] Public repo name, README title and release package name match **DLSS5 AMD Swapper**.
- [ ] Final release notes describe the exact build that passed verification.
