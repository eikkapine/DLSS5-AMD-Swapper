# Licensing and third-party boundary

My original DLSS5 AMD Swapper source, scripts and documentation are released under the repository's [MIT License](../LICENSE). That licence covers only work I can license myself.

It does not grant rights to Lossless Scaling, NVIDIA runtime/model files, `DLSS-NR-on-AMD`, third-party AMD compatibility binaries or other projects I reference.

## Third-party projects

| Project | How I use it | Repository rule |
| --- | --- | --- |
| [danielblnc/DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD) | AMD execution path for the first direct-game route | I do not redistribute, bundle, modify or repackage its installer/runtime. Users supply their own official copy. |
| [rakanki911/DLSS5-Swapper](https://github.com/rakanki911/DLSS5-Swapper) | Workflow/UI inspiration | My WPF AMD manager is a separate implementation. Preserve upstream notices if any source is ever copied/adapted. |
| [LastSkywalkerER/GameSaver](https://github.com/LastSkywalkerER/GameSaver) | Installed-game discovery architecture and executable-selection reference | The current upstream README labels the project MIT. My scanner is independently implemented in C#; preserve the upstream notice if source is ever copied/adapted. |
| [FrankBarretta/LSP-ReShade](https://github.com/FrankBarretta/LSP-ReShade) | ABI/proxy reference | Preserve its MIT notice if source is copied/adapted. |
| [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder) | Feeding/integration reference | Preserve its MIT notice if source is copied/adapted. |
| [NVIDIA/DLSS](https://github.com/NVIDIA/DLSS) | NVIDIA SDK/runtime terms reference | Do not redistribute NVIDIA DLLs, SDK files, models or weights from this repository. |
| [AMD FidelityFX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK) | FidelityFX reference/header material | Keep upstream notices with any published vendored material. |

The current `DLSS-NR-on-AMD` licence has restrictions that make bundling it into this project inappropriate, so the direct-game installer requires a local official setup supplied by the person running the tool. The manager verifies that local setup against GitHub release metadata and invokes it unchanged.

I also do not claim that using every combination of third-party NVIDIA/AMD compatibility software is automatically permitted everywhere. Anyone using or redistributing third-party components should review the current upstream licences and the target game's terms.

## Lossless Scaling

My project builds its own wrapper named `Lossless.dll` because that filename is required by the local forwarding setup. It is not the paid Lossless Scaling DLL.

During a private install, the original paid DLL can be preserved locally as `Lossless_original.dll` so my wrapper can forward to it. Neither the original DLL nor that local backup belongs in GitHub or a release ZIP.

I do not publish:

- the paid Lossless Scaling `Lossless.dll`
- `Lossless_original.dll`
- Lossless Scaling executables/assets
- copied private Lossless Scaling configuration
- local backups created during install/uninstall

## Public release boundary

A normal DLSS5 AMD Swapper release may contain:

- my source code
- my WPF manager
- project-built wrapper and bridge binaries
- project setup/uninstall scripts
- direct-game helper and performance/provenance tools
- documentation/licence notices
- the two already-approved comparison crops
- sanitized hash-backed measurement metadata

It must not contain:

- `DLSS-NR-on-AMD` binaries/installers or generated weights
- NVIDIA runtime/model/SDK files
- third-party AMD compatibility binaries
- paid Lossless Scaling files
- private logs, manifests, configs, crash dumps, traces or backups
- machine-specific paths, secrets or personal files
- unreviewed screenshots/captures

Third-party software always remains governed by its own current licence and terms.
