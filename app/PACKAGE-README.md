# DLSS5 AMD Swapper

Portable Windows build for AMD Neural Rendering with two routes:

- **Direct game** for supported x64 DX12/FSR games.
- **Lossless Scaling** for capturable games, video, browsers and other windows.

Download the Windows release ZIP from [GitHub Releases](https://github.com/eikkapine/DLSS5-AMD-Swapper/releases), choose **Extract All**, and open the extracted folder. GitHub's **Source code** archives contain source, not the runnable app. The Windows x64 release includes .NET; no developer tools are needed.

Run `Dlss5AmdSwapper.exe` for portable use, or run `Install.cmd` to install for your Windows account with Desktop and Start menu shortcuts. Installation verifies every packaged file, stages the new version, and keeps the previous installation for rollback. It does not require administrator access or change any game until you select one and choose **Set up**.

Use **Scan PC** to discover games from Steam, Epic, GOG, EA, Ubisoft, Battle.net, Xbox/Game Pass and common standalone install folders. The scan finishes with a verification summary and leaves the library unselected so you choose the target yourself. Discovered games stay visible even when they fail the direct-game compatibility check.

For the supported upstream direct-game route, **Set up** resolves install vs update, downloads and SHA-256 verifies the official DLSS-NR-on-AMD setup, and searches this PC for a valid local `nvngx_dlssnr.dll`. If that private NVIDIA runtime cannot be found, the manager asks you to select your own copy; it is never included in this package. The OptiScaler pre-SR route instead requires a compatible local OptiScaler AMD pre-SR multipass package; the manager can find it in Downloads or you can select it in settings.

**Installed does not mean active.** A game must use the supported graphics API and upscaler path. Launch the game, enable a supported upscaler, render a scene, then use **Refresh evidence**. Installation receipts alone cannot confirm that neural rendering changed the image; see `INSTALL.md` if nothing changes.

The window closes the app by default on a fresh setup. Keeping it in the tray is optional in Settings; updates retain your existing choice.

## Controls

Manager hotkeys write to the game's configuration file. They do **not** change a running game, and they are not global: Lossless Scaling owns them while its bridge is active, and the manager registers them only while a managed direct-game target is running.

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Save the direct-game on/off state for the next launch |
| `Ctrl+Alt+F7` | Save decreased strength for the next launch |
| `Ctrl+Alt+F8` | Save increased strength for the next launch |

In-game controls come from the runtime itself. These are the ones that change anything while you play:

| Key | Route | Action |
| --- | --- | --- |
| `Del` | OptiScaler pre-SR | Overlay: neural on/off, passes, tone, structure, skin structure |
| `Page Up` | OptiScaler pre-SR | Toggle the status readout |
| `Page Down` | OptiScaler pre-SR | Cycle readout detail |
| `End` | Official AMD runtime | Upstream status and live toggle |

Each additional neural pass costs roughly a proportional share of GPU frame time, so raising passes lowers framerate.

The direct-game page saves settings but does not claim that a running game applied them. Each route's own in-game overlay is the authoritative live surface: press `Del` for the OptiScaler pre-SR menu (arrow keys and Enter, including live neural passes, tone, structure and skin structure), `Page Up` for its compact status readout, or `End` for the official AMD runtime. Setup configures these keys; see `INSTALL.md`.

Setup offers **Light**, **Balanced**, **Detail** and **Max** presets. Each one also picks a scaling tier, which sets the internal render resolution and is the largest image-quality control on the OptiScaler pre-SR route. No preset enables frame generation. `INSTALL.md` has the full preset and scaling tables.

See `INSTALL.md` for setup and `THIRD-PARTY.md` for the runtime/licensing boundary.

This ZIP contains only the DLSS5 AMD Swapper manager and project-owned bridge/wrapper/scripts. It does not include paid Lossless Scaling files, DLSS-NR-on-AMD, NVIDIA DLSS-NR files, generated weights or third-party AMD proxy/runtime binaries.
