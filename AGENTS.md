# NR Auto Scale development

Read `docs/progress.md`, `VERSION` and `RELEASE.json` before continuing this work.
The user tests candidate versions manually, reports FPS, and wants those runs
logged and analyzed while performance development continues.

- Preserve native application dimensions, model/weights, neural precision and
  settings, full effect strength and `Inline=0`. Preserve the current Lossless
  Scaling profile; never restore an older Settings.xml over user changes.
- Do not run automated tests, synthetic scenes, GPU benchmarks, shader probes
  or gameplay. Production compilation, static inspection and existing-log
  analysis are permitted. Do not revive the inline mode that caused a timeout.
- Keep GPU ownership, producer/consumer fences and failure-drain lifetimes.
  No global driver/TDR, power, security or Special K changes.
- Append sanitized measurements using `bridge/scripts/Analyze-Run.py`. Associate
  each run with its deployment hash, not just the current installed executable.
  Keep raw logs, private configuration, vendor binaries and weights out of Git.
- Separate user-reported FPS, changed RGB submissions, HIP waits, neural jobs
  and LSFG/display output. Multiplying by 2 is a nominal estimate, not a measured
  display rate. Different gameplay runs are not controlled A/B tests.
- Publish requested checkpoints with version, build hashes, findings and exact
  validation limits. Distinguish manually exercised builds from new candidates.
  Preserve unrelated work. Use explicit paths when staging and inspect release
  contents; never force-push or overwrite older measurement records/releases.
