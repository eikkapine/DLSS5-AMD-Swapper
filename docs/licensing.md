# Licensing and third-party boundary

My original NR Auto Scale source, scripts, and documentation are released under the repository's [MIT License](../LICENSE). That license only covers work I can license myself.

It does not automatically cover Lossless Scaling, NVIDIA runtime/model files, the AMD compatibility proxy, copied upstream code, or other third-party material.

## Referenced projects

| Project | What I use it for | Public-repo rule |
| --- | --- | --- |
| [danielblnc/DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD) | AMD DLSS-NR compatibility runtime reference | Link/reference only. Do not redistribute its proxy, installer, config, or copied source unless its terms allow it. |
| [FrankBarretta/LSP-ReShade](https://github.com/FrankBarretta/LSP-ReShade) | ABI/proxy reference | MIT; preserve its notice if source is copied/adapted. |
| [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder) | Feeding/integration reference | MIT; preserve its notice if source is copied/adapted. |
| [NVIDIA/DLSS](https://github.com/NVIDIA/DLSS) | NVIDIA SDK/runtime terms reference | Do not redistribute NVIDIA DLLs, SDK files, or models from this repo. |
| [AMD FidelityFX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK) | Small vendored header subset | Keep the upstream notices and third-party notice file with any published vendored headers. |

The audited AMD compatibility project currently has restrictive personal/non-commercial terms for its own distributed proxy materials. I therefore keep those binaries/config/installers out of NR Auto Scale releases.

NVIDIA's DLSS/NGX terms also do not give me a clear basis to claim that every AMD runtime path is authorized. Because of that, I do not describe the complete third-party runtime chain as legally cleared. Anyone packaging or distributing additional runtime material should review the current upstream terms and obtain permission where needed.

## Lossless Scaling

The project builds its own wrapper named `Lossless.dll`. That filename is required for the local forwarding setup; the project-built file is not the paid Lossless Scaling DLL.

During a private local install, the original Lossless Scaling DLL can be preserved as `Lossless_original.dll` so my wrapper can forward to it. Neither the original DLL nor that private backup belongs in GitHub or a release ZIP.

Do not publish:

- the paid Lossless Scaling `Lossless.dll`
- `Lossless_original.dll`
- Lossless Scaling executables or app assets
- copied private Lossless Scaling configuration
- local backups made by setup/uninstall

## Public release allowlist

A normal NR Auto Scale release may contain:

- original project source
- the project-built wrapper and bridge binaries
- project build/setup/uninstall scripts
- documentation and license/notices
- the two already-approved comparison crops
- sanitized release/measurement metadata

It must not contain:

- AMD proxy/runtime binaries or installers
- NVIDIA runtime/model/SDK files
- paid Lossless Scaling files
- private logs, crash dumps, traces, backups, or machine-specific paths
- unreviewed screenshots, movie/browser captures, wallpapers, or personal files

This page records the project's release boundary; it is not legal advice or permission from any third-party rights holder.
