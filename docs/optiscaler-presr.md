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

### Provenance and package boundary

DLSS5 AMD Swapper does not download, redistribute, or bundle this package:

- **OptiScaler** is licensed under GPL-3.0 ([cdozdil/OptiScaler](https://github.com/cdozdil/OptiScaler)). Because the pre-SR fork's source code has not been published, its binaries cannot be redistributed with GPL-3.0 source obligations satisfied.
- **DLSS-NR-on-AMD** permits personal, non-commercial use and explicitly forbids redistribution and modification. Renamed pass proxy DLLs cannot be redistributed.
- **Weights** derive from NVIDIA's neural model and cannot be redistributed.
- **Vodkaman23/DLSS-NR-UE5-Opti-DLL** has no published license.

Users must supply their own local package and weights. The manager automatically scans `Downloads`, `Desktop`, and `Documents` for package folders or zip archives (extracting zip archives safely into `%LocalAppData%\DLSS5 AMD Swapper\optiscaler-packages\<hash>`).

The manager validates:
- Target architecture is PE x64.
- `OptiScaler.dll` VersionInfo has ProductName `OptiScaler` and ProductVersion containing `amd-presr`.
- Pass DLLs contain the internal marker `dlssnr_amd`.
- File integrity matches `SHA256SUMS.txt` when present.
- Weights are verified locally against existing generated copies (from the bridge runtime folder, Lossless Scaling folder, or managed game folders); all copies must match in SHA-256 hash. Git LFS pointer stubs and undersized files are rejected.

## Setup

1. Open **Settings** in DLSS5 AMD Swapper. Under **OptiScaler pre-SR package**, select your verified package folder or zip. Under **Generated weights**, select your locally generated `dlssnr_on_amd_weights.bin`.
2. Go to **Game library**, select a compatible x64 DirectX 12 game, and click **Set up**.
3. In the route selection dialog, choose **OptiScaler pre-SR**.
4. Select a preset: **Quality** (upscaling only) or **Performance** (ratio override and frame generation).
5. Click **Set up**. The manager installs `dxgi.dll`, patches `OptiScaler.ini`, stages the pass DLLs, copies the dependencies, and records a hash-backed manifest `.dlss5-amd-swapper.json`.
6. Launch the game with FSR / super-resolution enabled in game settings.
7. Press `Insert` to open the in-game OptiScaler configuration menu.
8. Check the **Diagnostics** panel in DLSS5 AMD Swapper to inspect runtime status.

## Presets

The manager writes presets by patching the package's base `OptiScaler.ini` or creating a minimal configuration if none exists.

### Quality preset

Focuses on neural rendering before standard super-resolution reconstruction without overriding game aspect ratios or enabling frame generation:

```ini
[Upscalers]
Dx12Upscaler=ffx

[DlssNr]
Enabled=true
RunBeforeSR=true
Passes=1
LocalTone=0
LocalStructure=1
SkinStructure=1
ApplyAfterRR=false

[Log]
LogToFile=true
LogLevel=2
```

### Performance preset

Adds a 3.0x upscale ratio override and multi-frame generation:

```ini
[Upscalers]
Dx12Upscaler=ffx

[DlssNr]
Enabled=true
RunBeforeSR=true
Passes=1
LocalTone=0
LocalStructure=1
SkinStructure=1
ApplyAfterRR=false

[Log]
LogToFile=true
LogLevel=2

[UpscaleRatio]
UpscaleRatioOverrideEnabled=true
UpscaleRatioOverrideValue=3.0

[FrameGen]
Enabled=true
FGInput=nvngxfg
FGNvngxReplacement=combo

[DLSSG]
InterpolationCount=2
```

If `dlss-enabler-headless.dll` is absent from the package directory, `FGNvngxReplacement` falls back to `ffx`.

## Diagnostics fields

The manager and CLI parse `amd_presr.log` and `OptiScaler.log` to extract:

- `pre_sr_active`: true when the pre-SR log reports completed passes or OptiScaler.log reports "DLSS-NR running".
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

- Created files (`dxgi.dll`, `OptiScaler.ini`, pass DLLs, weights, and newly created dependencies) are removed.
- Pre-existing files in the game directory (such as files that were already present in `OptiScaler\`) are preserved.
- If any managed file was modified outside the manager, it is retained for safety and the manifest remains in place.
- If an installation failure occurs, all staged files roll back immediately to the pre-installation state.

## Limits

- **DirectX 12 only**: The pre-SR path hooks into DirectX 12 super-resolution dispatch calls. Direct3D 11 and Vulkan are not supported.
- **HIP runtime requirement**: Requires `amdhip64_7.dll` (HIP 7 runtime included with current AMD drivers). HIP 6 alone is insufficient.
- **Render buffer constraints**: Refuses non-zero colour subrect origins and display-resolution motion vectors.
- **Anti-cheat**: Games with active anti-cheat solutions are blocked by the manager.
- **Frame generation requirements**: The performance preset requires the target game to support and call DLSS-G / Streamline frame generation.
- **No runtime INI hot reload**: Hot reloading of `OptiScaler.ini` is unproven. The in-game `Insert` menu is the authoritative control surface while the game is running.
- **Lossless Scaling incompatibility**: OptiScaler cannot be run inside Lossless Scaling. Lossless Scaling presents captured frames using Direct3D 11, whereas OptiScaler pre-SR requires a DirectX 12 super-resolution call.

## Advanced CLI

The Python direct-game helper supports the pre-SR route. Pass `--passes N` (1 to 3) to configure the neural pass count written to `OptiScaler.ini` (defaults to 1).

### Check compatibility

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --check
```

### Install Quality preset

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --route optiscaler-presr `
  --package "C:\Downloads\OptiScaler-AMD-PreSR-Multipass-v1.2" `
  --weights "D:\Games\Example\dlssnr_on_amd_weights.bin" `
  --preset quality
```

### Install Performance preset with explicit proxy

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --route optiscaler-presr `
  --package "C:\Downloads\OptiScaler-AMD-PreSR-Multipass-v1.2" `
  --weights "D:\Games\Example\dlssnr_on_amd_weights.bin" `
  --preset performance `
  --proxy-name dxgi.dll
```

### Update an existing install

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --update `
  --route optiscaler-presr `
  --package "C:\Downloads\OptiScaler-AMD-PreSR-Multipass-v1.2" `
  --weights "D:\Games\Example\dlssnr_on_amd_weights.bin" `
  --preset quality
```

### Diagnose runtime status

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --diagnose
```

### Remove managed files

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --remove
```
