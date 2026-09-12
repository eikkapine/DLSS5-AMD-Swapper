using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

namespace Dlss5AmdSwapper;

public partial class MainWindow
{
    private readonly OptiScalerPackageService _optiPackages = new();
    private readonly OptiScalerControlService _optiControl = new();
    private readonly OptiScalerDiagnosticsService _optiDiagnostics = new();
    private OptiScalerInstallerService? _optiInstaller;
    private OptiScalerPackage? _optiPackage;
    private LocalWeights? _localWeights;
    private string _optiPackageStatus = "OptiScaler package: not checked";
    private string _losslessLayerStatus = "Bridge runtime config not found";
    private string _losslessLayerTarget = "LocalStructure";
    private LayerState _losslessLayers = new(1, 1, 0, true);

    private OptiScalerInstallerService OptiInstaller => _optiInstaller ??= new OptiScalerInstallerService(_probe, _installer);

    public string OptiScalerPackagePath { get => _settings.OptiScalerPackagePath; set { _settings.OptiScalerPackagePath = value; OnPropertyChanged(); SaveSettings(); } }
    public string LocalWeightsPath { get => _settings.LocalWeightsPath; set { _settings.LocalWeightsPath = value; OnPropertyChanged(); SaveSettings(); } }
    public string[] PresetNames { get; } = ["Quality", "Performance"];
    public string OptiScalerDefaultPreset { get => _settings.OptiScalerDefaultPreset; set { if (value is null) return; _settings.OptiScalerDefaultPreset = value; OnPropertyChanged(); SaveSettings(); } }
    public string OptiScalerPackageStatus { get => _optiPackageStatus; private set => Set(ref _optiPackageStatus, value); }
    public int[] PassOptions { get; } = [1, 2, 3];

    public double LosslessStructure => _losslessLayers.Structure;
    public double LosslessSkin => _losslessLayers.Skin;
    public double LosslessTone => _losslessLayers.Tone;
    public bool LosslessSkinFollows => _losslessLayers.SkinFollowsStructure;
    public string LosslessLayerStatus { get => _losslessLayerStatus; private set => Set(ref _losslessLayerStatus, value); }
    private string? LosslessRuntimeIni => string.IsNullOrWhiteSpace(LosslessInstallPath) ? null : Path.Combine(LosslessInstallPath, "nr-bridge", "runtime", "dlssnr_on_amd.ini");
    private OptiScalerPreset DefaultPreset => OptiScalerDefaultPreset == "Performance" ? OptiScalerPreset.Performance : OptiScalerPreset.Quality;

