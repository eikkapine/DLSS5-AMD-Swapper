using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Dlss5AmdSwapper.Models;
using Microsoft.Win32;

namespace Dlss5AmdSwapper;

public partial class MainWindow
{
    private ICollectionView? _libraryView;
    private readonly HashSet<GameEntry> _libraryObserved = [];
    private readonly HashSet<string> _hiddenGamePaths = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _libraryRefreshTimer;
    private string _librarySearch = string.Empty;
    private string _libraryFilter = "All games";

    public string[] LibraryFilters { get; } = ["All games", "Ready", "Installed", "Running", "Needs review", "Blocked"];
    public string[] LibrarySorts { get; } = ["Name A–Z", "Name Z–A", "Installed first"];
    public string LibrarySearch { get => _librarySearch; set { _librarySearch = value; OnPropertyChanged(); QueueLibraryRefresh(); } }
    public string LibraryFilter { get => _libraryFilter; set { _libraryFilter = value; OnPropertyChanged(); QueueLibraryRefresh(); } }
    public string LibrarySort { get => _settings.LibrarySort; set { if (value is null) return; _settings.LibrarySort = value; OnPropertyChanged(); QueueLibraryRefresh(); SaveSettings(); } }
    public bool ShowHiddenGames { get => _settings.ShowHiddenGames; set { _settings.ShowHiddenGames = value; OnPropertyChanged(); QueueLibraryRefresh(); SaveSettings(); } }
    public bool GroupGamesByStore { get => _settings.GroupGamesByStore; set { _settings.GroupGamesByStore = value; OnPropertyChanged(); QueueLibraryRefresh(); SaveSettings(); } }

    // Called once after InitializeComponent and DataContext assignment.
    private void InitializeLibrary()
    {
        _hiddenGamePaths.UnionWith(_settings.HiddenGames ?? []);
        if (!LibrarySorts.Contains(_settings.LibrarySort)) _settings.LibrarySort = LibrarySorts[0];
        _libraryView = CollectionViewSource.GetDefaultView(Games);
        _libraryView.Filter = IsLibraryGameVisible;
        _libraryRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _libraryRefreshTimer.Tick += (_, _) => { _libraryRefreshTimer.Stop(); RefreshLibraryView(); };
        Games.CollectionChanged += LibraryCollectionChanged;
        foreach (var game in Games) ObserveLibraryGame(game);
        Closed += (_, _) =>
        {
            _libraryRefreshTimer.Stop();
            Games.CollectionChanged -= LibraryCollectionChanged;
            foreach (var game in _libraryObserved) game.PropertyChanged -= LibraryGameChanged;
        };
        RefreshLibraryView();
    }

