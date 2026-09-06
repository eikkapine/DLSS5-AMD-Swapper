# NR Auto Scale

[![Release](https://img.shields.io/badge/Release-v0.1.0--pre.1-blue.svg)](https://github.com/eikkapine/NR-Auto-Scale/releases/tag/v0.1.0-pre.1)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2011%2024H2%20(x64)-0078d4.svg?logo=windows)](https://microsoft.com/windows)
[![Target Architecture](https://img.shields.io/badge/Target%20GPU-AMD%20Radeon%20RX%209070%20XT%20(RDNA4)-ed1c24.svg?logo=amd)](https://www.amd.com)
[![Graphics APIs](https://img.shields.io/badge/APIs-DirectX%2012%20%7C%20DirectX%2011%20%7C%20WGC-00599c.svg)](https://learn.microsoft.com/en-us/windows/win32/direct3d12/)
[![Compute Backend](https://img.shields.io/badge/Compute-AMD%20HIP%207.2-purple.svg)](https://rocm.docs.amd.com/)

**NR Auto Scale** is an open-source bridge and native proxy architecture that brings **NVIDIA DLSS Neural Rendering (DLSS-NR / DLSS 5)** compatibility runtimes to **AMD RDNA4 hardware** through **Lossless Scaling**.

By completely decoupling screen capture and neural evaluation from the target application's process, NR Auto Scale enables DLSS Neural Rendering on **any on-screen window**—including PC games, video players, browsers, and desktop emulators—without in-process game hooks, anti-cheat risks, or game-specific modding.

---

## 🌟 Key Highlights

- 🚫 **Zero In-Process Game Injection**: Captures games non-intrusively via Windows Graphics Capture (`wgc`). Anti-cheats and protected game binaries remain 100% untouched.
- 🎯 **Native 1:1 Scale Fidelity**: Operates at bit-for-bit native display resolution. No forced spatial upscaling or geometry resampling—the visible clarity comes purely from neural reconstruction.
- ⚡ **Seamless Lossless Scaling Workflow**: Initiated directly from Lossless Scaling's standard **Scale** button. The proxy forwarder handles child process orchestration, synchronization, and window swapping automatically.
- 🎛️ **Live Hotkeys & Dynamic Blending**: Toggle between original and neural output on the fly (`Ctrl+Alt+F6`) and fine-tune effect strength in 10% increments (`Ctrl+Alt+F7` / `Ctrl+Alt+F8`) via real-time software alpha blending.
- 🛡️ **Automated Health Gating & Safety**: Pre-flight warmup verification monitors runtime logs for completed neural jobs, blank frame detection guards, and graceful fallbacks.
- 🔴 **Engineered for AMD RDNA4**: Evaluated on the **AMD Radeon RX 9070 XT** utilizing the AMD HIP runtime (`amdhip64_7.dll`) and `HIP_VISIBLE_DEVICES=1`.

---

## 🔍 Visual Comparison

The crops below represent verified analytical captures from the same frozen source frame in Counter-Strike 2. Both images depict the identical pixel rectangle with **zero resizing, zero artificial sharpening, and zero post-process color manipulation**.

| Original Native Source | DLSS Neural Rendering Enabled |
| :---: | :---: |
| ![Original native-resolution crop](docs/images/cs2-native-off.png) | ![NR enabled native-resolution crop](docs/images/cs2-native-on.png) |

### Analytical Proof Metrics

Captured on 2560x1440 native SDR source (`docs/images/comparison.json`):

| Metric | Measured Value | Verification Interpretation |
| :--- | :--- | :--- |
| **Source Dimensions** | 2560 × 1440 | Bit-exact native SDR capture |
| **Analytical Crop Box** | [1080, 100] to [1720, 850] (640 × 750) | 1:1 pixel crop, no geometry scaling |
| **Input vs. Original Max Delta** | **0** | Perfect, uncorrupted source frame capture |
| **Full-Frame Mean RGB Delta ($\Delta$)** | **2.8709** | Proven neural reconstruction difference across surfaces |
| **Full-Frame Max Channel Delta** | **55** | Significant neural refinement on high-frequency edges |
| **Runtime Zero-Output Rate** | **< 0.13%** | Healthy neural inference pipeline, zero blank frames |

---

## 📐 System Architecture

NR Auto Scale utilizes a decoupled two-tier architecture that bridges Direct3D 11 host environments with Direct3D 12 neural compute pipelines:

```
┌─────────────────────────────────────────────────────────────┐
│             Target Window (Game / Video / Browser)          │
└──────────────────────────────┬──────────────────────────────┘
                               │
                               │ Windows Graphics Capture (WGC)
                               ▼
┌─────────────────────────────────────────────────────────────┐
│ DlssNrBridge.exe (Decoupled Background Bridge Process)       │
│                                                             │
│  1. Ingests source frames non-intrusively via WGC API       │
│  2. Feeds frames to private D3D12 swapchain (D3D12Presenter)│
│  3. Invokes AMD DLSS-NR compatibility runtime (version.dll) │
│  4. Runs neural evaluation on AMD HIP (HIP_VISIBLE_DEVICES=1│
│  5. Reads back buffer & verifies non-black neural luma gate │
│  6. Blends frames live based on active strength modifier    │
│  7. Presents to D3D11 bridge window ("DLSS NR Bridge")      │
│  8. Publishes HWND via atomic marker: auto-ready-<pid>.txt  │
└──────────────────────────────┬──────────────────────────────┘
                               │
                               │ Atomic HWND handshake
                               ▼
┌─────────────────────────────────────────────────────────────┐
│ Lossless.dll (Native Proxy Forwarder)                       │
│                                                             │
│  1. Drop-in proxy intercepting Lossless Scaling interop     │
│  2. Forwards unchanged functions to Lossless_original.dll   │
│  3. Intercepts Activate(targetHWND) on "Scale" button click │
│  4. Spawns DlssNrBridge.exe and awaits ready marker         │
│  5. Overrides settings to 1:1 Native Resolution             │
│     (Custom Mode, Factor 1.0, Scaling Off, Multi-Display 1) │
│  6. Forwards Activate(bridgeHWND) to Lossless Scaling       │
└──────────────────────────────┬──────────────────────────────┘
                               │
                               │ Native 1:1 Presentation
                               ▼
┌─────────────────────────────────────────────────────────────┐
│             Lossless Scaling Fullscreen Output              │
└─────────────────────────────────────────────────────────────┘
```

---

## 🎮 Runtime Hotkeys & Controls

Global hotkeys allow interactive evaluation without needing to restart applications or reconfigure files:

| Hotkey | Function | Details | HUD Indication |
| :--- | :--- | :--- | :--- |
| <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>F6</kbd> | **Toggle NR** | Instantly switches between bypass and neural processed blend | `[on X.X]` / `[off X.X]` |
| <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>F7</kbd> | **Decrease Strength** | Lowers neural effect intensity by 10% (`-0.1`, clamped at `0.0`) | Step decrement |
| <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>F8</kbd> | **Increase Strength** | Raises neural effect intensity by 10% (`+0.1`, clamped at `1.0`) | Step increment |

> **Live HUD Feedback**: The bridge window title updates in real-time to reflect the active engine state, e.g. `DLSS NR Bridge [on 1.0]` or `DLSS NR Bridge [off 0.5]`.

---

## ⚙️ Configuration Reference (`NrAutoScale.ini`)

The configuration file resides beside `Lossless.dll` in the Lossless Scaling installation folder:

```ini
[AutoScale]
Enabled=1
BridgeExe=nr-bridge\runtime\DlssNrBridge.exe
RuntimeDirectory=nr-bridge\runtime
HipVisibleDevices=1
NativeResolution=1
Width=960
Height=540
StartupDelayMs=2000
WarmupFrames=320
ReadyTimeoutMs=180000
DefaultScalingTypeIfOff=0
ForceCaptureApi=1
```

| Parameter | Type | Default | Description |
| :--- | :---: | :---: | :--- |
| `Enabled` | `int` | `1` | Master toggle for the automatic proxy bridge. |
| `BridgeExe` | `path` | `...` | Relative or absolute path to `DlssNrBridge.exe`. |
| `RuntimeDirectory` | `path` | `...` | Directory containing runtime DLLs and logs. |
| `HipVisibleDevices` | `string` | `1` | Passed to the bridge environment to select the AMD GPU device for HIP execution. |
| `NativeResolution` | `int` | `1` | When `1`, operates at native 1:1 resolution, ignoring width/height overrides. |
| `StartupDelayMs` | `int` | `2000` | Delay after loading `version.dll` before creating D3D12 swapchain. |
| `WarmupFrames` | `int` | `320` | Frames evaluated in the private D3D12 feed before health verification. |
| `ReadyTimeoutMs` | `int` | `180000`| Maximum milliseconds to wait for the bridge ready file before aborting. |
| `ForceCaptureApi` | `int` | `1` | Forces WGC capture mode (`1`) within Lossless Scaling. |

---

## 🚀 Installation & Quick Start

### Prerequisites
1. **Operating System**: Windows 11 (build 24H2 or newer recommended for WGC API).
2. **GPU & Driver**: AMD Radeon RX 9000-series GPU (tested on RX 9070 XT) with modern AMD Adrenalin drivers.
3. **Lossless Scaling**: Installed via [Steam](https://store.steampowered.com/app/993090/Lossless_Scaling/).
4. **User-Supplied Runtimes**: Users must provide their own legally acquired AMD compatibility proxy (`version.dll`), `nvngx_dlssnr.dll`, and HIP 7.2 runtime files.

### Automated Setup
1. Download the latest release package (`v0.1.0-pre.1`) from the [Releases](https://github.com/eikkapine/NR-Auto-Scale/releases) page.
2. Extract the archive outside of the Lossless Scaling folder.
3. Run:
   ```cmd
   Setup.cmd
   ```
4. Select your Lossless Scaling install path when prompted. The installer backs up your original `Lossless.dll` to `Lossless_original.dll`.
5. Supply your private AMD proxy and NVIDIA runtime files as instructed.
6. Launch Lossless Scaling and click **Scale** on any active application.

For advanced or developer installation workflows, see [docs/install.md](docs/install.md).

---

## 🛠️ Building From Source

### Requirements
- Visual Studio 2022 (MSVC C++ toolset)
- CMake 3.20+
- .NET 8.0 SDK (for the optional controls helper)
- Windows 11 SDK (10.0.26100.0 or newer for WGC interop headers)

### Compilation Commands

```powershell
# 1. Build the .NET control helper
dotnet build .\controls\DlssNrControl\DlssNrControl.csproj -c Release

# 2. Build the native Lossless.dll proxy forwarder
.\auto-scale\build.ps1

# 3. Build the DlssNrBridge executable
.\bridge\build.ps1
```

### Running Test Harness & Probes

```powershell
# Run the automated native proxy integration suite (9 tests)
.\auto-scale\tests\Run-AutoScaleTests.ps1

# Execute standalone D3D12 hardware probe
.\probe\run_probe.ps1 -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1
```

---

## 📊 Performance & Latency Profile

Benchmarks recorded on an **AMD Radeon RX 9070 XT** (16GB VRAM, RDNA4, `gfx1201`):

| Resolution | Neural Job Time | Neural Frame Rate | Use Case Profile |
| :--- | :---: | :---: | :--- |
| **960 × 540** (Diagnostic) | ~15–16 ms | ~60–65 FPS | Fast diagnostic proof & menu testing |
| **2560 × 1440** (Native 1440p) | ~63 ms | ~16 FPS | High-fidelity desktop, video, & slow-paced rendering |

> [!NOTE]
> **Latency & Display Pipeline**: The current bridge transfers rendered frames from the D3D12 neural swapchain to D3D11 via CPU staging readback. While optimal for visual fidelity on video playback, desktop applications, and slow-paced games, it introduces multi-frame asynchronous latency. Direct GPU-to-GPU shared texture handles (`D3D11_RESOURCE_MISC_SHARED_NTHANDLE`) are planned for future zero-copy low-latency revisions.

---

## 📜 Repository Guidelines & Legal Compliance

To respect software licensing and distribution agreements:
- **Clean Room Open Source**: This repository contains original source code, scripts, and documentation under the MIT License.
- **No Proprietary Binaries**: This repository **does not** bundle or distribute paid Lossless Scaling binaries, NVIDIA proprietary SDKs/DLLs, model neural weights, or AMD redistributable packages.
- **Privacy First**: Test assets and verification suites use synthetic deterministic images or approved, sanitized analytical crops. No user wallpapers, personal browser data, or copyrighted films are stored.

See [docs/licensing.md](docs/licensing.md) and [docs/release-checklist.md](docs/release-checklist.md) for full compliance details.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
