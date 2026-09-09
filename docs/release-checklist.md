# Release checklist

I use this checklist before publishing a preview.

## Build and version

- [ ] `VERSION`, tag, and release title match.
- [ ] `py .\tools\Check-Publication.py` passes.
- [ ] Production bridge and proxy builds complete successfully.
- [ ] `RELEASE.json` contains the final binary hashes.
- [ ] Packaged binaries match the hashes in the release metadata.

## Runtime boundary

- [ ] No paid Lossless Scaling file is staged.
- [ ] No `Lossless_original.dll` is staged.
- [ ] No AMD proxy/runtime binary or installer is staged.
- [ ] No `dlssnr_on_amd_setup.exe` or local direct-game manifest is staged.
- [ ] No NVIDIA DLL, model, or SDK payload is staged.
- [ ] No private logs, backups, config, machine paths, or secrets are staged.

## Images

- [ ] Only reviewed/approved images are included.
- [ ] Image metadata is empty or intentionally public.
- [ ] No usernames, account details, chat, desktop UI, or unrelated personal content is visible.

## Claims

- [ ] README status matches the exact path exercised for this version.
- [ ] Bridge-only measurements are not described as end-to-end app results.
- [ ] Numeric game/display frame-rate claims come only from hashed PresentMon logs.
- [ ] Measurement JSON contains no hand-entered/manual performance fields.
- [ ] Performance claims keep source resolution, strength, model/weights, and precision clear.
- [ ] Unverified GPU/game/driver combinations are not presented as supported facts.

## Package

- [ ] `Setup.cmd` is at the package root.
- [ ] Install and uninstall docs are included.
- [ ] `CHECKSUMS.json` contains the allowlisted package files.
- [ ] ZIP contents are inspected after packaging.
- [ ] Release notes link to the matching tag/version.
