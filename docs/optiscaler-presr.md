# OptiScaler pre-SR route

The OptiScaler pre-SR route evaluates the neural rendering model before the super-resolution pass rather than after it. Running the model on the render-resolution colour buffer reduces the number of pixels evaluated and lets the upscaler reconstruct the final presentation resolution.

## What pre-SR means

In a standard post-upscale path, neural reconstruction runs on the full final display buffer. Evaluating a 2560x1440 native frame requires evaluating every pixel at full resolution. In my Crimson Desert tests with the post-FSR route on an RX 9070 XT at 2560x1440, the runtime logged a median of about 45 ms of network GPU time per job in `dlssnr_on_amd.log`.

With the pre-SR route, the neural model runs earlier in the pipeline on the unscaled render buffer before super-resolution (FSR (ffx)) scales it up. Neural evaluation cost scales directly with input pixel count: a 3840x2160 output frame rendered internally at 1280x720 feeds roughly one quarter the pixels of a native 2560x1440 pass into the network. Live verification and captured measurements for this pre-SR route in this project are currently pending.

## What the published 60-plus-frames-per-second setup actually used

Public reports and tutorials demonstrating more than 60 frames per second at 3840x2160 (such as the Resident Evil Requiem reference on an RX 9070 XT) did not run native 4K neural rendering. The published configuration used three combined stages:

1. **Low internal render resolution**: 1280x720 rendered to 3840x2160 (a 3.0x ratio override) reconstructed through FSR 4 via OptiScaler.
2. **Single neural pass**: evaluated once at 1280x720 before upscaling. In the reference video's OptiScaler in-game menu, model cost was on the order of 10 to 13 ms per frame.
3. **Frame generation**: 3x frame generation (`InterpolationCount=2` via DLSS-G replacement through FFX and DLSS Enabler).

The published 60-plus frames per second figure therefore relied heavily on frame generation, which introduces additional display latency. Frame generation multiplies presented frames; it does not reduce the base rendering or neural evaluation time.

## Package contents and provenance

The reference package `OptiScaler-AMD-PreSR-Multipass-v1.2` (and the bare folder published by Vodkaman23/DLSS-NR-UE5-Opti-DLL) contains:

