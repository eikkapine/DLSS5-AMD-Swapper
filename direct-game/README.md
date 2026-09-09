# Direct-game AMD route

This route is for 64-bit DirectX 12 games that expose an FSR path. It runs Neural Rendering inside the game integration instead of reconstructing from a finished desktop frame, so the runtime can use the game/upscaler data that the Lossless Scaling bridge cannot see.

The helper does **not** contain, download, redistribute, extract, patch or modify `DLSS-NR-on-AMD`. Its current licence forbids redistribution/bundling. Download `dlssnr_on_amd_setup.exe` yourself from the [official release page](https://github.com/danielblnc/DLSS-NR-on-AMD/releases), and supply your own legitimately obtained `nvngx_dlssnr.dll`. The helper verifies the setup file against the SHA-256 digest published by GitHub before it will install anything.

## Check a game

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --check
```

The first route requires an x64 executable plus FSR and DirectX 12 evidence. Targets containing common anti-cheat markers are blocked; this route is intended for single-player/offline use.

## Install

```powershell
py .\direct-game\amd_dlss5.py `
  --game "D:\Games\Example\Game.exe" `
  --install `
  --upstream-setup "C:\Downloads\dlssnr_on_amd_setup.exe" `
  --nr-dll "C:\MyDlls\nvngx_dlssnr.dll"
```

The helper:

- validates the game executable and nearby FSR/anti-cheat markers;
- verifies the upstream setup binary against the latest official GitHub release digest;
- copies only the user-supplied files into the local game folder;
- runs the upstream setup from the game folder;
- verifies that a proxy, config and generated weights were created;
- verifies that the generated config enabled FSR inputs, depth, temporal history, zero-copy interop support and inline gameplay mode;
- restores the exact pre-install managed-file state if setup fails;
- writes `.nr-auto-scale-direct.json` locally so removal is hash-checked and reversible.

The local manifest, model DLL, upstream installer, runtime logs and generated weights are private runtime files and are ignored by Git.

## Update

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --update `
  --upstream-setup "C:\Downloads\dlssnr_on_amd_setup.exe" `
  --nr-dll "C:\MyDlls\nvngx_dlssnr.dll"
```

## Diagnose

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --diagnose
```

Diagnostics summarize hashes, the generated rich-input config, FidelityFX dispatch activity, color/motion/depth staging, HIP/runtime timing and fault/error counts without copying raw log text or local paths into the report.

`--diagnose` also compares the logged FSR input size with the swapchain size. If they match, the neural pass is running at full output resolution and the report recommends switching the game from an AA/native FSR mode to an FSR quality/upscaling mode when lower processing cost is the goal.

## AMD driver / HIP requirement

Do not install a separate ROCm stack for this route. `DLSS-NR-on-AMD` currently requires **AMD Software: Adrenalin Edition 26.1.1 or newer** and uses the HIP runtime supplied by the AMD driver. `--diagnose` reports the HIP driver/runtime IDs that were actually loaded.

For performance, the important resolution is the game's **FSR input/render resolution**, not the final display resolution. Keep the display at native resolution and use the game's FSR quality mode when you want the neural network to run on a smaller render-resolution image before FSR reconstructs the native output.

## Remove

```powershell
py .\direct-game\amd_dlss5.py --game "D:\Games\Example\Game.exe" --remove
```

Removal only deletes files that were absent before the managed install and still match the hashes recorded after installation. The supplied `nvngx_dlssnr.dll` is kept by default. Add `--remove-model` to remove it too when this helper originally copied it and its hash still matches.
