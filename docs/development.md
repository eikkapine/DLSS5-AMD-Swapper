# Development

DLSS5 AMD Swapper is split into three main pieces:

- `app/` — .NET 8 WPF manager
- `direct-game/` — advanced Python direct-game helper and diagnostics
- `auto-scale/` + `bridge/` — Lossless Scaling proxy and neural bridge

## Build everything

The release-candidate loop is:

```powershell
.\app\Build-Package.ps1
```

That script builds the native Lossless Scaling wrapper, builds the bridge, runs the app smoke tests, publishes a self-contained `win-x64` WPF executable, copies only project-owned payload files, rejects forbidden third-party/private filenames, writes `SHA256SUMS.txt` and creates the local ZIP.

## App smoke tests

The smoke tests cover the parts most likely to make the manager unsafe or misleading:

- INI edits preserve unrelated sections
- runtime controls save atomically
- runtime diagnostics require real rich-path log evidence
- x64 PE probing works
- anti-cheat evidence overrides otherwise compatible targets
- SHA-256 helpers are deterministic
- an installed Lossless Scaling bridge can be recognized when a real install path is supplied

Run them directly with:

```powershell
dotnet run --project .\app\Dlss5AmdSwapper.SmokeTests\Dlss5AmdSwapper.SmokeTests.csproj -c Release
```

## Direct-game invariants

I keep the direct installer reversible and conservative:

- require x64 plus FSR/DX12 evidence unless an advanced force flag is used
- never allow force to bypass anti-cheat blocking
- verify the official upstream setup before install/update
- validate the generated rich-input configuration
- snapshot managed files before changing them
- restore the pre-install managed state on failure/cancel
- remove only files whose hashes still prove they are managed
- recognize the legacy manifest while writing the new `.dlss5-amd-swapper.json` format

## Lossless Scaling invariants

- keep the source/native frame as the visible base in the default mode
- cap only the hidden neural workload with `NeuralMaxHeight`
- keep the async runtime path independent from visible presentation cadence
- do not reuse stale full frames as temporal history
- reject unstable residual colour/luminance that causes trails/flicker
- preserve the existing managed bridge settings during an update
- never package the paid Lossless Scaling original DLL

## Performance work

The project already uses shared GPU transport, direct WGC SRV access where available, low-resolution neural work over native visible output and log-backed timing separation.

For direct-game performance, the important dimensions are the game's actual FSR input/render resolution versus its final swapchain/output resolution. Diagnostics must prove those dimensions before I describe the lower-resolution neural path as active.

## Publication discipline

Concrete frame-rate claims must come from hashed measurement logs. I run:

```powershell
py .\tools\Check-Publication.py
```

before a public update, then inspect the Git diff/status and final ZIP for third-party binaries, private config/logs, machine paths, personal files and unreviewed screenshots.
