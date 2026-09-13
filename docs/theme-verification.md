# Theme verification

Run from the repository root on Windows:

```powershell
dotnet run --project app/Dlss5AmdSwapper.ThemeTests/Dlss5AmdSwapper.ThemeTests.csproj -c Release -- artifacts/theme-verification
```

The test host uses the real application's compiled WPF resources, main window and setup dialog. It writes PNG renders and `contrast-report.json` in the output folder. It never shows a window, opens a native popup, or sends mouse/keyboard input. Settings and fixture games use a temporary test folder; startup scans, downloads, tray icons and global hotkeys are disabled.

Coverage includes both themes, switching back to dark on the same main window, all four app pages at normal and minimum window size, settings below the scroll boundary, available/unavailable setup routes, selected dropdown rows, disabled actions and fields, checkboxes, radio buttons, switches, context menus and tooltips. Popup controls render directly from their templates without opening on the desktop.

Contrast checks inspect effective text foregrounds and painted backgrounds in the realized visual tree, including gradient endpoints. Normal text requires 4.5:1 and large text 3:1. Semantic text brushes are also checked against every app surface. Each rendered surface must contain checked text, so an empty render cannot pass. The images are the additional visual check for spacing and clipping; numeric contrast alone does not establish layout quality.

The test does not simulate pointer hover, keyboard focus, native file pickers or Windows title bars. Those use their respective Windows interaction paths. The app's custom title-button hover and focus resources are theme-aware.
