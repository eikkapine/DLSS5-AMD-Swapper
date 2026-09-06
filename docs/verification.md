# Verification

Use this page to decide whether the project can honestly claim that DLSS neural rendering works on AMD through the Lossless Scaling path.

## Current Evidence

The project has partial positive evidence, but public-release verification is not complete.

Verified:

- The controls helper builds and passes its self-test.
- Startup INI commands update only the intended values.
- The listener smoke test confirms hotkey registration and message handling against a temporary INI.
- The standalone D3D12 probe has produced a real nonblack on/off image difference after engine initialization and completed neural jobs.
- The bridge developer path has produced matched off/on/blended output from a 960x540 source.
- The live bridge hotkeys `Ctrl+Alt+F6`, `Ctrl+Alt+F7`, and `Ctrl+Alt+F8` work in the tested bridge path.
- The native auto-scale wrapper fake integration harness passes all 9 current cases.

Pending:

- The actual Lossless Scaling app must start the path automatically when the user presses Scale.
- The deterministic full Lossless Scaling comparison must pass on a static desktop image at native WGC resolution.
- The native path must avoid geometry resizing.
- The full native-resolution Lossless Scaling app path still needs verification. The latest stopped test did not produce the expected proxy activation log before testing was halted.
- Public release contents must pass the source, license, and asset review.

## Controls Helper Evidence

Run from the repository root:

```powershell
dotnet build .\controls\DlssNrControl\DlssNrControl.csproj -c Release
.\controls\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe --self-test
```

Expected result:

```text
Build succeeded.
Self-tests passed.
```

Run one-shot commands against a temporary INI before using a real config:

```powershell
$config = Join-Path $env:TEMP "dlssnr-doc-test.ini"
Set-Content -LiteralPath $config -Encoding ASCII -Value "[DlssNrOnAmd]`r`nEnabled=0`r`nLocalStructure=1.0`r`nUseFsrInputs=0`r`n"
.\controls\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe --config $config --toggle
.\controls\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe --config $config --increase
Get-Content -Raw -LiteralPath $config
Remove-Item -LiteralPath $config
```

Expected INI state:

```ini
[DlssNrOnAmd]
Enabled=1
LocalStructure=1.1
UseFsrInputs=0
```

This proves config control only. The controls helper is not the main runtime route and does not prove a visible image change.

## Standalone Probe Evidence

Run from the repository root:

```powershell
.\probe\run_probe.ps1 -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1
```

The probe writes private test output under `probe\runs\`, including reports, comparison summaries, and captured buffers.

Accepted standalone probe evidence currently available showed:

```text
HIP_VISIBLE_DEVICES=1 was set only in the child process.
Startup delay after proxy load was long enough for hook initialization.
Engine initialization was observed.
About 400 neural jobs completed.
Approximate job time was 15-16 ms/job.
Self-check zero-output rate was 0.132%, accepted as healthy.
Off image matched the generated source exactly.
On image was nonblack.
On RGB mean absolute difference was 8.89.
On max channel delta was 55.
```

A passing standalone probe is a prerequisite. It does not prove the full Lossless Scaling path by itself.

Reject a probe result if completed-job logs are paired with invalid kernel errors, black output, mismatched source sizes, missing captures, or no visible on/off difference.

## Bridge Evidence

The bridge captures a selected source window, feeds a private D3D12 NR swapchain, and shows a visible D3D11 output window that Lossless Scaling can capture. It is currently a developer diagnostic route and an integration component, not proof of final end-user release by itself.

Accepted bridge evidence currently available showed:

```text
Source scenario: game menu, 960x540 bridge output
Off output max error: 0
Full-strength on output vs NR max error: 0
0.9 blend max error: <= 1
Runtime self-check zero-output rate: 0.116%, accepted as healthy
Runtime jobs: about 15-16 ms/job
Hotkeys: Ctrl+Alt+F6/F7/F8 verified for bridge output
```

Accepted frozen-source bridge evidence currently available showed:

```text
Runtime health: healthy
LocalTone: enabled
SkinStructure: enabled
Frozen proof images: saved locally
Mean difference: 3.40
Max delta: 49
```

These runs verify bridge behavior for tested menu scenarios. They do not prove competitive gameplay suitability.

Lossless Scaling captured the bridge output through WGC and LS1 in the tested bridge path. That older 960-to-1440 test verified automatic Scale/Unscale only as a diagnostic. The requested native integration must not rely on geometry resizing.

Native 2560x1440 bridge proof showed same-frame input matching the original with max error `0`; NR output changed the image with mean absolute difference `2.87` and max channel delta `55`; neural jobs took about `63 ms/job`. That is bridge/runtime timing, not full Lossless Scaling end-to-end latency. Do not make a competitive-play claim from this evidence.

## Native Auto-Scale Evidence

The native wrapper is implemented and the fake integration harness passes all 9 current cases. This verifies wrapper control flow in a harness, including the shape of the automatic path.

Public release still requires a real app proof:

- Press Scale in the actual Lossless Scaling app.
- Confirm the auto-scale wrapper starts the bridge without a separate launcher or source picker.
- Confirm Lossless Scaling captures the bridge output window.
- Confirm native WGC resolution is used.
- Confirm the wrapper keeps the Lossless Scaling path at native resolution with no geometry resizing.
- Confirm `Ctrl+Alt+F6` toggles the live output.
- Confirm `Ctrl+Alt+F7` and `Ctrl+Alt+F8` adjust live blend.
- Compare off/on frames against a static desktop image.

## Release Acceptance

Release acceptance requires all of these:

- The real Lossless Scaling Scale button starts the integration automatically.
- Off and on captures use the same deterministic source content.
- Native WGC resolution is used.
- No geometry resampling or upscaling is part of the claim.
- The on path loads the intended private runtime.
- Runtime logs or counters show completed neural work.
- Captured on buffers differ from the deterministic source pattern.
- Captured off/on buffers differ in a way that matches the enabled setting.
- The final comparison uses ordinary static desktop content, such as a browser image.
- Only approved analytical comparison crops are uploaded.
- No paid Lossless Scaling files, vendor runtime files, model files, logs, captures, or user-specific files are required in the public repository.

Reject the release claim if the only evidence is:

- An INI value changed.
- A DLL loaded.
- A hotkey notification appeared.
- Startup INI values changed without a runtime path that polls them.
- A ReShade or ordinary post-processing filter changed the image.
- A log exists without completed neural work.
- Completed-job logs are paired with invalid kernel errors or black output.
- A dynamic scene changed between off/on captures and was not frozen or otherwise matched.

## Test Assets

Use generated deterministic patterns for public fixtures. Do not publish:

- Screenshots from the user desktop unless the user explicitly approves that exact image.
- Windows wallpaper images.
- Movie frames.
- Browser captures containing private data.
- Logs with usernames, machine paths, GPU serials, or hardware identifiers.
