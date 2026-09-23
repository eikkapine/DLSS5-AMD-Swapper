# Install / setup

## Get the Windows app

1. Download the Windows x64 app ZIP from [GitHub Releases](https://github.com/eikkapine/DLSS5-AMD-Swapper/releases). Do not choose a **Source code** archive.
2. Right-click the ZIP, choose **Extract All**, and keep all its files together in a new folder. Running a single EXE or installer from inside the ZIP can omit required payload files.
3. Run `Install.cmd` for a per-user installation, or `Dlss5AmdSwapper.exe` for portable use. No .NET SDK or administrator access is required for installing the manager.

The installed manager is at `%LOCALAPPDATA%\Programs\DLSS5 AMD Swapper`. Settings and downloaded runtime packages are stored separately at `%LOCALAPPDATA%\DLSS5 AMD Swapper` and survive app updates. Close the manager before updating; setup copies and checks the entire new version before switching the installation. The previous directory is retained beside it as `DLSS5 AMD Swapper.previous-...`, including any extra files you placed there.

For an unattended installation that does not open the app, run `Install.cmd -NoLaunch`. Add `-NoShortcuts` to skip Desktop and Start menu shortcuts. To check extraction and SHA-256 integrity without installing anything, run `Install.cmd -VerifyOnly`. The checksum manifest detects changed or incomplete package content; it is not a publisher signature.

## Direct-game route

1. Run `Dlss5AmdSwapper.exe`.
2. Open **Game library** and press **Scan PC**, or add a game executable manually.
3. Review the scan summary, then select a compatible x64 DX12/FSR game.
4. Review the selected route and click **Set up**. For the upstream direct-game route, the manager downloads the official `dlssnr_on_amd_setup.exe`, verifies the GitHub-published size and SHA-256, and decides whether the game needs an install or update. For OptiScaler pre-SR, select a compatible local AMD pre-SR multipass package in settings, or use **Find in Downloads**. Ordinary OptiScaler alone does not provide the neural pre-SR passes.
5. If the selected route needs `nvngx_dlssnr.dll`, the manager searches for an existing legitimate local copy. If it cannot find one, it asks you to select your own copy. That DLL is never downloaded or redistributed by this project.
6. Launch the game, choose a supported upscaler in its graphics settings, render a scene, and use **Refresh evidence** to check runtime activity. Follow any route-specific instructions shown by the manager.

The scanner never selects a game automatically. You choose the target before any game folder is changed.

Common anti-cheat targets are blocked automatically. The direct route is intended mainly for supported single-player/offline games.

## If installation appears to make no difference

- Confirm the selected executable is the actual renderer, not a launcher. Unreal games commonly render from a nested `Binaries\Win64` executable.
- Check the game's active graphics API and upscaler. Placing DLLs beside a DX11, DX12 or Vulkan executable cannot make an unsupported render path compatible.
- Turn an upscaler on in the game itself. The pre-SR pass runs *before* super-resolution, so with the game set to TAA or native it has nothing to run before and the image cannot change, however complete the install looks. Evidence reports `upscaler_observed` for this; **Refresh evidence** says so in plain words.
- Restart the game after installing or updating. Existing processes may keep the previous DLL loaded.
- Use **Refresh evidence** after rendering a scene. Read the reported module/log evidence and any configuration issues; a successful file copy is not proof that the rendering passes ran.
- For the supported upstream route, use the in-game `End` overlay to confirm status. For OptiScaler pre-SR, use its in-game overlay (see below). Compare the same scene and settings with the effect switched on and off only after activity is confirmed.

## Supplying the files this app does not include

Three files are never downloaded or bundled: the OptiScaler AMD pre-SR package, the generated weights, and `nvngx_dlssnr.dll`. Setup looks for them automatically. When that fails, put the file in one of the folders below and use the refresh or **Find in Downloads** action, or select it directly in Settings.

| File | Searched automatically in |
| --- | --- |
| OptiScaler AMD pre-SR package (folder or `.zip`, or an extracted `AMDNR-*` folder with its matching Runtime zip extracted into it) | `Downloads`, `Desktop` and `Documents`, two folder levels deep |
| `dlssnr_on_amd_weights.bin` | the path set in Settings, the Lossless Scaling `nr-bridge\runtime` folder, any managed game folder, the package folder, then `Downloads`, `Desktop` and `Documents` |
| `nvngx_dlssnr.dll` | the selected game's folder and its neighbours, then `Downloads`, `Desktop` and `Documents` |

The real weights file is roughly 141 MB. A Git LFS pointer stub of a few hundred bytes is rejected and reported as missing, so check the file size if a copy you placed is not picked up. Weights are produced by running the official AMD runtime setup once; this project does not distribute them.

## Presets and scaling

Setup offers four presets for the OptiScaler pre-SR route. Each sets the neural controls and a scaling tier together, because raising every slider to its maximum usually looks worse rather than better.

| Preset | Passes | Structure | Skin | Tone | Scaling |
| --- | --- | --- | --- | --- | --- |
| Light | 1 | 1.0 | 1.0 | 0 | Quality (1.5x) |
| Balanced (default) | 1 | 1.5 | 1.5 | 0 | Balanced (1.7x) |
| Detail | 2 | 2.0 | 2.0 | 0 | Performance (2.0x) |
| Max | 3 | 2.0 | 2.0 | 0.5 | Ultra Performance (3.0x) |

Every extra neural pass costs GPU frame time, so more passes trade framerate for detail. No preset switches frame generation on; that stays an explicit choice inside the in-game overlay.

**Scaling decides the internal render resolution**, and it is the largest image-quality control on this route. The neural pass runs on the internal buffer, so a more aggressive tier leaves the network fewer pixels to work with.

| Tier | Ratio | 3840x2160 output | 2560x1440 output | 1920x1080 output |
| --- | --- | --- | --- | --- |
| DLAA | 1.0 | 3840x2160 | 2560x1440 | 1920x1080 |
| Ultra Quality | 1.3 | 2954x1662 | 1969x1108 | 1477x831 |
| Quality | 1.5 | 2560x1440 | 1707x960 | 1280x720 |
| Balanced | 1.7 | 2259x1271 | 1506x847 | 1129x635 |
| Performance | 2.0 | 1920x1080 | 1280x720 | 960x540 |
| Ultra Performance | 3.0 | 1280x720 | 853x480 | 640x360 |

A tier forces the render resolution and replaces the upscaler quality setting chosen inside the game. Choose **Game controlled** to leave that setting in charge instead. The chosen tier is recorded in the install manifest, so an update cannot change your resolution silently, and it can also be changed while playing from the overlay's **Upscale Ratio Override** section.

Both the preset and the scaling tier can be changed **after** setup: select the game in **Game library** and use the **Preset** and **Scaling** controls under *Effect controls*. They are written to `OptiScaler.ini` and apply the next time the game starts. A configuration that does not match any preset is shown as *Custom*.

**Changing the game's own upscaler quality while it is running can stop neural rendering.** The AMD backend re-initialises at the new render resolution and can then fail with `HIP completion timeout` in `amd_presr.log`, after which the neural pass no longer runs even though it still reports as enabled. Re-selecting the previous quality does not recover it; restart the game. Picking a fixed scaling tier here instead of **Game controlled** avoids the mid-session resolution change that triggers this.

## In-game overlay

Each route has its own in-game overlay; both are provided by the runtime itself, not by this manager.

| Route | Key | What it shows |
| --- | --- | --- |
| OptiScaler pre-SR | `Del` | Full menu, navigated with the arrow keys and Enter. The **DLSS Neural Rendering** section toggles neural rendering and adjusts passes, tone, structure and skin structure live, and reports completed AMD pre-SR passes. |
| OptiScaler pre-SR | `Page Up` | Compact always-on status readout; `Page Down` cycles how much detail it shows. |
| Official AMD runtime | `End` | Upstream status and live toggle. |

Setup writes these keys into `OptiScaler.ini`, so they work without any extra configuration. Changes you make in the overlay are saved back to that file, and updating or repairing the game keeps the keys you rebound.

If `Del` does nothing, the game is overriding the keyboard hook; the log records `WndProc is not subclassed`. Set `ManualInputPolling=true` under `[Hotfix]` in `OptiScaler.ini` and restart the game. Setup applies this automatically for Assetto Corsa Rally, which is known to need it.

Fresh downloads exit when the window closes. You can enable **Keep running in the tray when I close the window** in Settings; use the tray's **Exit** action to quit in that mode. Updates preserve your saved preference.

The release includes `SpecialK.deny.Dlss5AmdSwapper`, an application-specific marker that opts the manager out of Special K's graphics hooks. Keep it beside the manager EXE. It does not disable Special K in your games or require Special K to be installed. This prevents the overlay interference observed during shutdown testing.

## Lossless Scaling route

1. Install Lossless Scaling from an official source.
2. In DLSS5 AMD Swapper, open **Lossless Scaling**.
3. Select your local AMD compatibility proxy named `version.dll`.
4. Select your own `nvngx_dlssnr.dll`.
5. Click **Install / Update bridge**.
6. Focus a capturable window and press **Scale** in Lossless Scaling normally.

The visible output stays at the captured source resolution. By default only the hidden neural branch is capped to `480p` height.

## Remove / restore

- Direct game: select the game and click **Restore**. Changed files are preserved rather than deleted blindly.
- Lossless Scaling: open its page and click **Uninstall** to restore the managed local setup.
- Manager: close it, delete its directory under `%LOCALAPPDATA%\Programs` and its Desktop/Start menu shortcuts. To roll back an update, move the current manager directory aside and rename the desired `DLSS5 AMD Swapper.previous-...` directory to `DLSS5 AMD Swapper`. These operations do not restore game changes; use the route's restore/uninstall action first if that is your intent.

For the currently tested direct-game upstream path, use AMD Software: Adrenalin Edition 26.1.1 or newer. A separate ROCm installation is not required for normal use.
