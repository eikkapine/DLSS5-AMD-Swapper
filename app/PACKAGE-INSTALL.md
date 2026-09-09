# Install / setup

## Direct-game route

1. Run `Dlss5AmdSwapper.exe`.
2. Open **Game library** and press **Scan PC**, or add a game executable manually.
3. Review the scan summary, then select a compatible x64 DX12/FSR game.
4. Click **Set up**. DLSS5 AMD Swapper automatically downloads the current official `dlssnr_on_amd_setup.exe`, verifies the GitHub-published size and SHA-256, and decides whether the game needs an install or update.
5. The manager searches for an existing legitimate local `nvngx_dlssnr.dll`. If it cannot find one, it asks you to select your own local copy once. That DLL is never downloaded or redistributed by this project.
6. Launch the game and use **Refresh evidence** to check the runtime path.

The scanner never selects a game automatically. You choose the target before any game folder is changed.

Common anti-cheat targets are blocked automatically. The direct route is intended mainly for supported single-player/offline games.

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

For the currently tested direct-game upstream path, use AMD Software: Adrenalin Edition 26.1.1 or newer. A separate ROCm installation is not required for normal use.
