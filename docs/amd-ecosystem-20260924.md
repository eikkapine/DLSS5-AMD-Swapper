# AMD ecosystem refresh — 24 September 2026

This update adds support for AMD-NR pre-SR packages, fixes Restore when a game session has rewritten the runtime logs, and adds AMD-NR to **Settings → Check runtime updates**. A project being released does not mean it has been verified on a particular game or GPU. No figures from another project are presented as measurements of this one.

## Manager changes

- **AMD-NR packages.** The OptiScaler pre-SR route now accepts [3zwr1/AMD-NR---OptiScaler](https://github.com/3zwr1/AMD-NR---OptiScaler). The package needs its matching Runtime zip. The manager identifies the fork by its embedded build string and finds the pass DLLs and weights beside `OptiScaler.dll` or under `Runtime\`. The `Runtime/` checksums from `SHA256SUMS.txt` are enforced wherever the files were extracted, so a fork mixed with passes from another runtime release is rejected before anything is copied. The optional lmxxf runtime is installed and restored with the rest. See [AMD-NR packages](optiscaler-presr.md#amd-nr-packages-3zwr1).
- **Restore and runtime logs.** `dlssnr_on_amd.log`, `OptiScaler.log`, `amd_presr.log` and `lmxxf_backend.log` are rewritten on every game launch, so they never matched the hash recorded at install. Restore used to preserve them as "changed" files and keep the manifest, which left the game marked as managed and blocked switching routes. Both routes now delete these logs unless they existed before the install.
- **Runtime release check.** The check now includes AMD-NR releases alongside DLSS-NR-on-AMD and OptiScaler. Stock OptiScaler still cannot replace a pre-SR fork.

## Upstream status

### DLSS-NR-on-AMD

[v0.3.1](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.3.1) remains the latest official release checked. The [upstream refresh](upstream-refresh-20260919.md) covers its changes and the verified installer digest. [v0.3.0](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.3.0) turned pre-upscaling on by default, and its release notes publish before/after figures for several FSR quality modes. Those are upstream results. The Direct Game configuration keeps upstream's `PreUpscale=1`, `PreHistory=0` and `Async=0`.

RX 7000 (RDNA 3) has no FP8 matrix support, which RDNA 4 added, and this project has not verified results on it. [Issue #164](https://github.com/danielblnc/DLSS-NR-on-AMD/issues/164) tracks a nearly black image on an RX 7900 XTX in Cyberpunk 2077 with the current release.

### AMD-NR (3zwr1)

[Alpha0.3.1](https://github.com/3zwr1/AMD-NR---OptiScaler/releases/tag/Alpha0.3.1) was published on 23 September 2026. It is a GPL-3.0 OptiScaler fork with published source. The release has two parts: the fork zip and a Runtime zip containing the DLSS-NR-on-AMD v0.3.1 pass DLLs and weights. It adds a `Home` hotkey that toggles neural rendering and an optional frame-capture key. On first launch it also asks the player to choose between the DLSS-NR-on-AMD runtime and lmxxf's runtime.

### lmxxf runtime

[lmxxf/dlss5-on-amd-9070xt-porting](https://github.com/lmxxf/dlss5-on-amd-9070xt-porting) is a from-scratch RDNA 4 re-implementation of the DLSS-NR network. AMD-NR ships it as an optional backend and lists it as RX 9000 only. The manager installs it when the package contains it and never bundles it.

### janblade F5 and wilsjo2

[janblade/OptiScaler-F5-DLSSNR-Multipass v0.1.16](https://github.com/janblade/OptiScaler-F5-DLSSNR-Multipass/releases/tag/v0.1.16-highlight-guard-and-streamline-isolation) adds `DlssNr.PassFeedback`, which damps what each extra multipass step feeds the next. It also adds an opt-in D3D12 plus DirectML backend, which its notes say runs on an RX 6700 XT but is not yet playable there (RDNA 4 untested), and fixes a Streamline `slInit` 0x18 crash. Several of its fixes are credited to [wilsjo2's fork](https://github.com/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass). Neither fork was validated against real binaries for this update. The manager refuses packages it cannot identify rather than guessing their layout.

### neural-amd-opti

[MatheusFerreiraS/neural-amd-opti v0.1.1-amd-nr](https://github.com/MatheusFerreiraS/neural-amd-opti/releases/tag/v0.1.1-amd-nr) targets DLSS-only games. It needs the DLSS-NR-on-AMD v0.3.0 or v0.3.1 runtime `version.dll`, which is not included and is installed by the project's own setup script. That is a different install contract from the pre-SR packages here, so it is not integrated.

### NODIX DLSS 5 Manager

I reviewed [DLSS 5 Manager](https://github.com/NODIX-TECH/DLSS-5-MANAGER) 1.3.2, which added AMD support, for ideas only. Its uninstall also clears runtime leftovers. Comparing that behaviour against this manager's Restore surfaced the log-retention bug fixed above. Its copyright notice does not permit reuse, so no code or payload was copied.

## Validation

- C# smoke tests and Python tests cover AMD-NR identification, pass lookup under `Runtime\`, rejection of mismatched runtime checksums, installing and restoring the lmxxf runtime, and deleting runtime logs on Restore.
- The published AMD-NR v0.3.1 fork and Runtime files were validated in scratch folders through the manager's validation code and the CLI. Passes beside `OptiScaler.dll` and passes under `Runtime\` were accepted. A pass DLL from an older runtime release was rejected.
- No game was launched with AMD-NR for this update. Gameplay compatibility and performance on any GPU remain unverified here.

No third-party binaries were redistributed, modified or bundled for this review.
