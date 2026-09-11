# OptiScaler pre-SR route, Lossless Scaling layer controls, and v0.3.0-pre.1 — design

Date: 2026-09-11. Status: approved by the project owner before implementation.

## Why

The current direct-game route runs Neural Rendering **after** FSR, at the game's FSR input resolution. When a game runs FSR at native size the model processes the full display resolution; the Crimson Desert log on the reference machine (RX 9070 XT) shows `colour 2560x1440` and about **45 ms GPU per network job**. The same model at 1280x720 costs roughly a quarter of that.

A private OptiScaler build (`OptiScaler v10.0.0-dev (amd-presr-multipass-local)`) adds an AMD HIP backend that evaluates Neural Rendering **before** super-resolution on the render-resolution colour buffer, with up to three independent runtimes (multipass). Published tests on RX 9070 XT / RX 9060 XT reach 60+ FPS at 4K with a 3.0x FSR ratio (1280x720 render) and 3x frame generation. This project adds that route to the manager without redistributing any third-party binary.

## Third-party facts that shape the design

| File in the reference package | What it is | Repository rule |
| --- | --- | --- |
| `OptiScaler.dll` / `dxgi.dll` (~25.9 MB) | OptiScaler fork build `amd-presr-multipass-local`, GPL-3.0 project, fork source not published | Never bundled. User supplies. |
| `dlssnr_amd_pass1/2/3.dll` (7,156,224 bytes each) | DLSS-NR-on-AMD `version.dll` proxy v0.2.14, renamed, with patched embedded HLSL | Never bundled. User supplies. Upstream licence forbids redistribution/modification. |
| `dlssnr_on_amd_weights.bin` (147,689,451 bytes) | Weights generated from NVIDIA `nvngx_dlssnr.dll` 310.8.0.0; hash `6bf8dc93…` identical to copies generated locally by the official runtime | Never bundled or downloaded. Reused from local generated copies only. |
| `OptiScaler\*.dll`, `D3D12_OptiScaler\D3D12Core.dll` | FidelityFX, XeSS, Agility SDK redistributables | Never bundled. Copied from the user's package. |
| `dlss-enabler-headless.dll` | Optional FG helper for the `combo` Nvngx replacement | Optional, user-supplied. |
| `OptiScaler.ini` | OptiScaler's annotated default INI | Copied from package when present; otherwise the manager writes a minimal INI in its own words. |

The reference package also ships `INSTALAR_AMD.ps1`, `DIAGNOSTICO_AMD.ps1` and `SHA256SUMS.txt`. The manager reads `SHA256SUMS.txt` for verification but never executes the package scripts.

## Scope

Four sub-projects, one implementation plan, built in this order:

1. OptiScaler pre-SR direct-game route: manager services, UI, CLI mirror.
2. Lossless Scaling route: structure / skin / tone sliders and hotkeys.
3. Documentation, licensing, publication gate, release metadata.
4. Live verification on the owner's PC (Crimson Desert), then publish `v0.3.0-pre.1`.

Out of scope: bridge/compositor changes, deriving pass DLLs from the official v0.2.17 runtime, OptiScaler inside Lossless Scaling (the fork needs a DirectX 12 super-resolution call to hook; Lossless Scaling is D3D11 with its own shaders).

## 1. Route architecture

### Models

`Models/OptiScalerPackage.cs` — immutable record produced by validation:

