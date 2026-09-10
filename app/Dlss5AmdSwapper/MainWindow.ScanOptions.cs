using System.Windows;
using Microsoft.Win32;

namespace Dlss5AmdSwapper;

public partial class MainWindow
{
    private CancellationTokenSource? _scanCancellation;
    private bool _isScanning;

    public bool IsScanning { get => _isScanning; private set { Set(ref _isScanning, value); OnPropertyChanged(nameof(CanStartScan)); } }
    public bool CanStartScan => !IsScanning;
    public bool ScanAllDrives
    {
        get => _settings.ScanAllDrives;
        set { _settings.ScanAllDrives = value; OnPropertyChanged(); SaveSettings(); }
    }
    public string AdditionalScanFoldersText => _settings.AdditionalScanFolders.Count == 0
        ? "No extra folders. Launcher libraries and common game folders are scanned automatically."
        : string.Join(Environment.NewLine, _settings.AdditionalScanFolders);

    private void AddScanFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsScanning) return;
        var dialog = new OpenFolderDialog { Title = "Add a game or library folder" };
        if (dialog.ShowDialog(this) != true) return;
        var folder = Path.GetFullPath(dialog.FolderName);
        if (!_settings.AdditionalScanFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            _settings.AdditionalScanFolders.Add(folder);
            SaveSettings();
            OnPropertyChanged(nameof(AdditionalScanFoldersText));
        }
    }

    private void ClearScanFolders_Click(object sender, RoutedEventArgs e)
    {
        if (IsScanning) return;
        _settings.AdditionalScanFolders.Clear();
        SaveSettings();
        OnPropertyChanged(nameof(AdditionalScanFoldersText));
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();
}
