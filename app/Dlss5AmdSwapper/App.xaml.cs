using System.Windows;

namespace Dlss5AmdSwapper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Helper windows (including the tray menu) must never keep the manager
        // alive after its main window has actually closed. Hiding to the tray
        // does not close MainWindow, so the user's tray preference still works.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        base.OnStartup(e);
        if (MainWindow is null)
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }
}
