using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Dlss5AmdSwapper.Models;

public sealed class GameEntry : INotifyPropertyChanged
{
    private bool _installed;
    private bool _enabled;
    private bool _running;
    private bool _busy;
    private string _status = "Checking";
    private string _runtimeStatus = "Not checked";
    private double _localStructure = 1.0;
    private double _localTone = 1.0;
    private double _skinStructure = 1.0;
    private bool _liveAcknowledged;
    private InstallRoute _route;
    private int _passes = 1;
    private string? _coverArtPath;
    private BitmapImage? _coverImage;
    private bool _coverLoaded;

    public string? CoverArtPath
    {
        get => _coverArtPath;
        set
        {
            _coverArtPath = value;
            _coverImage = null;
            _coverLoaded = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CoverImage));
        }
    }

    // Decode only when WPF realizes a visible game row. Never retain a source-file handle.
    public BitmapImage? CoverImage
    {
        get
        {
            if (_coverLoaded) return _coverImage;
            _coverLoaded = true;
            try
            {
                if (_coverArtPath is null || !File.Exists(_coverArtPath) || new FileInfo(_coverArtPath).Length > 12 * 1024 * 1024) return null;
                using var stream = File.OpenRead(_coverArtPath);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 96;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                _coverImage = bitmap;
            }
            catch { /* Missing/corrupt local artwork must not interrupt the library. */ }
            return _coverImage;
        }
    }

    public string Name { get; init; } = string.Empty;
    public string ExePath { get; init; } = string.Empty;
    public string Store { get; init; } = "Manual";
    public bool X64 { get; set; }
    public bool HasFsr { get; set; }
    public bool HasDx12 { get; set; }
    public bool HasAntiCheat { get; set; }
    public IReadOnlyList<string> FsrMarkers { get; set; } = [];
    public IReadOnlyList<string> AntiCheatMarkers { get; set; } = [];
    public string DirectoryPath => Path.GetDirectoryName(ExePath) ?? string.Empty;
    public string ConfigPath => Path.Combine(DirectoryPath, "dlssnr_on_amd.ini");
    public string LogPath => Path.Combine(DirectoryPath, "dlssnr_on_amd.log");
    public string ManifestPath => Path.Combine(DirectoryPath, ".dlss5-amd-swapper.json");
    public string LegacyManifestPath => Path.Combine(DirectoryPath, ".nr-auto-scale-direct.json");
    public string OptiScalerIniPath => Path.Combine(DirectoryPath, "OptiScaler.ini");
    public string PreSrLogPath => Path.Combine(DirectoryPath, "amd_presr.log");
    public string OptiScalerLogPath => Path.Combine(DirectoryPath, "OptiScaler.log");
    public bool Eligible => X64 && HasFsr && HasDx12 && !HasAntiCheat;

    public bool Installed { get => _installed; set => Set(ref _installed, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool Running { get => _running; set => Set(ref _running, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string RuntimeStatus { get => _runtimeStatus; set => Set(ref _runtimeStatus, value); }
    public double LocalStructure { get => _localStructure; set => Set(ref _localStructure, value); }
    public double LocalTone { get => _localTone; set => Set(ref _localTone, value); }
    public double SkinStructure { get => _skinStructure; set => Set(ref _skinStructure, value); }
    public bool LiveAcknowledged { get => _liveAcknowledged; set => Set(ref _liveAcknowledged, value); }
    public InstallRoute Route { get => _route; set { if (Set(ref _route, value)) { OnPropertyChanged(nameof(RouteLabel)); OnPropertyChanged(nameof(IsPreSr)); OnPropertyChanged(nameof(IsPostFsr)); } } }
    public int Passes { get => _passes; set => Set(ref _passes, value); }
    public string RouteLabel => InstallRoutes.Label(Route);
    public bool IsPreSr => Route == InstallRoute.OptiScalerPreSr;
    public bool IsPostFsr => Route == InstallRoute.PostFsrRuntime;

    public string CompatibilityLabel => HasAntiCheat ? "Blocked: anti-cheat" : Eligible ? "Direct-game ready" : "Needs review";
    public string InstallLabel => Installed ? "Installed" : "Not installed";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyComputed()
    {
        OnPropertyChanged(nameof(Eligible));
        OnPropertyChanged(nameof(CompatibilityLabel));
        OnPropertyChanged(nameof(InstallLabel));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        if (name == nameof(Installed)) OnPropertyChanged(nameof(InstallLabel));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
