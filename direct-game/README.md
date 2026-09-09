# Direct-game AMD route

This route places Neural Rendering inside a supported game's FSR/DX12 path so it can use render-resolution colour and temporal guide data that a desktop capture cannot see.

I use it mainly for supported single-player/offline 64-bit games. Common anti-cheat targets are blocked automatically.

## Manager workflow

The normal workflow is through `Dlss5AmdSwapper.exe`:

1. Select your official `dlssnr_on_amd_setup.exe` and your own `nvngx_dlssnr.dll` under **Settings**.
2. Scan Steam or add a game executable.
3. Let the compatibility probe check x64, FSR, DX12 and anti-cheat markers.
4. Click **Install / Update**.
5. Launch the game and inspect the runtime evidence from the manager.
6. Use **Restore** when you want to remove the managed install.

The manager verifies the upstream setup against GitHub release metadata before it modifies the game directory. It then verifies that the setup produced a proxy, config and weights and that the config enables FSR inputs, depth, temporal history, interop and inline gameplay mode.

New managed installs write `.dlss5-amd-swapper.json`. The older `.nr-auto-scale-direct.json` format is still accepted for compatibility and migrates on a successful update.

## Runtime controls

`Ctrl+Alt+F6` toggles the configured effect and `Ctrl+Alt+F7/F8` decrease/increase strength for the active managed game. A setting change is only shown as live when the runtime log acknowledges it. The upstream **End** overlay remains the authoritative in-game live control/status surface.

## Advanced CLI

Check a target:

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --check
```

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

Diagnose:

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --diagnose
```

Remove:

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --remove
```

Removal only deletes managed files that were absent before installation and still match the hashes recorded after installation. A changed file is preserved for safety. `nvngx_dlssnr.dll` is kept by default; `--remove-model` removes it only when this helper originally copied it and its hash still matches.

## Third-party boundary

This repository does not contain, download, patch or redistribute `DLSS-NR-on-AMD` or NVIDIA runtime/model files. Download the official setup yourself from [DLSS-NR-on-AMD releases](https://github.com/danielblnc/DLSS-NR-on-AMD/releases) and supply your own legitimate `nvngx_dlssnr.dll`.

For the currently tested route, use AMD Software: Adrenalin Edition 26.1.1 or newer. A separate ROCm installation is not required for normal use; the compatibility runtime uses the HIP runtime supplied with the AMD driver.