| File / Folder | Details |
| --- | --- |
| `OptiScaler.dll` | OptiScaler fork build with VersionInfo ProductName `OptiScaler` and ProductVersion `10.0.0-dev (amd-presr-multipass-local) (20260907_075847)` (or Vodkaman build `(20260907_145041)`). Installed as proxy `dxgi.dll`. |
| `OptiScaler.ini` | Base configuration containing `[DlssNr]` and upscaler sections. |
| `dlssnr_amd_pass1/2/3.dll` | DLSS-NR-on-AMD `version.dll` proxy build v0.2.14 (7,156,224 bytes each). The three `dlssnr_amd_pass1/2/3.dll` files in a package are byte-identical copies; the fork loads each file name as an independent runtime instance so each pass owns its own temporal history. The embedded HLSL text differs only between the two published package variants (v1.2 vs the Vodkaman build), not between passes. |
| `dlssnr_on_amd_weights.bin` | Neural network weights (147,689,451 bytes). Matches the SHA-256 hash generated locally from `nvngx_dlssnr.dll` 310.8.0.0. In git-stored packages, this may appear as an LFS pointer with the real file beside it. |
| `OptiScaler\` | Dependency directory containing `amd_fidelityfx_framegeneration_dx12.dll`, `amd_fidelityfx_loader_dx12.dll`, `amd_fidelityfx_upscaler_dx12.dll`, `amd_fidelityfx_vk.dll`, `libxell.dll`, `libxess.dll`, `libxess_dx11.dll`, `libxess_fg.dll`, and `D3D12_OptiScaler\D3D12Core.dll`. |
| `Licenses\` | License attributions for DirectX, FidelityFX v1/v2, OptiScaler GPL-3.0, RenoDX, and XeSS. |
| Scripts & Readme | `INSTALAR_AMD.ps1`, `DIAGNOSTICO_AMD.ps1`, `LEIA-ME-AMD.md`, and `SHA256SUMS.txt`. |
| `dlss-enabler-headless.dll` | Optional OptiScaler v0.9.5-pre3 build shipping beside the package for frame generation support. |

### AMD-NR packages (3zwr1)

[3zwr1/AMD-NR---OptiScaler](https://github.com/3zwr1/AMD-NR---OptiScaler) is a GPL-3.0 OptiScaler fork with published source that uses the same `[DlssNr]` settings, pass DLL names and `amd_presr.log`. The manager accepts it as a pre-SR package. Each release ships as two zips, and both must come from the same release:

| Zip ([Alpha0.3.1](https://github.com/3zwr1/AMD-NR---OptiScaler/releases/tag/Alpha0.3.1)) | Contents |
| --- | --- |
| `AMDNR-v0.3.1.zip` | `OptiScaler.dll`, `OptiScaler.ini`, the `OptiScaler\` dependencies, the optional lmxxf runtime (`LmxxfNrRuntime.dll`, `LmxxfNrRuntime.pak`), licences and `SHA256SUMS.txt`. |
| `v0.3.1-Runtime.zip` | `dlssnr_amd_pass1/2/3.dll` (the DLSS-NR-on-AMD v0.3.1 runtime) and `dlssnr_on_amd_weights.bin`. |

Extract the first zip, then extract the Runtime zip into the same folder, either beside `OptiScaler.dll` or into a `Runtime` folder there. Select that folder in Settings; an `AMDNR-*` folder in Downloads, on the Desktop or in Documents is found automatically. AMD-NR zips are not extracted for you, because the fork zip alone has no pass DLLs.

- **Identification:** AMD-NR keeps upstream's ProductVersion (`11.0.0-dev …`), so the manager identifies it by the `AMD-NR v…` build string embedded in `OptiScaler.dll` and shows that string as the fork version.
- **Matching runtime:** `SHA256SUMS.txt` lists the Runtime zip under `Runtime/`. Those entries are checked wherever the files were extracted, so pass DLLs or weights from a different runtime release are rejected before anything is installed. A v0.3.1 fork needs the v0.3.1 passes.
- **Weights:** in the checked release, the Runtime zip's weights have the same SHA-256 as weights generated locally by the official setup, so the two copies do not conflict. If they ever differ, the manager refuses to choose between them.
- **lmxxf runtime:** when present in the package, `LmxxfNrRuntime.dll` and its asset pack (about 382 MB) are installed with the game and removed by Restore. Upstream lists it as RX 9000 only; it logs to `lmxxf_backend.log`. Delete both files from the package folder to skip the copy; the fork then runs the DLSS-NR-on-AMD runtime.
- **Runtime choice:** the shipped `OptiScaler.ini` leaves `NrBackend` unset, so on the first launch that finds a runtime the overlay asks once which one to use. The manager does not pin it. Upstream notes that a backend change takes effect on the next game start. Update keeps the game's current `OptiScaler.ini` as its base, so the choice survives.

### Provenance and package boundary

DLSS5 AMD Swapper does not download, redistribute, or bundle this package:

- **OptiScaler** is licensed under GPL-3.0 ([cdozdil/OptiScaler](https://github.com/cdozdil/OptiScaler)). Because the pre-SR fork's source code has not been published, its binaries cannot be redistributed with GPL-3.0 source obligations satisfied.
- **DLSS-NR-on-AMD** permits personal, non-commercial use and explicitly forbids redistribution and modification. Renamed pass proxy DLLs cannot be redistributed.
- **Weights** derive from NVIDIA's neural model and cannot be redistributed.
- **Vodkaman23/DLSS-NR-UE5-Opti-DLL** has no published license.
- **AMD-NR** publishes its fork source under GPL-3.0. Its Runtime zip carries DLSS-NR-on-AMD pass DLLs and weights, which stay under the terms above, and the lmxxf runtime ships with its own licence file. The manager does not download or bundle any of them.

Users must supply their own local package and weights. The manager automatically scans `Downloads`, `Desktop`, and `Documents` for package folders (including extracted `AMDNR-*` folders) or `OptiScaler-AMD-PreSR-Multipass*.zip` archives (extracting zip archives safely into `%LocalAppData%\DLSS5 AMD Swapper\optiscaler-packages\<hash>`).

The manager validates:
- Target architecture is PE x64.
- `OptiScaler.dll` VersionInfo has ProductName `OptiScaler` and ProductVersion containing `amd-presr`, or the binary carries AMD-NR's `AMD-NR v` build string.
- Pass DLLs contain the internal marker `dlssnr_amd`. Each pass is looked up beside `OptiScaler.dll`, then in `Runtime\`.
- File integrity matches `SHA256SUMS.txt` when present. Every file the manager installs (the fork binary, pass DLLs, `OptiScaler.ini`, the `OptiScaler\` dependencies, the enabler, the lmxxf runtime and the weights entry) must match exactly; entries listed under `Runtime/` are checked beside `OptiScaler.dll` when no `Runtime` folder holds them. A Git LFS pointer satisfies its entry when its `oid sha256:` equals the listed hash. A stale checksum on a file the manager never installs (readme, scripts, licence texts) is reported as a warning, because the reference package itself ships with such stale entries.
- Weights are verified locally against existing generated copies (from the bridge runtime folder, Lossless Scaling folder, or managed game folders); all copies must match in SHA-256 hash. Git LFS pointer stubs and undersized files are rejected.

## Setup

1. Open **Settings** in DLSS5 AMD Swapper. Under **OptiScaler pre-SR package**, select your verified package folder or zip. Under **Generated weights**, select your locally generated `dlssnr_on_amd_weights.bin`.
2. Go to **Game library**, select a compatible x64 DirectX 12 game, and click **Set up**.
3. In the route selection dialog, choose **OptiScaler pre-SR**.
4. Select a preset: **Light**, **Balanced** (recommended), **Detail** or **Max**. Each preset also selects a scaling tier, which decides the internal render resolution.
5. Click **Set up**. The manager installs `dxgi.dll`, patches `OptiScaler.ini`, stages the pass DLLs, copies the dependencies, and records a hash-backed manifest `.dlss5-amd-swapper.json`.
6. Enable a supported temporal upscaler input in game settings. In **Assetto Corsa Rally**, select **DLSS or XeSS**: upstream disables FSR inputs for this title. The game's selected input and OptiScaler's AMD `ffx` output are separate settings.
7. Press `Del` to open the in-game OptiScaler configuration menu.
8. Check the **Diagnostics** panel in DLSS5 AMD Swapper to inspect runtime status.

## When installation makes no visible difference

Check each stage in order:

1. **Files present:** the selected executable must be the game process, such as `acr/Binaries/Win64/acr.exe`, and its required proxy, pass runtimes, weights and dependencies must still be installed. A leftover ownership manifest after Restore does not mean the runtime remains installed. Use **Update** to repair a managed installation when you intend to reinstall it.
2. **Proxy loaded:** a fresh `OptiScaler.log` and the `Del` menu establish that OptiScaler loaded. They do not establish that a neural pass ran.
3. **Supported input selected:** Assetto Corsa Rally needs DLSS or XeSS input because its FSR inputs are disabled by an upstream compatibility quirk. Keep that quirk: simply forcing FSR hooks back on can trigger the crashes it prevents. See the [upstream Assetto guide](https://github.com/OptiScaler/OptiScaler/wiki/Assetto-Corsa-Rally) and [executable-specific quirks](https://github.com/OptiScaler/OptiScaler/blob/master/OptiScaler/misc/Quirks.h).
4. **Neural work completed:** refresh evidence after entering gameplay. Completed AMD pre-SR passes or positive model timing provide execution evidence. A `DLSS-NR running` setup line, `Enabled=true`, an old successful log, or higher FPS alone does not.
5. **Compare during the same scene:** use the runtime's supported in-game controls and check its state while comparing. Saving a manager setting does not prove the running runtime applied it.

For **Cyberpunk 2077**, upstream supports DLSS, FSR and XeSS inputs. With path tracing, its guide recommends XeSS or FSR input because DLSS input forces ray reconstruction and can produce noise on AMD. Follow the [Cyberpunk guide](https://github.com/OptiScaler/OptiScaler/wiki/Cyberpunk-2077) for the active game version and settings.

This pre-SR integration requires DirectX 12. A DirectX 11 or Vulkan app drawing successfully does not establish support for this neural route; basic graphics/proxy loading and neural evaluation must be tested separately.

## Presets

The manager writes presets by patching the package's base `OptiScaler.ini` or creating a minimal
configuration if none exists. Each preset sets the neural controls and a scaling tier together,
because raising every slider to its maximum usually looks worse rather than better.

| Preset | Passes | Structure | Skin | Tone | Scaling tier |
| --- | --- | --- | --- | --- | --- |
| Light | 1 | 1.0 | 1.0 | 0 | Quality (1.5x) |
| Balanced (default) | 1 | 1.5 | 1.5 | 0 | Balanced (1.7x) |
| Detail | 2 | 2.0 | 2.0 | 0 | Performance (2.0x) |
| Max | 3 | 2.0 | 2.0 | 0.5 | Ultra Performance (3.0x) |

Each additional pass owns an independent temporal history and costs a further share of GPU frame
time, so passes trade framerate for detail.

**No preset enables frame generation.** An earlier `Performance` preset combined a 3.0x ratio with
`InterpolationCount=2`, which switched frame generation on for anyone who chose it for speed. Frame
generation is now left to OptiScaler's own overlay, where it is an explicit choice.

### Scaling tiers

Scaling is the largest image-quality lever on this route: the neural pass runs on the internal
render buffer, so a more aggressive tier gives the network fewer pixels to work with.

| Tier | Ratio | 3840x2160 output | 2560x1440 output | 1920x1080 output |
| --- | --- | --- | --- | --- |
| Game controlled | - | the game's own setting | the game's own setting | the game's own setting |
| DLAA | 1.0 | 3840x2160 | 2560x1440 | 1920x1080 |
| Ultra Quality | 1.3 | 2954x1662 | 1969x1108 | 1477x831 |
| Quality | 1.5 | 2560x1440 | 1707x960 | 1280x720 |
| Balanced | 1.7 | 2259x1271 | 1506x847 | 1129x635 |
| Performance | 2.0 | 1920x1080 | 1280x720 | 960x540 |
| Ultra Performance | 3.0 | 1280x720 | 853x480 | 640x360 |

A forced ratio replaces whatever upscaler quality the game itself offers. **Game controlled** writes
`UpscaleRatioOverrideEnabled=false` so the in-game setting stays in charge instead of being silently
overridden. The tier is recorded in the manifest so an update cannot change the resolution behind
your back, and it can also be changed live in the overlay under **Upscale Ratio Override**.

A Balanced install writes:

```ini
[Upscalers]
Dx12Upscaler=ffx

