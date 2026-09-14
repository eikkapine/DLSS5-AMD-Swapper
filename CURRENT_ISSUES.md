# Current Issues / Claude Handoff

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