    private async Task<bool> ResolveOptiScalerSourcesAsync(bool showToast)
    {
        _optiPackage = null;
        _localWeights = null;
        try
        {
            var candidates = await Task.Run(() => _optiPackages.DiscoverCandidates(OptiScalerPackagePath));
            string? failure = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    var root = candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? await Task.Run(() => _optiPackages.EnsureExtracted(candidate)) : candidate;
                    _optiPackage = await Task.Run(() => OptiScalerPackageService.Validate(root));
                    if (!string.Equals(OptiScalerPackagePath, candidate, StringComparison.OrdinalIgnoreCase)) OptiScalerPackagePath = candidate;
                    break;
                }
                catch (InvalidOperationException error) { failure ??= $"{Path.GetFileName(candidate)}: {error.Message}"; }
            }
            var gameDirectories = Games.Select(game => game.DirectoryPath).ToArray();
            _localWeights = await Task.Run(() => OptiScalerPackageService.FindLocalWeights(LocalWeightsPath, gameDirectories, LosslessInstallPath));
            if (_localWeights is not null && !string.Equals(LocalWeightsPath, _localWeights.Path, StringComparison.OrdinalIgnoreCase)) LocalWeightsPath = _localWeights.Path;
            OptiScalerPackageStatus = _optiPackage is null
                ? "OptiScaler package: " + (failure ?? "none found. Put the OptiScaler-AMD-PreSR-Multipass folder or zip in Downloads, or choose it below.")
                : $"OptiScaler package: {_optiPackage.Summary}" + (_localWeights is null ? " · weights: none found (run the post-FSR route once so the runtime generates them)" : " · weights ready");
            if (showToast) ShowToast(OptiScalerPackageStatus, _optiPackage is not null && _localWeights is not null);
            return _optiPackage is not null && _localWeights is not null;
        }
        catch (Exception ex)
        {
            OptiScalerPackageStatus = "OptiScaler package: " + ex.Message;
            if (showToast) ShowError(ex);
            return false;
        }
    }

    private async void RefreshOptiScalerSources_Click(object sender, RoutedEventArgs e) => await ResolveOptiScalerSourcesAsync(true);

    private void BrowseOptiScalerPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the OptiScaler AMD pre-SR package folder" };
        if (dialog.ShowDialog(this) == true) OptiScalerPackagePath = dialog.FolderName;
    }

    private void BrowseLocalWeights_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Select a generated dlssnr_on_amd_weights.bin", "dlssnr_on_amd_weights.bin|dlssnr_on_amd_weights.bin|All files (*.*)|*.*");
        if (path is not null) LocalWeightsPath = path;
    }

    // Called by InstallSelected_Click when the selected game has no managed route yet.
    private async Task SetUpNewRouteAsync(GameEntry target)
    {
        var preSrReady = await ResolveOptiScalerSourcesAsync(false);
        var preSrBlock = preSrReady && _optiPackage is not null ? OptiScalerInstallerService.GetCompatibilityBlock(target, _optiPackage) : null;
        if (preSrBlock is not null) preSrReady = false;
        var dialog = new SetupDialog(target.Name,
            preSrBlock ?? _optiPackage?.Summary ?? OptiScalerPackageStatus,
            _localWeights is null ? "Weights: none found" : $"Weights: {Path.GetFileName(Path.GetDirectoryName(_localWeights.Path))} copy, {_localWeights.Size / (1024 * 1024)} MB",
            preSrReady, DefaultPreset) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.Route == InstallRoute.PostFsrRuntime)
        {
            await InstallPostFsrAsync(target, update: false);
            return;
        }
        ShowToast("Installing OptiScaler pre-SR…");
        var result = await OptiInstaller.InstallAsync(target, _optiPackage!, _localWeights!, dialog.Preset, update: false);
        _runtime.Refresh(target);
        if (ReferenceEquals(SelectedGame, target)) await RefreshDiagnosticsAsync(false);
        RecordActivity("Game installed (pre-SR)", $"{target.Name} · {result.Preset} · {result.Written.Count} files");
        ShowToast($"Installed pre-SR for {target.Name} · {result.Preset} preset · launch the game with FSR enabled", true);
    }

    private async Task UpdatePreSrAsync(GameEntry target)
    {
        var manifest = await OptiScalerInstallerService.ReadManifestAsync(target.ManifestPath, CancellationToken.None);
        if (OptiScalerInstallerService.GetCompatibilityBlock(target, manifest) is { } compatibilityBlock)
        {
            ShowToast("Migrating incompatible pre-SR route to the official AMD runtime…");
            var removed = await OptiInstaller.RemoveForPostFsrMigrationAsync(target);
            await InstallPostFsrAsync(target, update: false);
            RecordActivity("Game migrated to post-FSR", $"{target.Name} · {removed.Preserved.Count} preserved text file(s) · {compatibilityBlock}");
            return;
        }

        if (!await ResolveOptiScalerSourcesAsync(false)) throw new InvalidOperationException(OptiScalerPackageStatus);
        var proxyName = manifest?.ProxyName is { Length: > 0 } name ? name : "dxgi.dll";
        var preset = string.Equals(manifest?.Preset, "performance", StringComparison.OrdinalIgnoreCase) ? OptiScalerPreset.Performance : OptiScalerPreset.Quality;
        ShowToast("Updating OptiScaler pre-SR…");
        var result = await OptiInstaller.InstallAsync(target, _optiPackage!, _localWeights!, preset, update: true, proxyName);
        _runtime.Refresh(target);
        RecordActivity("Game updated (pre-SR)", $"{target.Name} · {result.Preset}");
        ShowToast($"Updated pre-SR for {target.Name}", true);
    }

    private async void PassesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedGame is not { IsPreSr: true } game || sender is not ComboBox combo || combo.SelectedItem is not int passes || passes == game.Passes) return;
        try { ShowToast((await _optiControl.SetPassesAsync(game, passes)).Message); }
        catch (Exception ex)
        {
            ShowError(ex);
            combo.SelectedItem = game.Passes;
        }
    }

    private async Task RefreshPreSrDiagnosticsAsync(GameEntry target, bool toast)
    {
        var diag = await _optiDiagnostics.InspectAsync(target);
        if (!ReferenceEquals(SelectedGame, target) || _closing) return;
        DiagnosticsSummary = diag.Summary;
        var parts = new List<string> { "Evidence: amd_presr.log + OptiScaler.log tail (1 MiB), whole files hashed" };
        if (diag.HipAdapter is not null) parts.Add("HIP " + diag.HipAdapter);
        if (diag.MeanModelMs is not null) parts.Add($"model {diag.MeanModelMs:0.0} ms mean ({diag.CostSamples} samples)");
        if (diag.PassesInitialized > 0) parts.Add($"{diag.PassesInitialized} pass runtime(s) initialised");
        DiagnosticsDetail = string.Join(" · ", parts);
        if (diag.PreSrActive) target.RuntimeStatus = "Pre-SR observed";
        if (toast) ShowToast(diag.Summary, diag.PreSrActive);
    }

    private void RefreshLosslessLayers()
    {
        var ini = LosslessRuntimeIni;
        if (ini is null || !File.Exists(ini)) { LosslessLayerStatus = "Bridge runtime config not found. Install the bridge first."; return; }
        try
        {
            _losslessLayers = _runtime.ReadLayers(ini);
            OnPropertyChanged(nameof(LosslessStructure)); OnPropertyChanged(nameof(LosslessSkin)); OnPropertyChanged(nameof(LosslessTone)); OnPropertyChanged(nameof(LosslessSkinFollows));
            LosslessLayerStatus = $"Layers apply while the bridge runs; the runtime reloads {Path.GetFileName(ini)}. Hotkeys Ctrl+Alt+F9 (cycle) / F10 (−) / F11 (+) target: {_losslessLayerTarget}";
        }
        catch (Exception ex) { LosslessLayerStatus = "Could not read layers: " + ex.Message; }
    }

    private async Task ApplyLosslessLayerAsync(string key, double value)
    {
        var ini = LosslessRuntimeIni;
        if (ini is null) return;
        try { ShowToast((await _runtime.SetLayerAsync(ini, key, value)).Message); }
        catch (Exception ex) { ShowError(ex); }
        RefreshLosslessLayers();
    }

    private async void LosslessStructureSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyLosslessLayerAsync("LocalStructure", LosslessStructureSlider.Value);
    private async void LosslessSkinSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyLosslessLayerAsync("SkinStructure", LosslessSkinSlider.Value);
    private async void LosslessToneSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyLosslessLayerAsync("LocalTone", LosslessToneSlider.Value);
    private async void LosslessSkinFollows_Click(object sender, RoutedEventArgs e) => await ApplyLosslessLayerAsync("SkinStructure", LosslessSkinFollowsBox.IsChecked == true ? -1 : LosslessSkinSlider.Value);

    private async Task HandleLayerHotkeyAsync(SwapperHotkey hotkey)
    {
        var ini = LosslessRuntimeIni;
        if (ini is null || !File.Exists(ini)) return;
        if (hotkey == SwapperHotkey.CycleLayer)
        {
            _losslessLayerTarget = _losslessLayerTarget switch { "LocalStructure" => "SkinStructure", "SkinStructure" => "LocalTone", _ => "LocalStructure" };
            RefreshLosslessLayers();
            ShowToast("Layer hotkeys now adjust " + _losslessLayerTarget);
            return;
        }
        var result = await _runtime.AdjustLayerAsync(ini, _losslessLayerTarget, hotkey == SwapperHotkey.LayerIncrease ? 0.1 : -0.1);
        RefreshLosslessLayers();
        ShowToast($"{_losslessLayerTarget}: {result.Message}");
    }
}
