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
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Dlss5AmdSwapper.ico"));
            using var stream = resource!.Stream;
            using var sourceIcon = new System.Drawing.Icon(stream);
            _tray = new Forms.NotifyIcon { Text = "DLSS5 AMD Swapper", Icon = (System.Drawing.Icon)sourceIcon.Clone(), Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Swapper", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() => { _exitRequested = true; Close(); }));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void DisposeDesktop()
    {
        if (_tray is null) return;
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Icon?.Dispose();
        _tray.Dispose();
        _tray = null;
    }

    private void ApplyTheme()
    {
        var light = LightTheme;
        System.Windows.Application.Current.Resources["HeroStartColor"] = (Color)ColorConverter.ConvertFromString(light ? "#FFF0EA" : "#291719");
        System.Windows.Application.Current.Resources["HeroMiddleColor"] = (Color)ColorConverter.ConvertFromString(light ? "#FFFFFF" : "#141821");
        System.Windows.Application.Current.Resources["HeroEndColor"] = (Color)ColorConverter.ConvertFromString(light ? "#F4F6FA" : "#10151D");
        var palette = new Dictionary<string, (string Dark, string Light)>
        {
            ["BgBrush"] = ("#090B10", "#F1F3F7"),
            ["PanelBrush"] = ("#10141C", "#FFFFFF"),
            ["CardBrush"] = ("#151A24", "#FFFFFF"),
            ["CardHoverBrush"] = ("#1B2230", "#E5E9F1"),
            ["LineBrush"] = ("#252D3D", "#CDD3DF"),
            ["TextBrush"] = ("#F7F8FA", "#18212F"),
            ["MutedBrush"] = ("#929BAD", "#526178"),
            ["AccentBrush"] = ("#FF5847", "#C93829"),
            ["SuccessBrush"] = ("#5EE29A", "#16723F"),
            ["WarningBrush"] = ("#FFC45C", "#865100"),
            ["DangerBrush"] = ("#FF6472", "#B62D40")
        };
        foreach (var (key, colors) in palette)
            System.Windows.Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? colors.Light : colors.Dark));
    }
}
