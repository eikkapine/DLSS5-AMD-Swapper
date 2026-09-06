# DLSS NR on AMD Controls

`DlssNrControl` is a small Windows helper for AMD DLSS NR startup INI settings. It edits only the `[DlssNrOnAmd]` section at the explicit config path you pass in.

The helper controls startup configuration only. It does not poll a running runtime, control the live rendered effect, or prove that neural rendering is active in the image path.

## Build

```powershell
cd controls
dotnet build .\DlssNrControl\DlssNrControl.csproj -c Release
```

The release executable is written to:

```text
controls\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe
```

## One-Shot Commands

Always pass the target INI file explicitly:

```powershell
$config = "<Lossless Scaling install folder>\dlssnr_on_amd.ini"
$control = ".\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe"

& $control --config $config --get
& $control --config $config --on
& $control --config $config --off
& $control --config $config --toggle
& $control --config $config --increase
& $control --config $config --decrease
```

`--get` reports configured values only. It does not claim that neural rendering is active in the running image path.

## Tray Hotkeys For Startup Config

```powershell
& $control --config $config --listen
```

While the tray helper is running:

- `Ctrl+Alt+F6` toggles `Enabled`.
- `Ctrl+Alt+F7` decreases `LocalStructure` by `0.1`.
- `Ctrl+Alt+F8` increases `LocalStructure` by `0.1`.

`LocalStructure` is clamped from `0.0` to `2.0` and defaults to `1.0` if the key is missing or malformed. The tray helper reloads the INI from disk for each command, writes atomically, and exits from its tray menu.

These hotkeys update the INI. They should not be documented as live effect controls for an already-running render path.

## Self-Test

```powershell
.\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe --self-test
```

The self-test uses temporary INI files only.

## Listener Smoke Test

```powershell
.\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe --listener-smoke
```

The listener smoke test starts the native hotkey window against a temporary INI, posts bounded test hotkey messages, and verifies the single-instance guard. It confirms registration and message handling. It does not prove that a physical keyboard event was pressed by a user.

## Live Bridge Controls

The live bridge is being developed separately. It owns the intended runtime controls:

| Hotkey | Bridge action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle bridge output on/off |
| `Ctrl+Alt+F7` | Decrease live output blend toward original |
| `Ctrl+Alt+F8` | Increase live output blend toward actual NR output |

That bridge blend is a `0.0` to `1.0` output mix between the original frame and actual NR output. It is not the INI `LocalStructure` value.
