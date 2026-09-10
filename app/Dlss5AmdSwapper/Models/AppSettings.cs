namespace Dlss5AmdSwapper.Models;

public sealed class AppSettings
{
    public string UpstreamSetupPath { get; set; } = string.Empty;
    public string NvngxDlssNrPath { get; set; } = string.Empty;
    public string LosslessScalingPath { get; set; } = string.Empty;
    public string LosslessProxyPath { get; set; } = string.Empty;
    public string LosslessNrPath { get; set; } = string.Empty;
    public string HipVisibleDevices { get; set; } = "0";
    public List<string> ManualGames { get; set; } = [];
    public List<string> HiddenGames { get; set; } = [];
    public bool ShowHiddenGames { get; set; }
    public bool GroupGamesByStore { get; set; }
    public string LibrarySort { get; set; } = "Name A–Z";
    public bool MinimizeToTray { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public bool ScanAllDrives { get; set; }
    public List<string> AdditionalScanFolders { get; set; } = [];
    public bool RegisterHotkeys { get; set; } = true;
    public string LastPage { get; set; } = "Home";
}
