# Direct-game AMD route

This route places Neural Rendering inside a supported game's FSR/DX12 path so it can use render-resolution colour and temporal guide data that a desktop capture cannot see.

I use it mainly for supported single-player/offline 64-bit games. Common anti-cheat targets are blocked automatically. Two execution backends are available:

1. **Official post-FSR runtime** (`amd-fsr-direct`): uses the official `DLSS-NR-on-AMD` setup and evaluates the neural model on the reconstructed output buffer.
2. **OptiScaler pre-SR route** (`amd-optiscaler-presr`): evaluates the neural model on the unscaled render buffer before super-resolution (FSR (ffx)) scaling, with optional frame generation.

## Manager workflow

The normal workflow is through `Dlss5AmdSwapper.exe`:

1. Under **Settings**, select your official `dlssnr_on_amd_setup.exe` and `nvngx_dlssnr.dll` (for the post-FSR route) or specify your `OptiScaler-AMD-PreSR-Multipass-v1.2` package folder/zip and weights file (for the pre-SR route).
2. Scan Steam or add a game executable.
3. Let the compatibility probe check x64, FSR, DX12 and anti-cheat markers.
4. Click **Set up** and select your preferred route and preset.
5. Launch the game and inspect the runtime evidence from the manager.
6. Use **Restore** when you want to remove the managed install.

The manager verifies the upstream setup or OptiScaler package before it modifies the game directory. It then records a hash-backed local manifest (`.dlss5-amd-swapper.json`, schema 3) for safe update and rollback behavior. The older `.nr-auto-scale-direct.json` format is still accepted for compatibility and migrates on update.

## Runtime controls

`Ctrl+Alt+F6` toggles the configured effect and `Ctrl+Alt+F7/F8` decrease/increase strength for the active managed game. A setting change is only shown as live when the runtime log acknowledges it. For the pre-SR route, the in-game **Insert** menu is the authoritative live control surface.

## Advanced CLI

Check a target:

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --check
```

### Official post-FSR route

Install:

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --upstream-setup "C:\Downloads\dlssnr_on_amd_setup.exe" `
  --nr-dll "C:\MyDlls\nvngx_dlssnr.dll"
```

Update:

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --update `
  --upstream-setup "C:\Downloads\dlssnr_on_amd_setup.exe" `
  --nr-dll "C:\MyDlls\nvngx_dlssnr.dll"
```

### OptiScaler pre-SR route

Pass `--passes N` (1 to 3) to configure the neural pass count written to `OptiScaler.ini` (defaults to 1).

Install (Quality preset):

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --route optiscaler-presr `
  --package "C:\Downloads\OptiScaler-AMD-PreSR-Multipass-v1.2" `
  --weights "D:\Games\Example\dlssnr_on_amd_weights.bin" `
  --preset quality
```

Install (Performance preset with explicit proxy):

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

Update:

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --update `
  --route optiscaler-presr `
  --package "C:\Downloads\OptiScaler-AMD-PreSR-Multipass-v1.2" `
  --weights "D:\Games\Example\dlssnr_on_amd_weights.bin" `
  --preset quality
```

### Common operations

Diagnose:

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --diagnose
```

Remove:

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --remove
```

Removal only deletes managed files that were absent before installation and still match the hashes recorded after installation. A changed file is preserved for safety. For the post-FSR route, `nvngx_dlssnr.dll` is kept by default; `--remove-model` removes it only when this helper originally copied it and its hash still matches.

## Third-party boundary

This repository does not contain, download, patch or redistribute `DLSS-NR-on-AMD`, OptiScaler fork binaries, or NVIDIA runtime/model files. Download the official setup yourself from [DLSS-NR-on-AMD releases](https://github.com/danielblnc/DLSS-NR-on-AMD/releases) or supply your own verified OptiScaler package and legitimate local `nvngx_dlssnr.dll`.

For the currently tested route, use AMD Software: Adrenalin Edition 26.1.1 or newer. A separate ROCm installation is not required for normal use; the compatibility runtime uses the HIP runtime supplied with the AMD driver (`amdhip64_7.dll`).
