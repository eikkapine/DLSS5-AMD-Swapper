# Bridge performance and verification — 7 September 2026

The optimized bridge is installed in `../nr-bridge/runtime/DlssNrBridge.exe` relative to the repository root. The first optimization pass preserved the installed 640×360 preset. A subsequent requested resolution update changes the saved preset and source defaults to **1280×720**. The neural runtime, weights, neural settings and Lossless Scaling profile remain unchanged.

## 720p default and online evidence

The source defaults are now `NativeResolution=0`, `Width=1280`, `Height=720` across the proxy, setup, direct installer and standalone launcher. Sources with another aspect ratio are fitted within those bounds. Fixed-size mode preserves the selected scaler; if it is Off, the fallback is LS1. Native source-size processing remains an explicit option.

Primary sources checked on 7 September 2026:

- [Upstream AMD runtime README](https://github.com/danielblnc/DLSS-NR-on-AMD): the author reports roughly 33 FPS at 1080p on an RX 9070 XT and describes performance as work in progress. It does not prescribe a universal resolution.
- [Community test, issue #92](https://github.com/danielblnc/DLSS-NR-on-AMD/issues/92), posted 6 September: an RX 9070 XT user reports about 52 FPS at 1366×768, 29 at 1080p, 17–20 at 1440p, and 8 at 4K in Silent Hill f. Those are that user's game results, not measurements of this bridge.
- [Community report, issue #70](https://github.com/danielblnc/DLSS-NR-on-AMD/issues/70): another user reports about 20 FPS at 720p in Cyberpunk and asks for an independent neural-resolution control. That illustrates variation between setups rather than a guaranteed 720p frame rate.

**1280×720 is this project's measured compromise, not a verified community consensus or an official NVIDIA/AMD recommendation.** It increases the former installed 360p pixel count fourfold while keeping substantially more neural throughput than the separately tested native 1440p path. These references concern neural rendering; conventional DLSS Super Resolution preset recommendations were not substituted for them.

Sequential tests on the same moving 1280×720 synthetic source, using the same installed bridge/runtime/settings:

| Processing bounds | Observed neural evaluations/s | Bridge presents/s | Launch to ready |
| --- | ---: | ---: | ---: |
| 640×360 | 106.96 | 343.63 | 2.590 s |
| 1280×720 | 52.98 | 269.19 | 2.634 s |

Both bounded runs exited successfully with healthy runtime logs, completed neural jobs and the effect enabled at full strength. Each ran for 24 requested seconds. The public numbers are rounded estimates from logged job milestones, not unique source frames, game FPS, or input latency. Higher resolution costs about half the neural throughput here; the 269 presents/s figure must not be described as 269 newly inferred frames/s.

A separate frozen-source check used the dimensions read from the saved installed INI. All four final captures were 1280×720; input and prepared original matched exactly, and the full-strength display matched the neural buffer exactly. Neural output was nonblack and differed from the static original by mean 2.788/255 and maximum 58/255 per RGB channel. The saved render was inspected. This verifies that the effect remains active and the host presents its output correctly; it is not a claim of identical neural results across separate runs. Private evidence is in `static-720p/pixel-verification.json` beneath the resolution run directory.

Private results are under `bridge/runs/resolution-20260907/{live-360p,live-720p}/`. The saved installed INI was backed up before its two resolution values were changed; unsupported preset latency/FPS promises were removed. Hash checks confirmed nine guarded files, including both neural INIs, the model/weights, runtime, installed wrapper and app profile, were unchanged. The existing wrapper already supports explicit 720p settings and was retained. The running app was left undisturbed after desktop inspection was blocked; **restart Lossless Scaling to load the saved 720p preset**. This update does not claim a new real-app 720p activation test.

Release builds and pixel tests passed again. All **12 proxy cases** passed, including missing-INI defaults, missing-key defaults and preservation of a selected scaler; the explicit native-mode cases remain covered. Four isolated installer checks passed: direct default, setup default, custom 1600×900 override and native-mode override. Installer tests use only project-built stub PE files, never a real app or vendor runtime.

## Earlier performance optimization at unchanged 360p

Controlled measurements on the local RX 9070 XT used the existing async neural runtime and identical configuration. The live fixture was a moving marker over a deterministic 1280×720 image, processed with the existing 640×360 preset. These are **bridge and neural-evaluation measurements, not game FPS or measured input latency**. A neural evaluation can reuse a source image, and the visible presenter can show an earlier completed neural result.

| Live-source measurement, 640×360 output | Original bridge | Installed bridge |
| --- | ---: | ---: |
| Completed neural evaluations per second, observed between logged job milestones | 27.82 | 110.19 |
| Bridge presents per second | 27.47 | 355.05 |
| Launch to ready notification | 6.581 s | 2.465 s |

The improvement in neural evaluation throughput was approximately 3.96×. The much larger presentation increase must not be represented as a corresponding increase in unique neural frames or game FPS. The synthetic source itself updated at roughly display/timer cadence, rather than at every bridge present.

At **native 2560×1440**, the installed build measured **14.20 completed neural evaluations/s** and **155.68 bridge presents/s**. The original bridge presented at 20.71/s, but its sparsely logged native neural rate does not support a precise inference-speed comparison. Full native neural rendering remains the principal performance limitation; the host changes do not make this a 1440p, 155-FPS neural renderer. Concurrent game GPU load can reduce these numbers.

Raw results are private local artifacts in `bridge/runs/performance-20260907/`: `baseline-live-640/measurement.json`, `final-live-640/measurement.json`, `baseline-native/measurement.json`, and `final-native/measurement.json`. Each measurement records executable, runtime and configuration hashes. Stage timings are in the adjacent `bridge-report.txt` files.

## What changed

The original bridge added a fixed 33 ms sleep **after** processing every frame. That limited even a fast inference path to below 30 bridge FPS. The new default lets the visible swapchain pace presentation, and the hidden neural feed no longer requests its own vertical-sync wait. Optional `--max-fps N` uses a frame-start-to-frame-start budget; zero is the default and does not add an explicit frame cap.

Capture now prefers the newest available frame in the two-slot capture pool. Prepared input is reused when there is no new source frame, neural readback storage is reused, full-strength and bypass display avoid an extra full-frame copy, and identical-size input avoids the scaling loop. The existing black-frame threshold has an exact integer early-exit implementation. Colour conversion, intermediate-strength blend arithmetic, model inputs and the visible presenter's opaque-alpha handling are preserved.

Startup health verification uses a wall-clock deadline so faster warmup cannot exhaust a fixed attempt count before inference finishes. GPU cleanup no longer throws from the presenter destructor while another failure is being unwound. Reports distinguish presentation rate from inference and include timing for each host phase.

## Preserved quality settings

The installed preset was already `NativeResolution=0`, `Width=640`, `Height=360` before the first optimization pass. It was **not reduced by that pass**. The later 720p change is documented above. Native mode was tested separately in isolated runs.

The existing asynchronous neural mode, input selection, effect intensity, local structure, local tone, skin structure and interop settings were preserved. The private runtime configuration is not distributed with the report.

The neural runtime and weights were not replaced or edited. The active app was returned to full neural strength after the earlier app test. `NrAutoScale.ini` and the application's `Settings.xml` matched their backups at that point; only the saved processing size and explanatory comments changed in the subsequent resolution update. No global GPU, Special K, subscription or model-selection settings were changed.

## Validation and its limits

Release compilation and `BridgePixelTests` passed. The tests execute the production AVX2 conversion and blend helpers against scalar references across vector-tail sizes and all 257 fixed-point blend weights. They also cover opaque alpha, buffer boundaries, bypass, native-size identity and the exact black-frame threshold.

The optional 30-FPS limiter was exercised in an isolated runtime and completed normally below its ceiling, with healthy neural jobs and zero-copy runtime interop. The installed configuration retains the uncapped default.

Frozen-source before/after captures had **byte-identical input and prepared-original RGB data** at both 640×360 and native 2560×1440. Neural output was nonblack and differed from the original image, showing that the effect remained active. The visible bridge was also inspected in the actual Lossless Scaling session.

**Neural output was not bit-identical across independent runs.** At 640×360, original-vs-optimized output differed by mean 1.047/255 and maximum 14/255 per channel. Two runs of the unchanged original build differed by mean 1.093/255 and maximum 17/255. This establishes pre-existing run variability and supports unchanged host image processing; it is not proof that all scenes are perceptually identical. The strict comparison utility correctly records `pixels_identical=false` and `passed=false` for these neural comparisons. Do not reinterpret those files as passing exact-output tests.

All nine auto-scale lifecycle cases passed: normal activation, cancellation, diagnostic forwarding, fixed-size compatibility, disabled pass-through, source closure, child exit, missing child executable, and invalid readiness. The first run's child-exit case was contaminated by global Special K injection into the fake renderer: the child logged that it was exiting but remained alive. Test staging now uses the same local Special K exclusion markers as the deployed bridge. The assertions and installed wrapper were not weakened or changed.

The installed application was then exercised through its configured **Ctrl+Alt+S** shortcut. It started the bridge, received readiness, and activated scaling automatically. Visible neural rendering, Ctrl+Alt+F6 bypass/restoration, Ctrl+Alt+F7 strength 0.9, Ctrl+Alt+F8 restoration to 1.0, and Unscale shutdown were verified. More than 15,000 neural jobs completed during this app session; runtime logs contained no GPU/kernel failure. Evidence is in `installed-app-runtime.log` and `installed-app-activation.log` in the run folder. This was a synthetic graphics source, not an in-game latency benchmark.

## Reproducing the checks

From the repository root:

```powershell
.\bridge\build.ps1 -Configuration Release
ctest --test-dir bridge\build -C Release --output-on-failure
.\auto-scale\tests\Run-AutoScaleTests.ps1 -Wrapper '..\Lossless.dll'
```

`bridge/tests/Start-FrameFixture.ps1` creates a bounded synthetic source and writes its HWND to a ready JSON file. Add `-Animate` for live capture. `bridge/tests/Measure-Bridge.ps1` stages a private runtime copy into a fresh output directory and benchmarks that source. Use `-LiveSource` to exercise capture continuously or omit it for frozen-source comparisons. `-NativeResolution` preserves source dimensions. `-ExtraArguments @('--max-fps','30')` exercises the optional limiter without changing the installed configuration.

`bridge/tests/Compare-BridgeFrames.py` compares frozen input, prepared original, neural result and visible-display captures without resizing. It requires the existing Python, NumPy and Pillow installation and intentionally returns failure for nonidentical neural output.

The visible D3D12 probe now uses a normal application window so the synthetic source can be selected by desktop automation. Its hidden mode retains the original tool-window behavior. Build scripts stop immediately on configure/build failures rather than reporting an older executable as a successful build.

## Deployment and rollback

Installed executable SHA-256:

```text
870B3AF9C2E66C33CDFDB4F74687913696252F531366F4ABCF4D22E7A097E531
```

Unchanged neural runtime SHA-256:

```text
16B30141FB63F0CEE385E4683C50BEC4B8482057B28DE6FD30E3B5311664D8F2
```

The pre-optimization executable and configuration backups are in `../backups/performance-20260907/` relative to the repository root, with a hash manifest. To roll back the bridge optimization, stop scaling and restore its `DlssNrBridge.exe` into `../nr-bridge/runtime/`. The resolution follow-up has a separate timestamped `../backups/resolution-*/` INI backup and manifest; restore that INI and restart the app to undo only the preset change. The installed `Lossless.dll` was not replaced. This source update includes the original optimization code, default changes, tests and report. No new binary release, vendor files, weights, private configs, logs or new captures are included.
