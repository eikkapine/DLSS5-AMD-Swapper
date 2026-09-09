# Reduced neural working resolution

## Why this candidate changes the pipeline

The remaining RX 9070 XT measurements point to the neural GPU job, not host submission, as the dominant cost. Recent DLSS 5 community work reached the same conclusion from the NVIDIA side: running Neural Rendering before the final Super Resolution pass reduces the number of pixels evaluated by the neural network and produces much larger gains than small host-side changes.

Relevant current references checked on 9 September 2026:

- TechPowerUp summary of the Neural Upstream performance work: <https://www.techpowerup.com/352476/modders-rework-dlss-5s-rendering-pipeline-for-a-big-performance-boost>
- `danielblnc/DLSS-NR-on-AMD` current project and its roughly 33 FPS 1080p RX 9070 XT note: <https://github.com/danielblnc/DLSS-NR-on-AMD>
- The latest checked upstream release is v0.2.16 (8 September 2026). Its listed changes are compatibility/proxy/RDNA3 fixes rather than a performance-specific kernel change, so dev.3 keeps the already-installed v0.2.15 runtime to avoid mixing an unrelated runtime update into the first working-scale comparison: <https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.2.16>
- RX 9070 XT resolution scaling report showing roughly 15 FPS at 3440x1440 versus a 30 FPS cap at 2016x840: <https://github.com/danielblnc/DLSS-NR-on-AMD/issues/116>
- Adjustable-resolution feeder documenting the square relationship between axis scale and neural pixel count: <https://github.com/Phroster/DLSS5-Feeder-Adjustable-Resolution>
- OptiScaler pre-SR experiments using a separate `WorkingScale`: <https://github.com/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass>

Those projects are references for the architecture. This implementation uses the existing NR Auto Scale capture/transport code and does not copy the GPL OptiScaler implementation.

## dev.3 implementation

`WorkingScale=0.75` is the new install preset. For a 2560x1440 source this resolves to 1920x1080, so the neural pass sees 2,073,600 pixels instead of 3,686,400: 56.25% of the native workload before fixed overheads.

The steady-state path is:

```text
WGC source at native size
        -> D3D11 SRV
        -> one hardware bilinear draw into the smaller shared neural texture
        -> DLSS-NR-on-AMD / HIP neural evaluation
        -> bridge output at neural working size
        -> selected Lossless Scaling scaler
        -> display size
```

If source and neural dimensions are identical, the existing exact `Load` path is retained. If WGC cannot expose the capture surface directly as an SRV, the fallback copy is allocated at source size and is still resized on the GPU. Full-frame CPU capture/readback is reserved for diagnostics and compatibility fallback.

The model, weights, neural precision/settings and full effect strength are unchanged. Reduced working resolution is therefore a deliberate resolution/performance tradeoff, not a weaker neural preset. `WorkingScale=0` disables it; `WorkingScale=0` plus `NativeResolution=1` restores the native 1:1 path.

Kernel timing diagnostics are now opt-in with `--hip-kernel-timing` so normal performance candidates do not pay instrumentation overhead that is no longer needed for every run.

## Validation boundary

Production compilation and static inspection are permitted for this candidate. No automated GPU test, synthetic scene, benchmark or gameplay is run by the agent. The user manually checks image quality, base FPS and normal Lossless Scaling behavior after deployment.
