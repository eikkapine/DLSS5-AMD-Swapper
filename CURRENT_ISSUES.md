# Current Issues / Claude Handoff

## Current checkpoint — 24 September 2026

v0.3.0-pre.4 (branch `feat/amd-ecosystem-refresh`, unreleased) adds AMD-NR pre-SR packages (build-string identification, passes under `Runtime\`, `Runtime/` checksums enforced, optional lmxxf runtime), deletes rewritten runtime logs on Restore so the manifest no longer survives a clean restore, and adds AMD-NR to the runtime release check. See [ecosystem refresh](docs/amd-ecosystem-20260924.md). It also carries the post-pre.3 issue #3 bridge fixes below, so it is the build issue #3 needs for a retest. AMD-NR has not been launched in a game; do not claim gameplay compatibility until a fresh session shows completed passes.

## Checkpoint — 19 September 2026

v0.3.0-pre.3 refreshes official v0.3.1 installer verification, modern asynchronous bridge configuration, startup HIP diagnostics, and runtime release checking. See [upstream refresh](docs/upstream-refresh-20260919.md) and [release validation](docs/releases/v0.3.0-pre.3.md). Issue #3 remains open for affected-machine confirmation; its original legacy runtime stall is not proven resolved. Preserve the Crimson Desert compatibility pin and require fresh completed neural work before claiming a successful runtime session.

Post-pre.3 correction from the issue #3 retests: the bridge aborted a healthy v0.3.1 startup because its fatal-marker check matched any occurrence of `fault`, including the informational DRED notice, and its HIP instrumentation required every named import, so v0.3.1's different wait API disabled the whole trace. Both are fixed in the bridge only; the reporter still needs a build carrying them, so a pre.4 package remains outstanding before issue #3 can be retested.

The notes below record the earlier v0.3.0-pre.2 checkpoint.

Branch: optiscaler-presr

1. Crimson Desert current build: the latest runtime session loads but does not reach verified Neural Rendering activity. Fresh evidence is missing engine initialization, FidelityFX dispatch, and neural network-job lines. Older sessions did reach those stages. Investigate compatibility with the current Steam build and only report success from a new launch session.

2. Diagnostics correctness: finish and verify the pending session-scoped diagnostics work so old successful log sections can never make a newer failed session appear active.

3. Diagnostics refresh overlap: the automatic selected-game refresh can be started again before a previous inspection finishes. Add a lightweight single-flight or cancellation guard without blocking the UI.

4. Hotkey/live-toggle truthfulness: F6 currently saves the desired Enabled state, but there is no verified proof that the already-running post-FSR runtime applies it immediately. Keep the UI truthful unless a supported live-control mechanism can be verified with fresh evidence.

5. Managed-install safety: review the pending additional-loader detection and its regression coverage so updates cannot silently proceed into a conflicting runtime setup.

6. Crimson compatibility pin: keep the game-specific official v0.2.17 fallback narrow and source-verified. Do not weaken the known-incompatible runtime guard.

7. Full validation still required after the latest edits: rerun C# smoke/regression tests, Python direct-game tests, publication checker, relevant .NET build/tests, git diff --check, and app/Build-Package.ps1.

8. Rebuild and re-test Crimson Desert after the diagnostics fixes. The manager must report stalled/inactive when the newest session lacks neural evidence; Enabled=1 or good performance alone is not proof.

9. Publication audit before push: no personal paths/usernames, private logs/configs/manifests, secrets, paid Lossless Scaling files, restricted runtime/model binaries, private third-party assets, or anecdotal FPS claims. Public measurements must come from reproducible logs.

10. Final integration: rebuild local package, verify a fresh Crimson session, audit the diff, commit optiscaler-presr, and push only after checks pass. Preserve unrelated working-tree changes.

## Status 2026-09-12 (commit 60e83a6, pushed to optiscaler-presr)
1 done (upstream: game build 25246367 breaks 3 runtime detours; evidence in docs/verification-20260912.md; app/CLI report it). 2 done (session-scoped; sampled logs no longer stitch head+tail). 3 done (single-flight + pending re-run). 4 done. 5 done (C# + CLI). 6 done (pin narrow, digest fallback shared via UpstreamReleases.cs). 7 done except app/Build-Package.ps1 (not run; RELEASE.json manager_release hash empty, published=false). 8 partial (fresh session classified via CLI --diagnose; UI status strings unit-tested, not eyeballed). 9 done. 10 pushed to branch; not merged to main, no release.
Pending: run app/Build-Package.ps1, fill RELEASE.json hash, optional Gemini re-review of 60e83a6, then release v0.3.0-pre.2.

Published v0.3.0-pre.2 (main 32e4ee5, tag 54fbe49): https://github.com/eikkapine/DLSS5-AMD-Swapper/releases/tag/v0.3.0-pre.2