- `Root` (folder), `OptiScalerDllPath`, `PassDllPaths` (1–3 entries; index 0 required), `IniPath?`, `DependencyFolder?` (`OptiScaler\`), `EnablerDllPath?`, `WeightsPath?` (only when the file is a real weights file: size > 1 MB and does not start with `version https://git-lfs`), `Sha256SumsPath?`
- `ForkVersion` (ProductVersion string from VersionInfo), `Files` (relative path → `FileState(size, sha256)`), `Sha256SumsVerified` (bool), `Layout` (`Package` or `Vodkaman`).

`Models/GameEntry.cs` gains:

- `Route` enum property `InstallRoute { None, PostFsrRuntime, OptiScalerPreSr }`, read from the manifest `route` field (`amd-fsr-direct` → PostFsrRuntime, `amd-optiscaler-presr` → OptiScalerPreSr).
- `Passes` (1–3) for the pre-SR route.
- `OptiScalerIniPath` = `<game dir>\OptiScaler.ini`, `PreSrLogPath` = `<game dir>\amd_presr.log`, `OptiScalerLogPath` = `<game dir>\OptiScaler.log`.
- `RouteLabel` for the UI badge.

`Models/AppSettings.cs` gains `OptiScalerPackagePath`, `LocalWeightsPath`, `OptiScalerDefaultPreset` (`Quality` default).

### Services

`Services/OptiScalerPackageService.cs`

- `Discover(configuredPath)` returns candidates in this order: configured path; `%USERPROFILE%\Downloads` searched two levels deep for folders named `OptiScaler-AMD-PreSR-Multipass*` and zips with that stem; any folder that holds both `dxgi.dll` and `dlssnr_amd_pass1.dll` (Vodkaman layout) under Downloads, Desktop or Documents, two levels deep.
- Zips extract to `%LocalAppData%\DLSS5 AMD Swapper\optiscaler-packages\<first 8 hex of zip sha256>\` once; the extracted folder is then validated like a folder. Zip entries are rejected when they escape the target folder.
- `Validate(root)` produces `OptiScalerPackage` or throws with a single actionable sentence. Rules:
  - OptiScaler binary is `OptiScaler.dll` or `dxgi.dll` in `root`; PE machine 0x8664; VersionInfo `ProductName == "OptiScaler"`; `ProductVersion` contains `amd-presr` (case-insensitive). Otherwise: "This OptiScaler build is not the AMD pre-SR fork; the pre-SR route needs a build whose version contains amd-presr."
  - `dlssnr_amd_pass1.dll` required, PE x64, file contains the ASCII marker `dlssnr_amd`. Pass 2/3 optional; when present they must be PE x64 with the same marker (hash equality is recorded, not required).
  - When `SHA256SUMS.txt` exists every listed file that exists is hashed. A mismatch on a file the manager installs (fork binary, pass DLLs, `OptiScaler.ini`, `OptiScaler\` dependencies, enabler, weights) is a hard failure naming the file; a Git LFS pointer satisfies its entry when its `oid sha256:` equals the listed hash. A mismatch on any other listed file (readme, scripts, licence texts) is recorded as a warning. Listed files that are absent are skipped, except the OptiScaler binary and pass 1 which stay required. (Amended 2026-09-12 after validating the genuine reference package: it ships a pointer for the weights and stale checksums for its readme and licence texts.)
  - Dependency folder: `root\OptiScaler\`. Optional in the package. Install fails later only when neither the package nor the game folder supplies `amd_fidelityfx_upscaler_dx12.dll` (needed for `Dx12Upscaler=ffx`).
- `FindLocalWeights(settings, games, losslessInstallPath)`: candidate order — configured `LocalWeightsPath`; `<Lossless Scaling>\nr-bridge\runtime\dlssnr_on_amd_weights.bin`; `<Lossless Scaling>\dlssnr_on_amd_weights.bin`; `dlssnr_on_amd_weights.bin` in every managed game folder. A candidate qualifies when size > 1 MB. When more than one qualifies they must share one hash; a mismatch is an error naming both paths. Result carries path, size and hash.

`Services/OptiScalerInstallerService.cs`

- `InstallAsync(game, package, weights, preset, proxyName = "dxgi.dll", update, cancellationToken)` and `RemoveAsync(game, cancellationToken)`.
- Reuses `GameProbeService` compatibility checks and the same refusals as the post-FSR route: not x64, anti-cheat markers, no FSR marker, no DX12 evidence, game running, an operation already active for the folder.
- Managed names: `ProxyNames` (`dxgi.dll`, `version.dll`, `winmm.dll`, `dbghelp.dll`, `wininet.dll`, `winhttp.dll`), `OptiScaler.ini`, `OptiScaler.log`, `amd_presr.log`, `dlssnr_amd_pass1.dll`, `dlssnr_amd_pass2.dll`, `dlssnr_amd_pass3.dll`, `dlssnr_on_amd_weights.bin`, `OptiScaler\**` (every file the package's dependency folder provides, tracked by relative path).
- Existing state handling:
  - Post-FSR manifest present → run `DirectGameInstallerService.RemoveAsync(game, removeModel: false)` first. Its result is recorded in the new manifest under `previous_route`. Files it preserved (hash changed) are left alone; the install continues only if no unmanaged proxy remains.
  - Pre-SR manifest present and `update == false` → error "already installed; use Update".
  - Any proxy name present without a manifest → refuse, same policy as today.
- Install steps, all under the folder lock:
  1. Snapshot every managed name (size + sha256).
  2. Copy fork binary to `<game>\<proxyName>`.
  3. Copy pass 1. Pass 2 and 3 come from the package when present, otherwise duplicated from pass 1. All three are always installed so `Passes=2/3` works without reinstalling.
  4. Copy weights from the local source.
  5. Copy dependency folder files. A file is skipped when the game already has the same relative path with an identical hash; such files are recorded as `preexisting` and never removed on restore.
  6. When the preset needs the enabler and the package has it, copy `dlss-enabler-headless.dll` into `<game>\OptiScaler\`.
  7. Write `OptiScaler.ini` (section 4).
  8. Re-hash every written file against its source; mismatch aborts.
  9. Write manifest schema 3.
  10. Any exception → restore the snapshot exactly (copy back files that existed, delete files that did not), delete a partially written manifest, rethrow.
- Remove: delete a managed file only when it was absent in `before` and its current hash equals `after`; otherwise preserve and report. If `previous_route` recorded a moved post-FSR proxy backup, it is not automatically reinstated (the user reinstalls the post-FSR route through the normal flow). The manifest is deleted only when nothing created remains.
- `SetPassesAsync`, `SetStructureAsync`, `SetSkinAsync`, `SetToneAsync` write the `[DlssNr]` keys through `IniDocument.SaveAtomic` and return "Saved for the next launch" until diagnostics prove a live reload.

`Services/OptiScalerDiagnosticsService.cs` — section 5.

`Services/RuntimeControlService.cs` — generalised so the same code drives the LS bridge runtime INI (section 6).

### UI

- Library row: route badge (`Post-FSR`, `Pre-SR`, none).
- **Set up** opens a small dialog: route radio (Post-FSR runtime / OptiScaler pre-SR), preset radio (Quality / Performance, default from settings), package summary line (fork version, layout, SHA256SUMS state), weights source line. Confirm → install.
- Game detail panel for a pre-SR game: Passes combo (1–3), existing Structure/Skin/Tone sliders, diagnostics summary (section 5), buttons Update / Restore / Open OptiScaler.ini / Open logs.
- Settings page: OptiScaler package path + Browse + "Find in Downloads"; local weights path (auto-filled, editable); default preset.
- Home page: Direct Game card mentions both backends.

### CLI mirror

`direct-game/amd_dlss5.py` gains `--route {post-fsr,optiscaler-presr}` (default `post-fsr`), `--package <dir>`, `--weights <file>`, `--preset {quality,performance}`, `--proxy-name`, `--passes N`. `--check` reports both routes' eligibility. `--diagnose` reads whichever manifest route is present. Manifest JSON is byte-compatible with the app (same field names, schema 3).

## 2. Presets and OptiScaler.ini

Base file: package `OptiScaler.ini` when present (values replaced in place, comments preserved); otherwise a minimal INI written by the manager containing only the sections below.

Quality (default):

```ini
[Upscalers]
Dx12Upscaler=ffx

[DlssNr]
Enabled=true
RunBeforeSR=true
Passes=1
LocalTone=0
LocalStructure=1
SkinStructure=1
ApplyAfterRR=false

[Log]
LogToFile=true
LogLevel=2
```

Performance adds:

```ini
[UpscaleRatio]
UpscaleRatioOverrideEnabled=true
UpscaleRatioOverrideValue=3.0

[FrameGen]
Enabled=true
FGInput=nvngxfg
FGNvngxReplacement=combo   ; ffx when dlss-enabler-headless.dll is absent

[DLSSG]
InterpolationCount=2
```

Notes recorded in the docs: frame generation through `nvngxfg` needs the game's own DLSS Frame Generation path; games without it must use the OptiScaler menu (`Insert`) to choose another FG input. The manager never claims FG is active; the OptiScaler log is the evidence.

## 3. Manifest schema 3

```json
{
  "schema_version": 3,
  "created_unix": 0,
  "route": "amd-optiscaler-presr",
  "game_exe": "Game.exe",
  "proxy_name": "dxgi.dll",
  "preset": "quality",
  "package": {
    "root": "<absolute path, redacted in diagnostics export>",
    "layout": "package|vodkaman",
    "fork_version": "10.0.0-dev (amd-presr-multipass-local) (20260907_075847)",
    "sha256sums_verified": true,
    "files": { "OptiScaler.dll": {"size": 0, "sha256": ""}, "dlssnr_amd_pass1.dll": {"size": 0, "sha256": ""} }
  },
  "weights": { "source": "<path>", "size": 0, "sha256": "" },
  "compatibility": { "x64": true, "fsr_markers": [], "dx12_evidence": [], "anti_cheat_markers": [] },
  "previous_route": { "route": "amd-fsr-direct", "removed": [], "preserved": [] },
  "before": {}, "after": {},
  "preexisting_dependencies": ["OptiScaler\\amd_fidelityfx_upscaler_dx12.dll"],
  "installed_proxy_names": ["dxgi.dll"]
}
```

The post-FSR route keeps schema 2 unchanged. Readers accept 1–3.

## 4. Diagnostics

`OptiScalerDiagnosticsService.Summarize(game)` returns:

- `pre_sr_active` — true when `amd_presr.log` contains `Completed AMD pre-SR passes=` or `OptiScaler.log` contains `DLSS-NR running`.
- `hip_adapter` — text after `HIP adapter:` in `amd_presr.log`.
- `passes_initialized` — count of `Initialized independent AMD pass`.
- `passes_completed` — last integer after `Completed AMD pre-SR passes=`.
- `model_size`, `target_size` — from `DlssNr_Dx12::Dispatch DLSS-NR running ...: target WxH, model WxH` in `OptiScaler.log`.
- `cost_ms` — mean/min/max of `DLSS-NR cost: X ms total = Y ms model` (total and model separately).
- `last_fault` — the newest line matching `AMD pre-SR: (?!idle)`, `HIP completion timeout`, `Unsupported AMD pre-SR`, `hash mismatch`, `weights.bin is required`, `LoadLibrary failed`, `initialization failed`, `Cannot load amdhip64_7.dll`.
- `log_sha256` and byte counts for both logs (raw logs stay private).

The game panel shows one line: `Pre-SR active · 1280x720 model · 11.2 ms · passes 1` or the last fault. The diagnostics export includes the summary with paths redacted.

## 5. Lossless Scaling layer controls

- `RuntimeControlService` takes the INI path per call (`game.ConfigPath` for direct games, `<LS>\nr-bridge\runtime\dlssnr_on_amd.ini` for the bridge). The `[DlssNrOnAmd]` section and 0–2 clamps stay.
- LS page: three sliders (Structure, Skin, Tone) plus "Skin follows structure" checkbox (writes `SkinStructure=-1`). Values read on page open and after each write. Text under the sliders: "Applies while the bridge runs; the runtime reloads this file."
- Hotkeys registered only while `LosslessScaling.exe` is running and the bridge install is present: `Ctrl+Alt+F9` cycles the target layer (structure → skin → tone), `Ctrl+Alt+F10` −0.1, `Ctrl+Alt+F11` +0.1. `HotkeyService` grows three ids; registration failure is reported once, not thrown. `Ctrl+Alt+F6/F7/F8` remain owned by the bridge.
- No change to `NrAutoScale.ini`, the proxy, or the bridge binary.

## 6. Documentation, licensing, publication gate, release

- New `docs/optiscaler-presr.md`: mechanism (why render-resolution NR is cheaper), reference setup (720p → 4K, FG 3x) with the explicit note that FG contributed to the published 60+ FPS, package contents and provenance, what the manager verifies, install/restore behaviour, INI presets, diagnostics fields, known limits (Vulkan, D3D11, anti-cheat, games without a DX12 SR call).
- `docs/licensing.md`: rows for cdozdil/OptiScaler (GPL-3.0), Dagherbou/OptiScaler_DLSSNR (GPL-3.0 fork), Vodkaman23/DLSS-NR-UE5-Opti-DLL (no licence; binaries never redistributed), gamegpu.com article (credit). Restate that `dlssnr_amd_pass*.dll` are DLSS-NR-on-AMD builds covered by that project's licence.
- `README.md`: Direct Game has two backends; credits; no new performance numbers until a hashed capture exists.
- `docs/install.md`, `direct-game/README.md`: pre-SR steps and CLI.
- `tools/Check-Publication.py`: forbidden names gain `dxgi.dll`, `OptiScaler*.dll`, `dlssnr_amd_pass*`, `libxess*`, `libxell*`, `amd_fidelityfx_*`, `D3D12Core.dll`, `dlss-enabler*`, `SHA256SUMS.txt`, `INSTALAR_AMD.ps1`, `DIAGNOSTICO_AMD.ps1`, `amd_presr.log`, `OptiScaler.log`.
- `.gitignore`: add `**/optiscaler-packages/`, `amd_presr.log`, `OptiScaler.log`, `backup-amd-presr-*/`.
- `VERSION` → `0.3.0-pre.1`; `RELEASE.json` and `docs/releases/v0.3.0-pre.1.md` filled after verification; `app/Build-Package.ps1` unchanged unless new payload files are needed (none expected).

## 7. Error handling summary

| Condition | Behaviour |
| --- | --- |
| Package missing / not the fork / SHA mismatch | Set up disabled with the reason; Settings shows the same sentence. |
| No local weights | Install refused: "No generated dlssnr_on_amd_weights.bin was found. Run the Post-FSR route once so the official runtime generates it, or point Settings at an existing copy." |
| Missing `amd_fidelityfx_upscaler_dx12.dll` in both package and game | Install refused naming the file. |
| Game running | Refused. |
| Unmanaged proxy present | Refused (unchanged policy). |
| Copy/hash failure mid-install | Exact snapshot restore, manifest removed, error surfaced. |
| Hotkey registration conflict | One warning in the LS page; sliders keep working. |

## 8. Testing

- `app/Dlss5AmdSwapper.SmokeTests`: package validation fixtures (valid layout, Vodkaman layout, LFS-pointer weights, wrong fork version, SHA mismatch), INI generation for both presets against a package INI and against no INI, manifest round-trip and restore on a temp folder, diagnostics parsing on captured log fixtures (sanitized excerpts), `RuntimeControlService` against a temp INI path.
- `direct-game/tests/test_amd_dlss5.py` (pytest, stdlib-only helper): same coverage for the CLI.
- Live on the owner's PC: Crimson Desert. Baseline is the existing log (`colour 2560x1440`, ~45 ms). After install: run, then `tools/Capture-Performance.ps1 -ProcessName CrimsonDesert.exe -Seconds 30`, analyzer JSON with hashes, diagnostics summary. Visual judgement is the owner's. Publication happens only after that.

## Decisions log

- Package files stay user-supplied; nothing from the reference package enters the repository or the release ZIP.
- Default preset is Quality; Performance (3.0x ratio + FG 3x) is one click away and documented as latency-adding.
- Pass DLLs are taken from the package as-is; deriving from the official runtime is deferred.
- Lossless Scaling gets sliders and hotkeys only; the compositor is untouched.
