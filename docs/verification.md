# Verification

I use fresh local builds, smoke tests and runtime logs to separate what the project has actually verified from what is still experimental.

## Manager and package

The current local manager candidate is built as a self-contained Windows x64 app. The package build runs the WPF smoke tests first, then builds the project-owned Lossless Scaling wrapper/bridge, publishes the manager and rejects forbidden third-party/private filenames before writing `SHA256SUMS.txt`.

The package contains only:

- `Dlss5AmdSwapper.exe`
- `SHA256SUMS.txt`
- project-owned `Lossless.dll`
- project-owned `DlssNrBridge.exe`
- project install/uninstall PowerShell scripts
- the project README, install guide, third-party boundary and MIT license

It does not contain `Lossless_original.dll`, Lossless Scaling executables/assets, `DLSS-NR-on-AMD`, `nvngx_dlssnr.dll`, generated weights, third-party AMD proxy binaries, private logs/INIs/manifests or personal files.

## Lossless Scaling path

The current bridge keeps the accepted native-source compositor behavior:

- visible output stays at the captured source resolution
- `NeuralMaxHeight=480` caps only the neural branch by default
- the current source frame remains the visible base
- unstable broad colour/luminance residuals are filtered
- stale chroma/correction magnitude is reduced during motion
- correction history is reused only where the source is effectively unchanged
- `Ctrl+Alt+F6/F7/F8` save toggle/decrease/increase settings; use the upstream live overlay to verify the active renderer state

The only public gameplay images are the two previously approved CS2 crops under `docs/images/`.

## Direct-game path

The direct route has been exercised with the official upstream setup on a local x64 DX12/FidelityFX title. Runtime evidence included FidelityFX dispatch interception, FSR colour/motion/depth staging, zero-copy input/output interop, inline same-frame mode, completed neural jobs and HIP device selection.

The manager and Python helper both block common anti-cheat markers. The WPF probe specifically checks that anti-cheat evidence overrides an otherwise compatible x64/FSR/DX12 target.

The direct installer records a reversible manifest and preserves files whose current hashes no longer match the install-time state instead of deleting them blindly.

### Session-scoped evidence

The manager evaluates runtime diagnostics strictly per session: only the newest `dlssnr_amd ... loaded into <game exe>` session in `dlssnr_on_amd.log` counts. Older successful runs can no longer mask a newer failed or stalled launch. When render detour failures (`detour of <function> failed (<code>)`) occur without engine initialization, the manager reports that render hooks failed to install, explains that no frames reached the runtime, and sets the game status to `Neural engine stalled - render hooks failed this launch`. Setting `Enabled=1` in the configuration or having high rendering performance does not prove Neural Rendering is active; only logged engine initialization, FidelityFX dispatch, and network job executions constitute verified evidence. A sampled log whose session header lies outside the 1 MiB window yields `SessionStartOutsideSample` and no verdict, and a log without any session header for the executable yields no verdict either.

## Reference-image comparison

`bridge/tests/Compare-DlssReference.py` can compare static reference pairs without publishing the source images. The experiments behind the current architecture showed that a colour-only desktop bridge explains only part of the large appearance change seen in full temporal DLSS examples, which is why the project now prefers the direct-game temporal route whenever possible.

## Not established

- universal compatibility across AMD GPUs or games
- pixel-identical parity with native NVIDIA DLSS implementations
- a controlled direct-game A/B performance benchmark for the current manager candidate
- permission to redistribute third-party runtime binaries beyond their own licences

Concrete performance claims stay out of public prose unless they are generated from hashed logs.
