# Install and build

NR Auto Scale has a direct-game AMD route for supported DX12/FSR games and a separate Lossless Scaling compatibility route.

## Direct-game AMD route

Use this path first for a supported single-player/offline game. Download `dlssnr_on_amd_setup.exe` yourself from the [official DLSS-NR-on-AMD releases](https://github.com/danielblnc/DLSS-NR-on-AMD/releases), and provide your own legitimate `nvngx_dlssnr.dll`.

Check the target:

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

The helper requires x64 plus FSR and DirectX 12 evidence, blocks common anti-cheat targets, verifies the upstream setup against GitHub's published SHA-256 digest, verifies the created runtime state and rich FSR/depth/temporal configuration, and rolls back partial changes if setup fails. The upstream installer and NVIDIA DLL stay user-supplied and private.

The current upstream requirement is **AMD Software: Adrenalin Edition 26.1.1 or newer**. Do not install a separate ROCm/HIP stack for the direct-game route; the compatibility runtime uses the HIP runtime delivered with the AMD driver.

See [Direct-game AMD route](../direct-game/README.md) for update, diagnose and removal commands.

## Lossless Scaling route

On the `experimental-soft-cheat` branch, the visible output stays at the captured source resolution, the neural branch is capped to **480 pixels high** by default, and the dev.14 stable neural residual is combined with a uniform local-clarity/dehaze-style image filter.

## Install the preview

1. Install Lossless Scaling from its official store page.
2. Download the latest preview ZIP from <https://github.com/eikkapine/NR-Auto-Scale/releases>.
3. Extract the ZIP somewhere outside the Lossless Scaling folder.
4. Run:

   ```powershell
   .\Setup.cmd
   ```

5. Pick the Lossless Scaling install folder when prompted.
6. Supply your own local AMD compatibility proxy and NVIDIA DLSS-NR DLL when prompted. Use the AMD driver version required by that compatibility runtime; do not add a second HIP/ROCm installation unless its upstream documentation explicitly requires one.
7. Focus a capturable game/browser/video window and press **Scale** in Lossless Scaling.

The release ZIP does not contain NVIDIA DLLs, AMD proxy binaries, model files, HIP installers, paid Lossless Scaling files, private logs or local configuration.

## Default processing mode

Fresh setup writes:

```ini
NativeResolution=0
WorkingScale=0
NeuralMaxHeight=480
```

`NeuralMaxHeight=480` takes precedence. The bridge captures the source at its real size, creates a reduced neural texture with the same aspect ratio, and keeps the visible bridge at the source size.

Examples:

| Source | Neural branch | Visible bridge |
| ---: | ---: | ---: |
| 2560×1440 | ~854×480 | 2560×1440 |
| 1920×1080 | ~854×480 | 1920×1080 |
| 1440×1080 | 640×480 | 1440×1080 |

Sources below the cap are not enlarged for neural processing.

The legacy modes remain available:

```ini
# Reduced source-relative mode
NeuralMaxHeight=0
WorkingScale=0.75

# Full 1:1 neural processing
NeuralMaxHeight=0
WorkingScale=0
NativeResolution=1

# Fixed fallback
NeuralMaxHeight=0
WorkingScale=0
NativeResolution=0
Width=1280
Height=720
```

If you already have an installation, stop scaling before changing `NrAutoScale.ini` or replacing the bridge executable. Do not replace your Lossless Scaling profile or neural runtime INI just to update the project.

## Runtime controls

| Hotkey | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle processed output on/off |
| `Ctrl+Alt+F7` | Decrease strength |
| `Ctrl+Alt+F8` | Increase strength |

The experimental clarity mode supports `0.0..4.0` strength. The baseline is `1.0`.

## Runtime requirements

- Windows 11
- Lossless Scaling installed from an official source
- AMD Radeon GPU; development is focused on RX 9070 XT / RDNA4
- User-supplied AMD compatibility proxy
- User-supplied NVIDIA DLSS-NR DLL/model payload required by that proxy
- AMD driver/runtime required by the chosen compatibility runtime
- Visual C++ x64 runtime when required by the built binaries

## Build from source

Source builds additionally require CMake 3.20+, MSVC C++ build tools and a Windows SDK with the required WGC/D3D headers.

Build the proxy:

```powershell
.\auto-scale\build.ps1
```

Build the bridge:

```powershell
.\bridge\build.ps1
```

The project-built proxy artifact is also named `Lossless.dll`; it is separate from the paid application's original DLL.

## Developer setup

The source-tree wrapper is:

```powershell
.\auto-scale\scripts\Setup.cmd
```

For explicit private paths:

```powershell
.\auto-scale\scripts\Install-AutoScale.ps1 `
  -LsDir "<Lossless Scaling install folder>" `
  -ProxyVersionSource "<private AMD proxy version.dll>" `
  -NrSource "<private nvngx_dlssnr.dll>" `
  -NeuralMaxHeight 480 `
  -WorkingScale 0
```

The installer privately preserves the original Lossless Scaling DLL as `Lossless_original.dll` so the project proxy can forward to it. That paid original must never be copied into the public repository or release ZIP.

## Uninstall

Use the release/source uninstall script while scaling is stopped:

```powershell
.\scripts\Uninstall-AutoScale.ps1
```

or, from the source tree:

```powershell
.\auto-scale\scripts\Uninstall-AutoScale.ps1
```

## Public release boundary

Do not publish:

- paid Lossless Scaling binaries or app assets
- `Lossless_original.dll`
- AMD compatibility proxy/runtime binaries
- NVIDIA DLLs, models or weights
- private INIs, logs, backups, captures or machine-specific paths

The public ZIP contains only project-built artifacts, setup/uninstall scripts, approved existing comparison images and documentation.
