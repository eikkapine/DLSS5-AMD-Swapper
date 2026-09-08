# D3D12 runtime probe

This is a small standalone D3D12 renderer I use to check the neural runtime path without touching the Lossless Scaling installation.

It renders a deterministic generated pattern, runs the external runtime against it, and compares the captured output with the known source. Probe runs stay local under `probe\runs\` and are ignored by Git.

## Run it

From the repository root:

```powershell
.\probe\run_probe.ps1 -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1
```

For a normal visible swapchain:

```powershell
.\probe\run_probe.ps1 -Visible -Frames 700 -Seconds 25 -Width 640 -Height 360 -HipVisibleDevices 1
```

Use the HIP device index that actually selects your AMD GPU.

To inspect an existing probe run without launching the renderer again:

```powershell
.\probe\run_probe.ps1 -ValidateRun .\probe\runs\<run-id>
```

## What it checks

The runner compares an `Enabled=0` pass with an `Enabled=1` pass and records whether:

- the runtime initialized
- neural jobs completed
- the disabled output still matches the generated source
- enabled output is non-black/non-constant
- the enabled output differs from the source
- GPU/runtime error markers appeared

`compare-summary.txt` and `evidence.json` contain the compact result for a run. Raw run files are private development artifacts and are not published.

## Why this exists

The probe answers one narrow question: can the external D3D12 neural runtime produce a real changed image on the selected AMD path?

It does not verify the full Lossless Scaling integration, frame pacing, end-to-end latency, or game compatibility. Those are tracked separately in [Verification](../docs/verification.md).

## Optional variants

The runner also exposes controlled interop/inline/startup-delay options for debugging compatibility problems. They are development switches, not required for normal NR Auto Scale setup.
