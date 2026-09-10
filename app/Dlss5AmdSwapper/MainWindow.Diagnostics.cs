using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dlss5AmdSwapper.Services;
using Microsoft.Win32;

namespace Dlss5AmdSwapper;

public partial class MainWindow
{
    private readonly ActivityHistoryService _activityHistory = new();
    private bool _diagnosticExportBusy;
    private Grid? _diagnosticOverlay;

    private void RecordActivity(string action, string detail = "") => _activityHistory.Record(action, detail);

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticExportBusy || _diagnosticOverlay is not null) return;
        var game = SelectedGame;
        if (game is null) { ShowToast("Select a game before exporting runtime diagnostics."); return; }
        _diagnosticExportBusy = true;
        try
        {
            var evidence = await Task.Run(() => _diagnostics.InspectAsync(game));
            if (_closing) return;
            var report = DiagnosticsReportService.Create(game, evidence);
            ShowDiagnosticsOverlay("Review diagnostics", "Only the fields below will be saved. No raw logs, game names, paths or activity history are included. Nothing is uploaded.", report,
                "Save report", () =>
                {
                    var dialog = new SaveFileDialog
                    {
                        Title = "Save reviewed diagnostics", FileName = "DLSS5-AMD-Swapper-diagnostics.json",
                        Filter = "JSON report (*.json)|*.json", DefaultExt = ".json", AddExtension = true
                    };
                    if (dialog.ShowDialog(this) != true) return;
                    try
                    {
                        File.WriteAllText(dialog.FileName, report);
                        RecordActivity("Diagnostics exported", "Reviewed structured report saved locally.");
                        ShowToast("Diagnostics saved.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowToast("Could not save report: " + ex.Message); }
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowToast("Could not read runtime evidence: " + ex.Message); }
        finally { _diagnosticExportBusy = false; }
    }

    private void ActivityHistory_Click(object sender, RoutedEventArgs e)
    {
        var entries = _activityHistory.Read();
        var text = entries.Count == 0 ? "No activity recorded yet." : string.Join("\n\n", entries.Select(entry => $"{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {entry.Action}\n{entry.Detail}"));
        ShowDiagnosticsOverlay("Activity history", "The latest 200 actions are stored on this computer. History stays local and is excluded from diagnostics exports.", text,
            "Clear history", () => { _activityHistory.Clear(); ShowToast("Local activity history cleared."); }, closeAfterAction: true);
    }

    private void ShowDiagnosticsOverlay(string title, string subtitle, string text, string actionLabel, Action action, bool closeAfterAction = false)
    {
        if (_diagnosticOverlay is not null || Content is not Border { Child: Grid root }) return;
        var previousFocus = Keyboard.FocusedElement;
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)) };
        _diagnosticOverlay = overlay;
        Grid.SetRowSpan(overlay, Math.Max(1, root.RowDefinitions.Count));
        Grid.SetColumnSpan(overlay, Math.Max(1, root.ColumnDefinitions.Count));
        Panel.SetZIndex(overlay, 100);
        KeyboardNavigation.SetTabNavigation(overlay, KeyboardNavigationMode.Cycle);
        var disabled = root.Children.OfType<UIElement>().Select(child => (child, child.IsEnabled)).ToArray();
        foreach (var (child, _) in disabled) child.SetCurrentValue(IsEnabledProperty, false);

        var card = new Border
        {
            Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(24), Margin = new Thickness(32),
            MaxWidth = 850, MaxHeight = 600, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        var description = new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 10, 0, 18) };
        Grid.SetRow(description, 1); body.Children.Add(description);
        var preview = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Background = (Brush)FindResource("BgBrush"), Foreground = (Brush)FindResource("TextBrush"), Padding = new Thickness(12)
        };
        Grid.SetRow(preview, 2); body.Children.Add(preview);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var close = new Button { Content = "Close", MinWidth = 90, Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = actionLabel, MinWidth = 110, Style = (Style)FindResource("PrimaryButton") };
        void Dismiss()
        {
            root.Children.Remove(overlay);
            _diagnosticOverlay = null;
            foreach (var (child, enabled) in disabled) child.SetCurrentValue(IsEnabledProperty, enabled);
            if (previousFocus is not null) Keyboard.Focus(previousFocus);
        }
        close.Click += (_, _) => Dismiss();
        save.Click += (_, _) => { action(); if (closeAfterAction) Dismiss(); };
        overlay.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Dismiss(); } };
        buttons.Children.Add(close); buttons.Children.Add(save); Grid.SetRow(buttons, 3); body.Children.Add(buttons);
        card.Child = body; overlay.Children.Add(card); root.Children.Add(overlay); close.Focus();
    }
}
