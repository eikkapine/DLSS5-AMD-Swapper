# Controls helper

`DlssNrControl` is an optional .NET helper for editing **startup** values in a `dlssnr_on_amd.ini` file.

It is separate from the live bridge hotkeys. Editing the INI does not prove that a currently running frame is being processed.

## Build

```powershell
dotnet build .\controls\DlssNrControl\DlssNrControl.csproj -c Release
```

## Commands

Always pass the target INI explicitly:

```powershell
$config = "<path to dlssnr_on_amd.ini>"
$control = ".\controls\DlssNrControl\bin\Release\net8.0-windows\DlssNrControl.exe"

& $control --config $config --get
& $control --config $config --on
& $control --config $config --off
& $control --config $config --toggle
& $control --config $config --increase
& $control --config $config --decrease
```

`--listen` runs the helper as a tray listener for startup-config changes.

## Startup-config hotkeys

| Shortcut | INI action |
| --- | --- |
| `Ctrl+Alt+F6` | Toggle `Enabled` |
| `Ctrl+Alt+F7` | Reduce `LocalStructure` by `0.1` |
| `Ctrl+Alt+F8` | Increase `LocalStructure` by `0.1` |

`LocalStructure` is clamped from `0.0` to `2.0`.

For the **running visual output**, the same key combinations are handled by `DlssNrBridge.exe` and control bypass/blend directly. See [bridge/README.md](../bridge/README.md).

## Helper checks

```powershell
& $control --self-test
& $control --listener-smoke
```

These validate INI/message handling only; they do not run the neural renderer.
