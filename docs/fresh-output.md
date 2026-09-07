# Fresh-output handoff — 7 September 2026

Follow-up: the [native GPU handoff](gpu-handoff.md) replaces steady-state CPU frame transfers and reduces the default feed ceiling. This page records the preceding CPU duplicate-suppression build and its original 2-ms polling budget.

The user's latest v0.2.15 run reports sampled neural jobs of 52, 51, 74 and 55 ms at 2560x1440. Its last milestone has 1,200 feed frames and 91 ready jobs. The scheduling requests were accepted, but the user reports no visible improvement. These sparse counters do not establish how many visible frames were identical or successfully interpolated.

The new bridge targets repeated visible work and the cadence presented to Lossless Scaling. It compares every RGB byte with the last successfully submitted image. An identical image avoids CPU-to-GPU visible upload, colour conversion, the visible GPU copy and a visible Present call. Any RGB difference is retained; there is no tolerance, hash-only decision, downscaling or output approximation. Alpha is excluded because the existing visible conversion always makes it opaque.

The asynchronous producer continues capturing, feeding the neural runtime, reading back output, processing stop/hotkey messages and checking runtime health even when a visible submission is suppressed. Its minimum start-to-start interval is 2 ms (at most 500 feed iterations/s), independent of visible presentation. Work taking longer than 2 ms receives no additional delay. A lower explicit `--max-fps` limit is respected. This bounds polling pressure; it is not a claimed neural evaluation rate.

Startup and hotkey changes force submission. An occlusion return causes repaint retries without inventing an RGB change. The Direct3D swapchain types, GPU fences, runtime DLL, model, weights, native processing dimensions, effect strength and user's LSFG settings are unchanged. `Inline=0` stays selected. This candidate does not revive the failed same-frame/inline experiment.

`bridge-cadence-<pid>.log` is generated during normal manual use, without saving images. It reports feed rate, changed-RGB submission rate, visible submission rate, identical submissions skipped and occlusion returns once per second. A changed RGB submission is not necessarily a completed new neural job, a displayed frame or an interpolated frame. If duplicates are scarce, this change has little work to eliminate. If the visible swapchain remains occluded, the log exposes the retry path.

Use `--repeat-presentations` in a standalone manual launch to disable suppression and its implicit feed budget; `--no-proxy` keeps the repeated presentation path as well. No application profile changes are needed for normal proxy launches.

## Verification boundary

This handoff changes presentation scheduling. Its effect on WGC/LSFG interpolation and gameplay smoothness must be established by the user's manual check; reduced presentation counts alone are not proof of improvement. A completed inference speedup is not claimed. Automated playback, GPU benchmarks and test suites remain excluded, and GitHub publication remains pending user verification.

The scoped installation backup is `backups/fresh-output-20260907-211449/` under the Lossless Scaling installation. It preserves the preceding bridge and installation manifest, source files and the user's last runtime log. `deployment-state.json` records the build, installed hashes and allowed initialization checks when completed.

## API context

[Microsoft's Present documentation](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-present) defines presentation synchronization and the `DXGI_STATUS_OCCLUDED` status. It does not guarantee how a separate frame generator will treat repeated or irregular inputs.
