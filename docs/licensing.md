# Licensing And Attribution

This page records what can be published and what still needs care. It is not legal advice, and it is not blanket authorization that the whole runtime path is allowed.

## Project License

Original source code, scripts, and documentation authored in this development workspace are MIT licensed under [LICENSE](../LICENSE).

That license does not cover third-party projects, runtime DLLs, model files, paid Lossless Scaling files, Windows assets, screenshots, logs, copied upstream source, or the user's local files.

GitHub's licensing guide explains that repositories without a license do not grant broad reuse rights by default: <https://docs.github.com/articles/licensing-a-repository>.

The current probe, bridge, auto-scale wrapper, setup scripts, and control sources are project-authored code and do not include NVIDIA SDK material.

## Source References

| Source | Observed license status | Public repo action |
| --- | --- | --- |
| <https://github.com/danielblnc/DLSS-NR-on-AMD> | License added in commit `057c87324bfd8131c45c6b7e7de7d22ab46844d5` on 2026-09-06. The audited terms allow personal, non-commercial use and do not permit redistributing the proxy, installer, or config. | Link as a reference only. Do not redistribute its `version.dll`, setup executable, binaries, config, or copied source without separate permission. |
| <https://github.com/FrankBarretta/LSP-ReShade> | MIT, copyright 2025 FrankBarretta | If source is copied or adapted, keep the MIT notice and attribution. If only referenced, link upstream. |
| Local `LosslessProxy` clone | MIT, copyright 2025 FrankBarretta | Do not claim ownership. Exclude the local clone from the public repo unless imported as an audited third-party component with notices. |
| <https://github.com/jlrouzies-fr/DLSS5-Feeder> | MIT, copyright 2026 Jean-Laurent ROUZIES | If source is copied or adapted, keep the MIT notice and attribution. If only referenced, link upstream. |
| <https://github.com/NVIDIA/DLSS/blob/main/LICENSE.txt> | NVIDIA DLSS SDK proprietary terms. The RTX Supplement says the public license permits DLSS/NGX SDK use only on systems that include NVIDIA GPUs. | Do not redistribute user-provided NVIDIA DLLs, SDK files, or models in this repo. No NVIDIA permission has been found for an AMD runtime path; separate permission may be required before claiming that path is allowed. |
| <https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK> | Vendored headers include AMD permission notices and an upstream third-party notice file in the local snapshot. | If FidelityFX headers are published, preserve the license headers, keep `3rdpartynotice.md`, and record the pinned upstream revision. |

## Runtime Permission Boundary

The source-only audit does not clear runtime permissions.

- User-provided NVIDIA DLLs or SDK materials do not create clear permission to use DLSS/NGX through an AMD runtime path.
- The AMD proxy reference has restrictive personal, non-commercial terms, so its binaries, installer, config, and copied source must not be redistributed from this project.
- MIT-licensed reference projects solve only their own source-notice requirements. They do not authorize NVIDIA SDK use, AMD proxy redistribution, Lossless Scaling file redistribution, or a claim that the full project is legally permitted.
- Public release wording must avoid saying the project is "all allowed" unless separate runtime permissions are resolved.

## Lossless Scaling File Boundary

Do not publish paid Lossless Scaling files.

The project may build an original proxy artifact named `Lossless.dll`. That project-built file is distinct from the paid Lossless Scaling original DLL. A local private install may preserve the user's original DLL as `Lossless_original.dll` so the proxy can forward calls to it.

The public repo and public release package must not include:

- The paid Lossless Scaling original `Lossless.dll`.
- `Lossless_original.dll`.
- Lossless Scaling executables, app assets, configuration files copied from the paid app, or local backups.
- Any file copied from the user's installed Lossless Scaling directory unless it is an original project file.

## Public Package Allowlist

A public package may contain:

- Original project source.
- Build scripts.
- Documentation and license files.
- The project-built proxy `Lossless.dll`.
- The project-built bridge executable.
- Setup and uninstall scripts.

Everything outside this allowlist needs a fresh license and contents review before upload.

## Files To Exclude From Public Releases

- Paid Lossless Scaling application files.
- AMD runtime binaries and setup executables.
- NVIDIA DLSS DLLs, SDK files, and model files.
- Full upstream repository clones unless added through a deliberate audited import.
- Local backups from active installations.
- Logs, crash dumps, traces, and machine-specific reports.
- Screenshots, movie frames, browser captures, Windows wallpaper images, and comparison images from local testing, except for the approved analytical crops under `docs/images/`.
- Private environment files.
- FidelityFX headers without their license notices, third-party notice file, and pinned upstream revision.

The approved analytical comparison crops under `docs/images/` may be published on the GitHub page. Do not add additional screenshots, personal images, browser captures, movie frames, or wallpaper images without a fresh contents review.

## Attribution Rule

If future development copies or adapts upstream source, add a notice that names the upstream project, copyright holder, license, and source URL before publishing.

If the project only uses an upstream repository as a reference, keep it as a link in documentation and do not include its files in this repository.
