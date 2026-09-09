# Development

There are now two performance targets:

1. **practical output performance** using the pre-upscale `WorkingScale` path, and
2. **native 1:1 Neural Rendering** for maximum image fidelity and future kernel/runtime work.

The current public checkpoint, `v0.1.0-pre.3-dev.3`, is the first one focused on the practical path. It runs a 2560×1440 source at 1920×1080 neural resolution with `WorkingScale=0.75`, keeping the same model, weights, neural precision/settings and full effect strength. The final enlargement is handled by Lossless Scaling.

On the RX 9070 XT test system this reached about 30 FPS base output and about 60 FPS with Lossless Scaling 2× frame generation. The selected full-effect bridge window was 28.84 changed RGB submissions/s.

## Invariants I keep for normal performance work

- full neural effect strength unless I am explicitly testing the live blend
- the same compatibility runtime model/weights and neural precision/settings
- `Inline=0` / asynchronous path where the current setup requires it
- GPU ownership/fence ordering and failure-drain lifetime rules
- exact 1:1 texel handling whenever the source and neural dimensions match
- paid/vendor/private runtime files outside Git

`WorkingScale` is an explicit quality/performance control. A value below 1.0 is a deliberate resolution tradeoff and is documented as such rather than being described as a same-quality native optimization.

## Current optimization order

The work so far has reduced host-side overhead with shared GPU transport, duplicate suppression, inference-aware feeding, direct WGC SRV use and output/history copy elision.

The largest remaining native-resolution cost is still inside the neural GPU workload. Current research points toward:

- fewer neural pixels before final upscale
- kernel fusion to reduce memory round-trips
- lower VGPR/LDS pressure on RDNA4
- better occupancy / wave-level tuning
- avoiding synchronization bubbles between compute stages

The external compatibility runtime does not currently expose enough source here to safely rewrite those neural kernels inside NR Auto Scale, so dev.3 implements the largest architecture-level lever available in this project: running NR before the final upscale.

## Validation discipline

I keep these numbers separate:

- user-observed base FPS
- user-observed LSFG/display output
- changed RGB submissions
- bridge feed/presentation rate
- HIP waits and runtime timing

Raw logs remain private. Public measurement JSON contains only hashes and sanitized aggregates.

Before publishing a checkpoint I build the production bridge/wrapper, verify artifact hashes, inspect the staged files, create the allowlisted ZIP, and confirm it contains no paid Lossless Scaling file, external vendor runtime, private config/log or unreviewed screenshot.
