# September manager verification

The manager update was checked on 10 September 2026.

- Release smoke suite passed, including install/restore preservation, runtime-control concurrency and cancellation, stale-state clearing, bounded log parsing, sanitized export, x64 validation and anti-cheat gating.
- A clean export of the staged source built and passed the same suite. The C# Models directory is now tracked; a broad ignore rule previously excluded it. Windows CI runs the suite from a fresh checkout.
- Self-contained Windows package and native bridge/wrapper builds completed. The unchanged C++ bridge emits an existing deprecated `/await` compiler warning.
- Package checksum and path validation passed. External runtime files are excluded.
- The actual packaged WPF app was inspected in dark/light themes. Search narrowed the library without selecting a game, and wheel scrolling over game cards moved the list. The overview setup action is disabled with no game selected.
- App screenshots show the actual packaged overview and settings, without private paths or logs.

This is manager verification, not a fresh gameplay benchmark. Neural rendering quality, live application of saved settings and game frame time were not re-measured for this release. Full DPI/accessibility coverage and all feature combinations remain unverified. A resize through the automation surface produced a native title-bar/clipping anomaly below the declared minimum size; normal and maximized layouts were inspected, but this is not a claim of exhaustive resize validation.

See [feature comparison](swapper-parity.md) for remaining NVIDIA-manager gaps and AMD consumer restrictions.