[DlssNr]
Enabled=true
RunBeforeSR=true
Passes=1
LocalTone=0
LocalStructure=1.5
SkinStructure=1.5
ApplyAfterRR=false

[Log]
LogToFile=true
LogLevel=2

[UpscaleRatio]
UpscaleRatioOverrideEnabled=true
UpscaleRatioOverrideValue=1.7
```

## In-game overlay

OptiScaler provides the live control surface; this manager only configures it. Setup writes the
following into `[Menu]`, filling in defaults only, so a key rebound inside the overlay survives a
later update or repair.

| Key | Setting | Purpose |
| --- | --- | --- |
| `Del` | `ShortcutKey=0x2E` | Opens the menu. Arrow keys and Enter navigate it. The **DLSS Neural Rendering** section enables neural rendering, selects pre-SR order, and adjusts passes, tone, structure and skin structure while the game runs. |
| `Page Up` | `FpsShortcutKey=0x21` | Toggles the compact always-on readout (`FpsOverlayType=2`), which reports the active API, upscaler and frame timing. |
| `Page Down` | `FpsCycleShortcutKey=0x22` | Cycles the readout between minimal and detailed forms. |

`OverlayMenu=true` is written explicitly because the upstream default depends on the proxy name.

AMD-NR adds its own `[DlssNr]` keys, which the manager leaves at their shipped values. `Home`
(`ToggleKey=auto`) switches neural rendering on and off for either runtime while the menu is closed,
which makes a same-scene comparison quick. `CaptureKey` is unbound by default; once bound in
`OptiScaler.ini`, it writes eight frames to an `amd-nr-capture` folder beside the game for bug reports.

Games that take over the keyboard leave the overlay unreachable; their log records `WndProc is not
subclassed` or `subclass lost`. The remedy is `ManualInputPolling=true` under `[Hotfix]`, which setup
applies automatically for `acr.exe`. Diagnostics report this condition as `input_hook_warning`.

## Diagnostics fields

The manager and CLI parse `amd_presr.log` and `OptiScaler.log` to extract:

- `pre_sr_active`: completed neural passes or positive model timing were observed in the inspected session after its latest fault. A "DLSS-NR running" setup line alone is insufficient.
- `upscaler_observed`: the game created an FidelityFX upscaler context (`ffxCreateContext_Dx12`) in this session. When this is false the game was rendering without DLSS, FSR or XeSS selected, so the pre-SR pass had nothing to run before and the image cannot change regardless of how the files were installed.
- `hip_adapter`: the AMD GPU adapter recognized by the HIP runtime.
- `passes_initialized`: number of neural passes successfully initialized (1 to 3).
- `passes_completed`: number of neural passes completed for the frame.
- `model_size`: internal render buffer dimensions fed into the neural network (such as 1280x720).
- `target_size`: output buffer dimensions reconstructed by the upscaler (such as 3840x2160).
- `mean_total_ms`: mean total cost of the neural pass per frame (model plus OptiScaler's own composition).
- `mean_model_ms`: model portion of the neural pass cost.
- `cost_samples`: number of timing samples recorded.
- `last_fault`: last recorded fault or error code.
- `presr_log_sha256`, `presr_log_bytes`, `optiscaler_log_sha256`, `optiscaler_log_bytes`: file size and SHA-256 hashes of the inspected log files.

## Restore

Restoring a game installation removes only files created by the manager that still match their recorded post-install hashes:

- Created files (`dxgi.dll`, `OptiScaler.ini`, pass DLLs, weights, the lmxxf runtime, and newly created dependencies) are removed.
- Runtime logs (`OptiScaler.log`, `amd_presr.log`, `dlssnr_on_amd.log`, `lmxxf_backend.log`) are deleted unless they existed before the install. Every game session rewrites them, so they never match an install-time hash; keeping them used to leave the manifest behind and block a later route change.
- Pre-existing files in the game directory (such as files that were already present in `OptiScaler\`) are preserved.
- If any managed file was modified outside the manager, it is retained for safety and the manifest remains in place.
- If an installation failure occurs, all staged files roll back immediately to the pre-installation state.

## Limits

- **DirectX 12 only**: The pre-SR path hooks into DirectX 12 super-resolution dispatch calls. Direct3D 11 and Vulkan are not supported.
- **HIP runtime requirement**: Requires `amdhip64_7.dll` (HIP 7 runtime included with current AMD drivers). HIP 6 alone is insufficient.
- **Render buffer constraints**: Refuses non-zero colour subrect origins and display-resolution motion vectors.
- **Anti-cheat**: Games with active anti-cheat solutions are blocked by the manager.
- **Frame generation**: no preset enables frame generation. It can be switched on in OptiScaler's own overlay, and that path requires the target game to support and call DLSS-G / Streamline frame generation.
- **Resolution changes during a session**: changing the game's own upscaler quality while playing re-initialises the AMD backend at the new render resolution. Observed on Cyberpunk 2077: the backend re-initialised from 1505x847 to 1706x960, recorded three passes and then logged `HIP completion timeout pass 1`, after which no further pass ran for the rest of the session despite `Enabled=true`. Re-selecting the previous quality does not recover it; the game must be restarted. A fixed scaling tier avoids the mid-session change entirely.
- **No runtime INI hot reload**: Hot reloading of `OptiScaler.ini` is unproven. The in-game `Del` menu is the authoritative control surface while the game is running.
- **Lossless Scaling incompatibility**: OptiScaler cannot be run inside Lossless Scaling. Lossless Scaling presents captured frames using Direct3D 11, whereas OptiScaler pre-SR requires a DirectX 12 super-resolution call.
- **Crimson Desert**: the pre-SR v1.2 proxy (SHA-256 `07a1e2ca3fbf6c9c9a2923a755603c69fabf115b0904c92f10efe95fdb2b0caa`) is blocked for Crimson Desert because it causes startup access-violation faults, and the manager migrates such installs back to the post-FSR route. This is hash-specific so a future fixed AMD pre-SR build can be tested without changing the game blacklist.

## Advanced CLI

The Python direct-game helper supports the pre-SR route. Pass `--passes N` (1 to 3) to configure the neural pass count written to `OptiScaler.ini` (defaults to 1).

### Check compatibility

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --check
```

