using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dlss5AmdSwapper;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

internal static class Program
{
    private static readonly List<object> Checks = [];
    private static readonly List<string> Failures = [];
    private static string _output = "";

    [STAThread]
    private static int Main(string[] args)
    {
        _output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "theme-verification"));
        Directory.CreateDirectory(_output);
        var isolatedSettings = Path.Combine(Path.GetTempPath(), "swapper-theme-tests-" + Guid.NewGuid().ToString("N"));
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Do not Show/ShowDialog or synthesize mouse or keyboard input. These are WPF
        // visual-tree renders; startup scanning, downloads and settings use are isolated.
        var window = new MainWindow(new AppSettingsService(isolatedSettings), initializeBackgroundServices: false, initializeTrayIcon: false);
        window.MinimizeToTray = false;
        window.Games.Add(new GameEntry { Name = "Assetto Corsa Rally · UI fixture", ExePath = Path.Combine(isolatedSettings, "Rally", "Fixture.exe"), Store = "Steam", Status = "Installed · awaiting runtime evidence", Installed = true, Route = InstallRoute.OptiScalerPreSr });
        window.Games.Add(new GameEntry { Name = "Cyberpunk 2077 · UI fixture", ExePath = Path.Combine(isolatedSettings, "Cyberpunk", "Fixture.exe"), Store = "Steam", Status = "Compatibility needs review" });
        try
        {
            foreach (var light in new[] { false, true, false })
            {
                var theme = light ? "light" : "dark";
                window.LightTheme = light;
                VerifyPalette(theme, app.Resources);
                foreach (var page in new[] { "HomePage", "GamesPage", "LosslessPage", "SettingsPage" })
                {
                    foreach (var other in new[] { "HomePage", "GamesPage", "LosslessPage", "SettingsPage" })
                        ((UIElement)window.FindName(other)).Visibility = other == page ? Visibility.Visible : Visibility.Collapsed;
                    Render((FrameworkElement)window.Content, 1280, 820, $"{theme}-{page}");
                    if (window.FindName(page) is ScrollViewer scroll && scroll.ScrollableHeight > 0)
                    {
                        scroll.ScrollToBottom();
                        Render((FrameworkElement)window.Content, 1280, 820, $"{theme}-{page}-bottom");
                        scroll.ScrollToTop();
                    }
                    Render((FrameworkElement)window.Content, 1080, 680, $"{theme}-{page}-minimum");
                }

                Render(BuildControlGallery(), 1160, 790, $"{theme}-controls");
                RenderPopups(theme);
                foreach (var available in new[] { true, false })
                {
                    var dialog = new SetupDialog("Assetto Corsa Rally · theme fixture", "Local OptiScaler package verified", "Local generated weights verified", available, OptiScalerPreset.Balanced,
                        "Enable the game's DLSS upscaling option, then open the OptiScaler Del overlay. Refresh evidence after driving to verify completed Neural Rendering frames.");
                    var content = (FrameworkElement)dialog.Content;
                    content.Measure(new Size(560, double.PositiveInfinity));
                    Render(content, 560, Math.Ceiling(content.DesiredSize.Height), $"{theme}-setup-{(available ? "available" : "unavailable")}");
                    dialog.Close();
                }
            }
        }
        finally
        {
            window.Close();
            if (Directory.Exists(isolatedSettings)) Directory.Delete(isolatedSettings, recursive: true);
        }

        File.WriteAllText(Path.Combine(_output, "contrast-report.json"), JsonSerializer.Serialize(new { checks = Checks, failures = Failures }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var failure in Failures.Distinct()) Console.Error.WriteLine(failure);
        Console.WriteLine($"{Checks.Count} contrast checks; {Failures.Count} failures. Offscreen renders: {_output}");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void VerifyPalette(string theme, ResourceDictionary resources)
    {
        foreach (var foreground in new[] { "TextBrush", "MutedBrush", "AccentBrush", "Accent2Brush", "SuccessBrush", "WarningBrush", "DangerBrush" })
            foreach (var background in new[] { "BgBrush", "PanelBrush", "CardBrush", "CardHoverBrush" })
                Check(theme, $"palette {foreground} / {background}", Color(resources, foreground), Color(resources, background), 4.5);
        Check(theme, "selected dropdown text", Color(resources, "AccentForegroundBrush"), Color(resources, "AccentBrush"), 4.5);
        foreach (var stop in new[] { "PrimaryStartColor", "PrimaryEndColor" })
            Check(theme, $"primary button {stop}", Color(resources, "PrimaryTextBrush"), (Color)resources[stop], 4.5);
    }

    private static Color Color(ResourceDictionary resources, string key) => ((SolidColorBrush)resources[key]).Color;

    private static FrameworkElement BuildControlGallery()
    {
        var grid = new Grid { Margin = new Thickness(24) };
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        var columns = Enumerable.Range(0, 3).Select(_ => new StackPanel { Margin = new Thickness(10) }).ToArray();
        for (var i = 0; i < columns.Length; i++) { Grid.SetColumn(columns[i], i); grid.Children.Add(columns[i]); }

        void Add(int column, FrameworkElement item) { item.Margin = new Thickness(0, 0, 0, 14); columns[column].Children.Add(item); }
        TextBlock Label(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold };
        Button Button(string text, string style, bool enabled = true) => new() { Content = text, Style = (Style)Application.Current.FindResource(style), IsEnabled = enabled };
        Add(0, Label("Actions and editable text"));
        Add(0, Button("Set up Neural Rendering", "PrimaryButton"));
        Add(0, Button("Set up · unavailable", "PrimaryButton", false));
        Add(0, Button("Refresh evidence", "GhostButton"));
        Add(0, Button("Refresh · unavailable", "GhostButton", false));
        Add(0, Button("Remove managed files", "DangerButton"));
        Add(0, new TextBox { Text = @"C:\Games\Fixture\Game.exe" });
        Add(0, new TextBox { Text = "Read-only diagnostics text", IsReadOnly = true });
        Add(0, new TextBox { Text = "Unavailable source path", IsEnabled = false });
        Add(0, new TextBlock { Text = "Accent values · 1.0 · Steam", Foreground = (Brush)Application.Current.FindResource("Accent2Brush") });

        Add(1, Label("Dropdowns and choices"));
        Add(1, new ComboBox { ItemsSource = new[] { "Quality", "Performance" }, SelectedIndex = 0 });
        Add(1, new ComboBox { ItemsSource = new[] { "Quality unavailable" }, SelectedIndex = 0, IsEnabled = false });
        foreach (var (text, selected, enabled) in new[] { ("Quality · selected row", true, true), ("Performance · normal row", false, true), ("Unavailable row", false, false) })
            Add(1, new ComboBoxItem { Content = new TextBlock { Text = text }, IsSelected = selected, IsEnabled = enabled });
        Add(1, new CheckBox { Content = "Checked preference", IsChecked = true });
        Add(1, new CheckBox { Content = "Unchecked preference" });
        Add(1, new CheckBox { Content = "Unavailable preference", IsChecked = true, IsEnabled = false });
        Add(1, new RadioButton { Content = "Selected setup route", IsChecked = true });
        Add(1, new RadioButton { Content = "Unavailable setup route", IsEnabled = false });
        Add(1, new CheckBox { Style = (Style)Application.Current.FindResource("SwitchStyle"), IsChecked = true });
        Add(1, new CheckBox { Style = (Style)Application.Current.FindResource("SwitchStyle"), IsChecked = false });

        Add(2, Label("Menus and help"));
        Add(2, new TextBlock { Text = "Context menus and tooltips are rendered separately from their actual control templates.", TextWrapping = TextWrapping.Wrap });
        foreach (var (key, text) in new[] { ("SuccessBrush", "Verified completed frames"), ("WarningBrush", "Installed · needs runtime evidence"), ("DangerBrush", "Missing runtime files"), ("MutedBrush", "Secondary explanation remains readable") })
            Add(2, new TextBlock { Text = text, Foreground = (Brush)Application.Current.FindResource(key), TextWrapping = TextWrapping.Wrap });

        var root = new Border { Background = (Brush)Application.Current.FindResource("BgBrush"), Child = grid };
        TextElement.SetForeground(root, (Brush)Application.Current.FindResource("TextBrush"));
        return root;
    }

    private static void RenderPopups(string theme)
    {
        // Popup controls cannot have a visual parent. Render them directly without
        // opening their native popup window or interrupting the user's desktop.
        var menu = new ContextMenu { Visibility = Visibility.Visible };
        menu.Items.Add(new MenuItem { Header = "Open game folder" });
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "Re-check compatibility" } });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Unavailable action", IsEnabled = false });
        Render(menu, 320, 128, $"{theme}-context-menu");
        var tooltip = new ToolTip { Visibility = Visibility.Visible, Content = new TextBlock { Text = "The app saves the next on/off state.\nUse the in-game overlay for live control.", TextWrapping = TextWrapping.Wrap } };
        Render(tooltip, 320, 66, $"{theme}-tooltip");
    }

    private static void Render(FrameworkElement root, double width, double height, string name)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        // Include the window's inherited background when rendering its transparent content.
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle((Brush)Application.Current.FindResource("BgBrush"), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(_output, name + ".png"))) encoder.Save(file);
        var previousChecks = Checks.Count;
        VerifyText(root, name);
        if (Checks.Count == previousChecks) Failures.Add($"{name}: no text was realized; the render check would be empty.");
    }

    private static void VerifyText(DependencyObject node, string render)
    {
        if (node is UIElement { Visibility: not Visibility.Visible }) return;
        if (node is TextBlock block && block.ActualWidth > 0 && block.ActualHeight > 0 && !string.IsNullOrWhiteSpace(block.Text)
            && !block.FontFamily.Source.Contains("Icons", StringComparison.OrdinalIgnoreCase) && block.Foreground is SolidColorBrush foreground)
        {
            var minimum = block.FontSize >= 24 || (block.FontSize >= 18.66 && block.FontWeight.ToOpenTypeWeight() >= 700) ? 3 : 4.5;
            foreach (var background in Backgrounds(block)) Check(render, block.Text, foreground.Color, background, minimum);
        }
        if (node is TextBox box && box.ActualWidth > 0 && box.Foreground is SolidColorBrush text && box.Background is SolidColorBrush fill)
            Check(render, box.Text, text.Color, fill.Color, 4.5);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) VerifyText(VisualTreeHelper.GetChild(node, i), render);
    }

    private static IEnumerable<Color> Backgrounds(DependencyObject child)
    {
        for (var node = child; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            // Inspect painted template surfaces. Control.Background may retain a
            // system default even when its template never draws that background.
            var brush = node switch { Border b => b.Background, Panel p => p.Background, _ => null };
            if (brush is SolidColorBrush solid && solid.Color.A == 255 && solid.Opacity == 1) return [solid.Color];
            if (brush is GradientBrush gradient && gradient.GradientStops.All(stop => stop.Color.A == 255))
                return gradient.GradientStops.Select(stop => stop.Color);
        }
        return [Color(Application.Current.Resources, "BgBrush")];
    }

    private static void Check(string scope, string label, Color foreground, Color background, double minimum)
    {
        var first = Luminance(foreground);
        var second = Luminance(background);
        var ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
        Checks.Add(new { scope, label, foreground = foreground.ToString(), background = background.ToString(), ratio = Math.Round(ratio, 2), minimum });
        if (ratio + .001 < minimum) Failures.Add($"{scope}: {label} ({foreground} on {background}) contrast {ratio:F2}:1 < {minimum}:1");
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