    private void LibraryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var game in _libraryObserved) game.PropertyChanged -= LibraryGameChanged;
            _libraryObserved.Clear();
            foreach (var game in Games) ObserveLibraryGame(game);
        }
        if (e.OldItems is not null)
            foreach (GameEntry game in e.OldItems) { game.PropertyChanged -= LibraryGameChanged; _libraryObserved.Remove(game); }
        if (e.NewItems is not null)
            foreach (GameEntry game in e.NewItems) ObserveLibraryGame(game);
        QueueLibraryRefresh();
    }

    private void ObserveLibraryGame(GameEntry game)
    {
        if (_libraryObserved.Add(game))
        {
            game.PropertyChanged += LibraryGameChanged;
            var coverPath = GameCoverPath(game);
            if (File.Exists(coverPath)) game.CoverArtPath = coverPath;
        }
    }

    private void LibraryGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameEntry.Eligible) or nameof(GameEntry.Installed) or nameof(GameEntry.Running) or nameof(GameEntry.CompatibilityLabel)) QueueLibraryRefresh();
    }

    private void QueueLibraryRefresh()
    {
        if (_libraryRefreshTimer is null || _closing) return;
        // Throttle instead of restarting: frequent runtime notifications must not starve a refresh.
        if (!_libraryRefreshTimer.IsEnabled) _libraryRefreshTimer.Start();
    }

    private bool IsLibraryGameVisible(object item)
    {
        if (item is not GameEntry game) return false;
        if (!ShowHiddenGames && _hiddenGamePaths.Contains(game.ExePath)) return false;
        var query = LibrarySearch.Trim();
        if (query.Length > 0 && !game.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            && !game.Store.Contains(query, StringComparison.OrdinalIgnoreCase)) return false;
        return LibraryFilter switch
        {
            "Ready" => game.Eligible,
            "Installed" => game.Installed,
            "Running" => game.Running,
            "Needs review" => !game.Eligible && !game.HasAntiCheat,
            "Blocked" => game.HasAntiCheat,
            _ => true
        };
    }

    private void RefreshLibraryView()
    {
        if (_libraryView is null) return;
        using (_libraryView.DeferRefresh())
        {
            _libraryView.SortDescriptions.Clear();
            _libraryView.GroupDescriptions.Clear();
            if (GroupGamesByStore)
            {
                _libraryView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GameEntry.Store)));
                _libraryView.SortDescriptions.Add(new SortDescription(nameof(GameEntry.Store), ListSortDirection.Ascending));
            }
            if (LibrarySort == "Installed first") _libraryView.SortDescriptions.Add(new SortDescription(nameof(GameEntry.Installed), ListSortDirection.Descending));
            _libraryView.SortDescriptions.Add(new SortDescription(nameof(GameEntry.Name), LibrarySort == "Name Z–A" ? ListSortDirection.Descending : ListSortDirection.Ascending));
        }
        // Filtering must not retain an invisible target or move selection to the next row.
        if (SelectedGame is not null && !IsLibraryGameVisible(SelectedGame)) SelectedGame = null;
        if (SelectedGame is null) GamesList.UnselectAll();
        var visible = Games.Count(game => IsLibraryGameVisible(game));
        LibraryCountText.Text = $"{visible} of {Games.Count} games · {_hiddenGamePaths.Count} hidden";
        LibraryEmptyText.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static GameEntry? LibraryMenuGame(object sender) => (sender as FrameworkElement)?.DataContext as GameEntry;

    private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryMenuGame(sender) is not { } game) return;
        try
        {
            if (!Directory.Exists(game.DirectoryPath)) throw new DirectoryNotFoundException("The game folder no longer exists. Scan again to refresh your library.");
            Process.Start(new ProcessStartInfo(game.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CopyGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryMenuGame(sender) is not { } game) return;
        try { Clipboard.SetText(game.DirectoryPath); ShowToast("Game folder copied."); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void GameContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not GameEntry game) return;
        if (menu.Items.OfType<MenuItem>().LastOrDefault() is { } hide) hide.Header = _hiddenGamePaths.Contains(game.ExePath) ? "Show in library" : "Hide from library";
    }

    private void HideGame_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryMenuGame(sender) is not { } game) return;
        if (!_hiddenGamePaths.Remove(game.ExePath)) _hiddenGamePaths.Add(game.ExePath);
        _settings.HiddenGames = _hiddenGamePaths.Order(StringComparer.OrdinalIgnoreCase).ToList();
        SaveSettings();
        RefreshLibraryView();
    }

    private static string GameCoverPath(GameEntry game)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(game.ExePath).ToUpperInvariant())));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5 AMD Swapper", "covers", key + ".png");
    }

    private void RecheckLibraryGame_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryMenuGame(sender) is not { } game || game.Busy) return;
        SelectedGame = game;
        ProbeSelected_Click(sender, e);
    }

    private async void ChangeGameCover_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryMenuGame(sender) is not { } game) return;
        var picker = new OpenFileDialog { Title = "Choose local cover art", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var destination = GameCoverPath(game);
            await Task.Run(() => CacheGameCover(picker.FileName, destination));
            game.CoverArtPath = destination;
            ShowToast("Cover saved locally.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static void CacheGameCover(string source, string destination)
    {
        using var input = File.OpenRead(source);
        if (input.Length > 12 * 1024 * 1024) throw new InvalidDataException("Choose an image smaller than 12 MB.");
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || frame.PixelWidth > 8192 || frame.PixelHeight > 8192 || frame.PixelHeight > frame.PixelWidth * 8)
            throw new InvalidDataException("Choose an image up to 8192 × 8192 pixels with a regular cover aspect ratio.");
        input.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Min(320, frame.PixelWidth);
        bitmap.StreamSource = input;
        bitmap.EndInit();
        bitmap.Freeze();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temporary))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ResetGameCover_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryMenuGame(sender) is not { } game) return;
        try
        {
            // Only delete the deterministic cache file; never touch the chosen source image.
            File.Delete(GameCoverPath(game));
            game.CoverArtPath = null;
            ShowToast("Cover reset.");
        }
        catch (Exception ex) { ShowError(ex); }
    }
}
