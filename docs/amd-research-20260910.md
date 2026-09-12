# AMD rendering research — 10 September 2026

This review distinguishes usable upstream updates from proposals and different rendering techniques. No frame-rate numbers from another project are presented as measurements of this one.

## Current AMD DLSS Neural Rendering runtime

The newest official release checked is [DLSS-NR-on-AMD v0.2.18](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.18), published 11 September. It adds GTA V Enhanced frame-generation support and improves frame pacing, latency, stutter and keyboard input handling, alongside smaller fixes. [v0.2.17](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.17) addressed exposure textures, multi-GPU failures and dynamic-resolution stutter. Earlier [v0.2.13](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.13) repaired a CPU-copy fallback and an inactive optimization; [v0.2.15](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.15) repaired duplicate FSR3 captures. These are relevant updates, but their presence must be checked in the installed game, not inferred from the manager version.

The public source tree at [057c873](https://github.com/danielblnc/DLSS-NR-on-AMD/tree/057c87324bfd8131c45c6b7e7de7d22ab46844d5) contains documentation and notices, not runtime kernels. The author says source publication is pending in [issue 4](https://github.com/danielblnc/DLSS-NR-on-AMD/issues/4). The two newest forks inspected, GrimOak and WKImods, also contained documentation rather than an alternative kernel implementation. This bounded search found no newer verified AMD runtime to integrate.

The upstream [license](https://github.com/danielblnc/DLSS-NR-on-AMD/blob/057c87324bfd8131c45c6b7e7de7d22ab46844d5/LICENSE) permits personal noncommercial use and expressly restricts redistribution, modification and reverse engineering, with its stated legal exception. It is not an open-source license. Keep the runtime external; do not repackage it or treat a fork or binary dump as permission to modify it.

### Configuration and measurement

- Retain inline operation for gameplay. The [documented async option](https://github.com/danielblnc/DLSS-NR-on-AMD/blob/057c87324bfd8131c45c6b7e7de7d22ab46844d5/README.md) is intended for photo mode; it is not a demonstrated quality-preserving gameplay speed preset.
- Verify real colour/motion/depth inputs, exposure handling, adapter selection and the zero-copy path in fresh logs. A CPU-copy fallback is a concrete diagnostic lead; a performance slider without a confirmed runtime interface is not.
- Tone, structure and skin controls alter appearance. Lowering them is not evidence of reducing neural workload, and must not be marketed as a free speed improvement.
- [Issue 88](https://github.com/danielblnc/DLSS-NR-on-AMD/issues/88) proposes decoupling the neural grid from output resolution. Its author explicitly had no AMD runtime test. Proposed INI names in the discussion are not implemented configuration keys. A smaller neural input can lose fine neural detail even if final output remains native size.
- [Issue 113](https://github.com/danielblnc/DLSS-NR-on-AMD/issues/113) requests modular kernels and automated benchmarking. It is a development proposal, not a released optimization.

## AMD's own rendering technologies

The current official [FSR SDK v2.3.0](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/tag/v2.3.0) was released 24 June 2026, at [60f4ea8](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/tree/60f4ea81909200d8542eca14dccb2628b763a9a3). AMD's [SDK overview](https://gpuopen.com/amd-fsr-sdk/) lists Upscaling 4.1.1, Frame Generation 4.0.1, Ray Regeneration 1.2.0 and Radiance Caching 0.9.0 preview. These features solve different problems:

| Technology | What it does | Hardware and practical integration |
| --- | --- | --- |
| FSR Upscaling | Reconstructs a higher-resolution frame | AMD's [June update](https://gpuopen.com/learn/amd-fsr-sdk-2-3-blog/) adds ML upscaling on RX 7000 alongside RX 9000. A game's supported integration or eligible driver upgrade is the preferred path. It does not provide the DLSS NR model's material/face reinterpretation. |
| FSR Frame Generation | Interpolates additional displayed frames | ML route targets RDNA 4. Requires temporal inputs and presentation integration; it does not shorten a neural pass or increase simulation FPS. |
| FSR Ray Regeneration | Reconstructs noisy ray-traced lighting | Requires an engine ray-tracing integration and its buffers. It cannot add scene-correct ray tracing to a finished screenshot. |
| FSR Radiance Caching | Learns indirect illumination during path tracing | [Technical preview](https://gpuopen.com/amd-fsr-radiancecaching/) targets RX 9000 and DX12. It consumes path-tracing training data; it is not a universal post-process swap. |

The SDK includes component-specific terms in [docs/license.md](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/docs/license.md) and [Kits/FidelityFX/docs/license.md](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/docs/license.md), plus third-party notices. Review the particular component's terms before any distribution. No SDK DLLs or model weights are included by this research.

A future manager can detect eligible FSR installations and explain supported native options. Automatically replacing arbitrary game DLLs would require version/ABI checks, tested games, backups and applicable distribution rights. It is not justified by a DLL having FSR in its name.

## HIP / ROCm

AMD publishes distinct [HIP SDK for Windows](https://www.amd.com/en/developer/resources/rocm-hub/hip-sdk.html) and [ROCm platform](https://www.amd.com/en/products/software/rocm/sdk.html) offerings. Their version numbers and platform support are not interchangeable. The Windows page inspected lists HIP SDK 7.2; development documentation may describe other releases. A newer compiler/toolkit does not recompile kernels inside an existing third-party binary. Installing it globally is therefore not an established optimization for this runtime.

For the current direct-game path, follow the runtime's documented AMD driver requirement and log the actual loaded runtime version. Evaluate a toolchain change only with compatible source, an exact target GPU/OS matrix and a repeatable before/after capture. Do not replace system HIP DLLs or add extra SDK prerequisites merely because a higher version number exists.

## Offloading to another GPU

[Neural Coprocessor at d89316e](https://github.com/maohgad-web/Neural-coprocessor/tree/d89316eac0c2eb9984bc9918139b630438606b65), dated 8 September, is MIT-licensed research for moving a terminal neural pass to a second GPU. Its NVIDIA runtime path and documented second-GPU/second-monitor presentation topology do not establish an AMD implementation. Colour handling, swapchain changes and frame generation have documented limitations. Cross-adapter offload is an architectural research option, not a drop-in speed patch for a single Radeon card.

## Integration priority

1. Keep official AMD runtime provenance visible and identify installed versions per game.
2. Diagnose wrong-adapter, CPU-copy and repeated-capture paths from bounded logs; retain quality settings during comparisons.
3. Reduce avoidable manager polling, repeated hashing and UI-thread I/O without changing neural input or temporal behavior.
4. Present native FSR technologies as separate capabilities with their actual prerequisites.
5. Defer new kernel routes, async gameplay defaults, multipass and low-resolution neural presets until implementation rights, runtime support and measured quality/performance evidence exist.

No runtime binaries were downloaded, patched or installed for this review.

## Update — 12 September 2026

Following the 11 September 2026 Crimson Desert update, re-testing on the development machine confirmed that the game executable now breaks three essential render detours in both upstream v0.2.17 and v0.2.18, stalling the runtime before neural engine initialization. The OptiScaler AMD pre-SR v1.2 proxy also causes startup access violations and is now blocked for this title. I updated the manager and CLI diagnostics to enforce session-scoped verification, detect failed render hooks, and report inactive runtime sessions truthfully. Full test logs, mitigation checks, and reproduction details are documented in [Direct Game verification — 12 September 2026](verification-20260912.md).
