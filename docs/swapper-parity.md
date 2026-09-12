# Swapper feature and runtime audit

Checked on 10 September 2026. This is a dated comparison, not a claim of complete feature parity or a benchmark. The AMD manager uses a different runtime integration from the NVIDIA Swapper.

## Sources

- NVIDIA Swapper [v2.2.5, released 9 September 2026](https://github.com/rakanki911/DLSS5-Swapper/releases/tag/v2.2.5), at [50605be](https://github.com/rakanki911/DLSS5-Swapper/commit/50605be2895f171a2f5e93d109e320515cfdb28f). Its [README at that revision](https://github.com/rakanki911/DLSS5-Swapper/blob/50605be2895f171a2f5e93d109e320515cfdb28f/README.md) is the feature reference.
- AMD runtime [v0.2.18, released 11 September 2026](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.18). The public documentation tree can lag the binary release, so release metadata and runtime logs are treated separately.
- The NVIDIA overlay's [versioned command protocol](https://github.com/rakanki911/DLSS5-Swapper/blob/50605be2895f171a2f5e93d109e320515cfdb28f/src/overlay-protocol.js) distinguishes Feeder and RenoDX controls and rejects unavailable or stale commands. It does not establish an AMD control interface.

## Manager features

The table reflects the September manager update. Remaining gaps are explicit; this release does not claim complete parity.

| Capability | NVIDIA reference | AMD manager / remaining work |
| --- | --- | --- |
| Installed-game discovery | Store libraries and manual additions | Present, including additional launchers; installation eligibility remains narrower than discovery |
| Search and combined filters | Title, API, DLSS, add-ons | Title/store search with status filter; no arbitrary API/add-on filter |
| Library presentation | Store grouping, artwork, themes | Store grouping, local covers, dark/light theme |
| Scan controls | Manage roots; exhaustive drive scan opt-in | Extra roots, cancellation and opt-in full-drive scan |
| Context menu | Folder, rescan, cover, restore, hide | Folder, recheck, copy path, covers and hide menu; restore button |
| Install/update/restore | Multiple routes with originals retained | AMD direct-game setup/update and manifest-backed restore present |
| History and diagnostics export | Install history and reviewable diagnostic bundle | Persistent local history and previewable sanitized export |
| In-game controls | Custom panel for particular consumers | AMD upstream End-key overlay and manager settings; custom NVIDIA panel is not AMD-compatible by default |
| Tray lifetime | Optional close-to-tray | Implemented |
| Recording status badge | Persistent per-game overlay badge | Missing |
| API override | Per-game override distinct from detection | Missing; an override must never bypass runtime compatibility checks |
| Add-on management | Custom add-ons and integrated routes | General add-on manager missing |
| Localization | Multiple languages and RTL | English only |
| Community reports | Optional hosted service | Missing; no equivalent service configured |
| Distribution | Installer and portable packages | Portable package and optional per-user installer |
| Lossless Scaling | Not the primary workflow | Separate AMD bridge present; uses completed frames rather than engine buffers |

## Runtime changes worth carrying forward

AMD v0.2.18 improves frame pacing, latency, stutter and keyboard input handling and adds GTA V Enhanced frame-generation support. The manager resolves the latest official release dynamically; updating the manager alone does not prove every previously installed game has that runtime. Check the installed runtime and its logs before attributing behavior to the release.

Earlier AMD [v0.2.15](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.15) addressed repeated FSR3 capture, and [v0.2.13](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.13) addressed an unintended CPU-copy fallback. These are reasons to verify capture and copy-path evidence. Upstream speed claims are not measurements of this project and are intentionally not reproduced here.

The NVIDIA release improves overlay lifecycle, library detection and diagnostics. Those design fixes can inform this manager independently of GPU architecture. Its new multipass route increases neural work; it is not a performance optimization. Its HDR fix is specific to the Feeder consumer and should not be treated as a patch to the AMD FSR runtime.

The public AMD repository inspected here contains documentation and license notices, not the HIP kernel implementation. It therefore does not provide source with which to port a NVIDIA runtime optimization into AMD kernels. Manager CPU and I/O improvements are possible locally, but neural frame-time gains require measured runtime changes.

## Compatibility boundaries

The documented AMD direct-game route requires Windows 11, DX12 and FSR, with supported RDNA hardware and a locally supplied compatible neural DLL. The upstream documentation identifies RX 9000 as tested and RX 7000 as expected to work; this is not a guarantee for every model or game. Its End-key controls expose tone, structure and skin adjustments. Vulkan support and model selection are described as future work in the inspected documentation.

NVIDIA Swapper routes involving native DLSS, OptiScaler, RenoDX multipass, legacy API translation or emulators cannot simply be enabled by removing a vendor check. Neither an API override nor discovering an executable proves the AMD runtime can consume its rendering inputs. These routes need an AMD-compatible consumer and actual GPU validation before they can be advertised as supported. Likewise, NVIDIA driver version checks do not apply to AMD drivers.

For live controls, a successfully written configuration is not proof that a running game applied it. Keep the saved state separate from runtime acknowledgement, and verify that an acknowledgement refers to the requested setting. Retain the upstream overlay as a supported live-control path where the manager cannot confirm a change.

## Verification requirements

- Measure manager responsiveness separately from game, neural-pass and display frame times.
- Preserve native output and existing quality settings when testing performance changes.
- Verify live controls, update/restore, scan cancellation, zero automatic selection and persisted settings using executable behavior.
- Check layout at minimum size and different DPI scales, including long titles, empty libraries and error states.
- Publish screenshots only from the actual completed app, with private paths and personal data excluded.
- Keep third-party runtime binaries, models, paid Lossless Scaling files and private logs outside the repository and public package.
