# Desktop lifecycle regression tests

On Windows with the .NET 8 SDK:

```powershell
dotnet run --project app/Dlss5AmdSwapper.DesktopTests -c Release
```

The runner starts five bounded child processes using the actual WPF application
and main window. Windows stay offscreen, never activate, and never appear on the
taskbar. No mouse or keyboard automation is used. Settings use unique temporary
folders; startup discovery, network work, and global hotkeys are disabled.

Cases cover fresh settings (close exits), normal close with an extra hidden
helper window, a saved tray preference (hide, restore, explicit Exit), refusal
to exit during installation, and cancelling a library scan while stopping timers.
The real notification icon is initialized and disposed to exercise its lifetime.
Every child must finish within 15 seconds; the runner cleans up only a child it
started if a regression leaves that process alive.

The manager's `SpecialK.deny.Dlss5AmdSwapper` file opts only that executable out of
Special K global graphics injection. A matching test-host marker keeps the tests
independent of optional locally installed graphics overlays. Neither marker adds
a dependency or changes any game's overlay settings.
