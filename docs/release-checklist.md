# Release Checklist

## v0.1.0-pre.2 checkpoint exception and status

The user authorized publication after manually reporting approximately 19 FPS.
This experimental checkpoint uses production compilation, static/package/hash
inspection and the existing manual logs, with no automated tests or playback.
The older checklist below is a historical/full-validation reference and must
not be presented as passed for this preview. Current wording and limits are in
[the release notes](releases/v0.1.0-pre.2.md) and [progress.md](progress.md).

The package uses the exact manually exercised bridge executable. Source/setup
defaults remain fixed 720p; the user's measured profile selects native 1440p.
Native image equivalence, broad compatibility and 60 FPS remain unverified.

## Historical full-validation checklist

Complete this checklist before publishing a public GitHub repository.

## Build And Test

- [ ] `dotnet build .\controls\DlssNrControl\DlssNrControl.csproj -c Release` succeeds.
- [ ] `.\controls\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe --self-test` succeeds.
- [ ] Temporary-INI smoke tests prove `--toggle`, `--increase`, and `--decrease`.
- [ ] `.\controls\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe --listener-smoke` succeeds.
- [ ] `.\auto-scale\build.ps1` succeeds and produces the project-authored proxy `Lossless.dll`.
- [ ] `.\auto-scale\tests\Run-AutoScaleTests.ps1` succeeds. The current suite has passed all 12 proxy cases and four isolated installer checks.
- [ ] `.\bridge\build.ps1` succeeds.
- [ ] `.\probe\run_probe.ps1 -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1` succeeds in a private local development folder.
- [ ] Optional visible probe run `.\probe\run_probe.ps1 -Visible -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1` succeeds.
- [ ] Probe output includes matched off/on captures from the generated static test pattern.
- [ ] Probe logs or counters show completed neural runtime work.
- [ ] Bridge run evidence shows `Ctrl+Alt+F6` toggles live output and `Ctrl+Alt+F7`/`Ctrl+Alt+F8` adjust output blend.
- [ ] Bridge proof confirms off output max error `0`, full-strength on output equals NR max error `0`, and blended output differs only within rounding tolerance.
- [ ] Frozen-source bridge proof shows nonzero NR difference without black output.
- [ ] Native Lossless Scaling integration starts automatically from the actual Scale button with no separate launcher or source picker.
- [ ] Native WGC resolution is used.
- [ ] The wrapper keeps Lossless Scaling geometry resizing disabled, or equivalent native-resolution behavior is confirmed.
- [ ] Full deterministic Lossless Scaling/browser testing shows a visible on/off difference on a static image without geometry resizing.
- [ ] Native 2560x1440 bridge/runtime timing is described as experimental and not as full Lossless Scaling end-to-end latency.
- [ ] Any gameplay wording is limited to the tested menu scenario unless a later low-latency implementation is verified.

## Public Repository Contents

- [ ] Include only original source, build scripts, documentation, license files, and approved project-built artifacts.
- [ ] Include the project-built proxy `Lossless.dll` only if the release needs a binary package and the binary was built from this repo.
- [ ] Include the project-built bridge executable only if the release needs a binary package and the binary was built from this repo.
- [ ] Include setup and uninstall scripts only after they are reviewed for source-safe packaging.
- [ ] Exclude `LosslessProxy/` unless it is added as a proper upstream reference, submodule, or separately audited source import.
- [ ] Exclude `backup-original/`.
- [ ] Exclude build outputs that are not part of an approved source-safe binary package.
- [ ] Exclude runtime captures, logs, screenshots, and local artifacts.
- [ ] Exclude NVIDIA, AMD, and model binaries.
- [ ] Exclude paid Lossless Scaling files, including the original `Lossless.dll`, `Lossless_original.dll`, app executables, app assets, configs copied from the paid app, and local backups.
- [ ] Exclude Windows wallpaper test images and comparison images.
- [ ] Exclude private environment files.
- [ ] Remove user-specific absolute paths from documentation and generated files.
- [ ] Remove user logs and GPU identifiers.
- [ ] Include only the approved analytical comparison crops under `docs/images/`; exclude any additional screenshots or personal images.

## License And Attribution

- [ ] Confirm the license for every copied upstream source file.
- [ ] Preserve required copyright notices.
- [ ] Add third-party notices for copied source.
- [ ] Link to upstream repositories used as references.
- [ ] Do not claim ownership of the upstream `LosslessProxy` clone or other third-party projects.
- [ ] Keep proprietary runtime files out of the public repo.
- [ ] Resolve the runtime permission question before claiming the whole project is allowed. No NVIDIA permission has been found for a DLSS/NGX AMD runtime path, and the AMD proxy reference is personal/non-commercial with no redistribution of proxy, installer, or config.
- [ ] Confirm release wording does not imply blanket legal clearance from a source-only audit.
- [ ] Preserve FidelityFX notices, include the upstream third-party notice, and record the pinned source revision if FidelityFX headers are published.

## Current Source Update and Release Claim

The 7 September source update defaults to 1280×720 fixed-size processing, with native mode optional. Its performance, default-forwarding and installer evidence is in [performance.md](performance.md). The older binary preview is unchanged. The native-specific checks above remain requirements for a future native 1:1 release claim; they do not describe the fixed-size default.

Use release wording only after verification passes:

```text
Experimental AMD DLSS NR native Lossless Scaling integration with verified Scale-button startup and native-resolution static desktop image validation, without geometry resizing.
```

Before verification passes, use this wording:

```text
Experimental AMD DLSS NR Lossless Scaling integration tooling with 720p processing by default and optional native mode. Direct Scale-button verification and a full native-mode app comparison are not yet complete.
```

Prepare any public outreach message, including an Ancient Gameplays copyable note, only after the public repo URL exists. Do not send outreach automatically.
