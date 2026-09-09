# DLSS5 AMD Swapper

Portable Windows build for AMD Neural Rendering with two routes:

- **Direct game** for supported x64 DX12/FSR games.
- **Lossless Scaling** for capturable games, video, browsers and other windows.

Run `Dlss5AmdSwapper.exe`. No installer is required for the manager itself.

Use **Scan PC** to discover games from Steam, Epic, GOG, EA, Ubisoft, Battle.net, Xbox/Game Pass and common standalone install folders. The scan finishes with a verification summary and leaves the library unselected so you choose the target yourself. Discovered games stay visible even when they fail the direct-game compatibility check.

For a compatible direct-game target, **Set up** automatically resolves install vs update, downloads and SHA-256 verifies the current official DLSS-NR-on-AMD setup, and searches this PC for a valid local `nvngx_dlssnr.dll`. If that private NVIDIA runtime cannot be found, the manager asks you to select your own copy; it is never included in this package.

## Controls

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle effect |
| `Ctrl+Alt+F7` | Decrease strength |
| `Ctrl+Alt+F8` | Increase strength |

The direct-game page reports a setting as live only when the runtime log acknowledges it. The upstream `End` overlay remains the authoritative in-game status/control surface.

See `INSTALL.md` for setup and `THIRD-PARTY.md` for the runtime/licensing boundary.

This ZIP contains only the DLSS5 AMD Swapper manager and project-owned bridge/wrapper/scripts. It does not include paid Lossless Scaling files, DLSS-NR-on-AMD, NVIDIA DLSS-NR files, generated weights or third-party AMD proxy/runtime binaries.
