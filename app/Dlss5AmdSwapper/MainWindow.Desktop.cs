using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Dlss5AmdSwapper;

public partial class MainWindow
{
    private Forms.NotifyIcon? _tray;
    private bool _exitRequested;
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set { _settings.MinimizeToTray = value; OnPropertyChanged(); SaveSettings(); }
    }
    public bool LightTheme
    {
        get => _settings.Theme == "Light";
        set { _settings.Theme = value ? "Light" : "Dark"; ApplyTheme(); OnPropertyChanged(); SaveSettings(); }
    }

    private void InitializeDesktop()
    {
        ApplyTheme();
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Dlss5AmdSwapper;component/Assets/Dlss5AmdSwapper.ico"));
            using var stream = resource!.Stream;
            using var sourceIcon = new System.Drawing.Icon(stream);
            _tray = new Forms.NotifyIcon { Text = "DLSS5 AMD Swapper", Icon = (System.Drawing.Icon)sourceIcon.Clone(), Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Swapper", null, (_, _) => Dispatcher.Invoke(() => RestoreFromTray()));
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(RequestExit));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => RestoreFromTray());
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    internal void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    internal void RestoreFromTray(bool activate = true)
    {
        if (_closing) return;
        Show();
        WindowState = WindowState.Normal;
        if (activate) Activate();
    }

    private void DisposeDesktop()
    {
        var tray = _tray;
        _tray = null;
        if (tray is null) return;
        tray.Visible = false;
        var menu = tray.ContextMenuStrip;
        var icon = tray.Icon;
        tray.ContextMenuStrip = null;
        tray.Icon = null;
        tray.Dispose();
        menu?.Dispose();
        icon?.Dispose();
    }

    private void ApplyTheme() => Services.ThemeService.Apply(System.Windows.Application.Current.Resources, LightTheme);
}