### Install Balanced preset

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --route optiscaler-presr `
  --package "C:\Downloads\OptiScaler-AMD-PreSR-Multipass-v1.2" `
  --weights "D:\Games\Example\dlssnr_on_amd_weights.bin" `
  --preset balanced
```

### Install Max preset with an explicit scaling tier and proxy

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --route optiscaler-presr `
  --package "C:\Downloads\OptiScaler-AMD-PreSR-Multipass-v1.2" `
  --weights "D:\Games\Example\dlssnr_on_amd_weights.bin" `
  --preset max `
  --scaling ultraperformance `
  --proxy-name dxgi.dll
```

### Install from an AMD-NR folder

Extract `AMDNR-v0.3.1.zip`, then its matching `v0.3.1-Runtime.zip` into the same folder. The weights can come from that folder or from your own generated copy.

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --route optiscaler-presr `
  --package "C:\Downloads\AMDNR-v0.3.1" `
  --weights "C:\Downloads\AMDNR-v0.3.1\dlssnr_on_amd_weights.bin" `
  --preset balanced
```

### Update an existing install

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --update `
  --route optiscaler-presr `
  --package "C:\Downloads\OptiScaler-AMD-PreSR-Multipass-v1.2" `
  --weights "D:\Games\Example\dlssnr_on_amd_weights.bin" `
  --preset balanced
```

### Diagnose runtime status

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --diagnose
```

### Remove managed files

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --remove
```
