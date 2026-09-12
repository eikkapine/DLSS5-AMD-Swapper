using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;
using Microsoft.Win32;

namespace Dlss5AmdSwapper;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppSettingsService _settingsService = new();
    private readonly GameProbeService _probe = new();
    private readonly RuntimeControlService _runtime = new();
    private readonly RuntimeDiagnosticsService _diagnostics = new();
    private readonly LosslessScalingService _lossless = new();
    private readonly RuntimeSourceService _runtimeSources = new();
    private readonly GameDiscoveryService _discovery;
    private readonly DirectGameInstallerService _installer;
    private readonly DispatcherTimer _runtimeTimer;
    private readonly DispatcherTimer _toastTimer;
    private AppSettings _settings;
    private HotkeyService? _hotkeys;
    private GameEntry? _selectedGame;
    private string _pageTitle = "Overview";
    private string _hotkeyStatus = "Waiting for a managed game";
    private string _diagnosticsSummary = "Run a game and refresh evidence to verify the rich path.";
    private string _diagnosticsDetail = string.Empty;
    private string _losslessStatusText = "Checking Lossless Scaling";
    private string _losslessInstallPath = string.Empty;
    private string _runtimeSourcesStatus = "Preparing automatic sources";
    private string _runtimeSetupStatus = "Official setup: checking latest release";
    private string _runtimeNrStatus = "NVIDIA runtime: searching local copies";
    private bool _closing;
    private bool _installOperation;
    private int _diagnosticsRefreshInFlight;
    private bool _diagnosticsRefreshPending;
    private long _pollCount;
    private bool _losslessDeviceInitialized;
    private HashSet<string> _runningProcessNames = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        _discovery = new GameDiscoveryService(_probe);
        _installer = new DirectGameInstallerService(_probe);
        _settings = _settingsService.Load();
        InitializeComponent();
        DataContext = this;
        InitializeLibrary();
        InitializeDesktop();

        _runtimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runtimeTimer.Tick += RuntimeTimer_Tick;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    public ObservableCollection<GameEntry> Games { get; } = [];

    public GameEntry? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (ReferenceEquals(_selectedGame, value)) return;
            _selectedGame = value;
            OnPropertyChanged();
            if (value is not null)
            {
                _runtime.Refresh(value);
                _ = RefreshDiagnosticsAsync(false);
            }
            UpdateHotkeyRegistration();
        }
    }

    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }
    public string HotkeyStatus { get => _hotkeyStatus; private set => Set(ref _hotkeyStatus, value); }
    public string DiagnosticsSummary { get => _diagnosticsSummary; private set => Set(ref _diagnosticsSummary, value); }
    public string DiagnosticsDetail { get => _diagnosticsDetail; private set => Set(ref _diagnosticsDetail, value); }
    public string LosslessStatusText { get => _losslessStatusText; private set => Set(ref _losslessStatusText, value); }
    public string LosslessInstallPath { get => _losslessInstallPath; private set => Set(ref _losslessInstallPath, value); }
    public string RuntimeBadgeText => RuntimeSourcesStatus.StartsWith("Ready for", StringComparison.Ordinal) ? "Runtime files ready" : "Runtime setup";
    public string RuntimeSourcesStatus { get => _runtimeSourcesStatus; private set { Set(ref _runtimeSourcesStatus, value); OnPropertyChanged(nameof(RuntimeBadgeText)); } }
    public string RuntimeSetupStatus { get => _runtimeSetupStatus; private set => Set(ref _runtimeSetupStatus, value); }
    public string RuntimeNrStatus { get => _runtimeNrStatus; private set => Set(ref _runtimeNrStatus, value); }

    public string UpstreamSetupPath { get => _settings.UpstreamSetupPath; set { _settings.UpstreamSetupPath = value; OnPropertyChanged(); SaveSettings(); } }
    public string NvngxDlssNrPath { get => _settings.NvngxDlssNrPath; set { _settings.NvngxDlssNrPath = value; OnPropertyChanged(); SaveSettings(); } }
    public string LosslessProxyPath { get => _settings.LosslessProxyPath; set { _settings.LosslessProxyPath = value; OnPropertyChanged(); SaveSettings(); } }
    public string LosslessNrPath { get => _settings.LosslessNrPath; set { _settings.LosslessNrPath = value; OnPropertyChanged(); SaveSettings(); } }
    public string HipVisibleDevices { get => _settings.HipVisibleDevices; set { _settings.HipVisibleDevices = value; OnPropertyChanged(); SaveSettings(); } }
    public bool RegisterHotkeys { get => _settings.RegisterHotkeys; set { _settings.RegisterHotkeys = value; OnPropertyChanged(); SaveSettings(); UpdateHotkeyRegistration(); } }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshLosslessStatus();
        _ = ResolveOptiScalerSourcesAsync(false);
        RefreshLosslessLayers();
        Navigate(_settings.LastPage);
        // Process/hotkey polling must not wait for a potentially long library scan.
        // As managed games are discovered, the timer can make a running target live immediately.
        RefreshProcessSnapshot();
        _runtimeTimer.Start();
        UpdateHotkeyRegistration();
        IsScanning = true;
        _scanCancellation = new CancellationTokenSource();
        try { await LoadGamesAsync(_scanCancellation.Token); }
        catch (OperationCanceledException) { ShowToast("Game scan cancelled."); }
        finally { _scanCancellation.Dispose(); _scanCancellation = null; IsScanning = false; }
        if (_closing) return;
        await RefreshRuntimeSourcesAsync(false);
        Navigate(_settings.LastPage);
        RefreshProcessSnapshot();
        UpdateHotkeyRegistration();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_installOperation)
        {
            e.Cancel = true;
            ShowToast("Wait for the installation or restore to finish before exiting.");
            return;
        }
        if (!_exitRequested && MinimizeToTray && _tray is not null)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _closing = true;
        _scanCancellation?.Cancel();
        DisposeDesktop();
        _runtimeTimer.Stop();
        _hotkeys?.Dispose();
        _hotkeys = null;
        SaveSettings();
    }

    private async Task LoadGamesAsync(CancellationToken cancellationToken)
    {
        foreach (var path in _settings.ManualGames.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
            await AddGameInternalAsync(Path.GetFileNameWithoutExtension(path), path, "Manual", false, cancellationToken);

        try
        {
            var discovered = await _discovery.DiscoverAsync(includeHeuristics: false, cancellationToken: cancellationToken);
            foreach (var game in discovered)
                await AddGameInternalAsync(game.Name, game.ExePath, game.Store, false, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ShowToast("Game scan skipped: " + ex.Message);
        }
        SelectedGame = null;
        GamesList.UnselectAll();
    }

    private async Task AddGameInternalAsync(string name, string exePath, string store, bool persist, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(exePath);
        var existing = Games.FirstOrDefault(g => g.ExePath.Equals(full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return;
        var game = new GameEntry { Name = name, ExePath = full, Store = store };
        Games.Add(game);
        try { await _probe.ProbeAsync(game, cancellationToken); }
        catch (OperationCanceledException) { Games.Remove(game); throw; }
        catch (Exception ex) { game.Status = ex.Message; }
        _runtime.Refresh(game);
        if (persist && !_settings.ManualGames.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            _settings.ManualGames.Add(full);
            SaveSettings();
        }
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string page) Navigate(page);
    }

    private void Navigate(string page)
    {
        HomePage.Visibility = Visibility.Collapsed;
        GamesPage.Visibility = Visibility.Collapsed;
        LosslessPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        switch (page)
        {
            case "Games": GamesPage.Visibility = Visibility.Visible; PageTitle = "Game library"; break;
            case "Lossless": LosslessPage.Visibility = Visibility.Visible; PageTitle = "Lossless Scaling"; RefreshLosslessStatus(); RefreshLosslessLayers(); break;
            case "Settings": SettingsPage.Visibility = Visibility.Visible; PageTitle = "Settings"; break;
            default: page = "Home"; HomePage.Visibility = Visibility.Visible; PageTitle = "Overview"; break;
        }
        _settings.LastPage = page;
        SaveSettings();
    }

    private async void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select the game's main executable", Filter = "Windows games (*.exe)|*.exe|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await AddGameInternalAsync(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, "Manual", true);
        Navigate("Games");
    }

    private async void ScanGames_Click(object sender, RoutedEventArgs e)
    {
        if (IsScanning) return;
        IsScanning = true;
        _scanCancellation = new CancellationTokenSource();
        try
        {
            SelectedGame = null;
            GamesList.UnselectAll();
            ShowToast("Scanning installed games…");
            var progress = new Progress<string>(message => HotkeyStatus = message);
            var discovered = await _discovery.DiscoverAsync(includeHeuristics: true, progress: progress, cancellationToken: _scanCancellation.Token, additionalFolders: _settings.AdditionalScanFolders.ToArray(), includeAllDrives: ScanAllDrives);
            foreach (var game in discovered)
            {
                _scanCancellation.Token.ThrowIfCancellationRequested();
                await AddGameInternalAsync(game.Name, game.ExePath, game.Store, false, _scanCancellation.Token);
            }
            SelectedGame = null;
            GamesList.UnselectAll();
            if (_closing) return;
            var ready = Games.Count(game => game.Eligible);
            var blocked = Games.Count(game => game.HasAntiCheat);
            var review = Games.Count - ready - blocked;
            var sources = Games
                .GroupBy(game => game.Store)
                .OrderBy(group => group.Key)
                .ToDictionary(group => group.Key, group => group.Count());
            HotkeyStatus = "PC game scan complete";
            RecordActivity("Scan completed", $"{Games.Count} targets checked; {ready} compatible; {blocked} blocked.");
            ShowToast($"Verified {Games.Count} installed game(s) · {ready} direct-game ready.", true);
            ScanTotalText.Text = Games.Count.ToString();
            ScanReadyText.Text = ready.ToString();
            ScanReviewText.Text = review.ToString();
            ScanBlockedText.Text = blocked.ToString();
            ScanSourcesText.Text = sources.Count == 0
                ? "No launcher sources reported games."
                : string.Join("   •   ", sources
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key} {pair.Value}"));
            MainContent.IsEnabled = false;
            ScanSummaryOverlay.Visibility = Visibility.Visible;
            ScanSummaryDoneButton.Focus();
            SelectedGame = null;
            GamesList.UnselectAll();
        }
        catch (OperationCanceledException) { ShowToast("Scan cancelled. Completed entries were kept."); }
        catch (Exception ex) { ShowError(ex); }
        finally { _scanCancellation?.Dispose(); _scanCancellation = null; IsScanning = false; }
    }

    private void ScanSummaryDone_Click(object sender, RoutedEventArgs e)
    {
        ScanSummaryOverlay.Visibility = Visibility.Collapsed;
        MainContent.IsEnabled = true;
        SelectedGame = null;
        GamesList.UnselectAll();
    }

    private void ScanSummary_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ScanSummaryDone_Click(sender, e); e.Handled = true; }
    }

    private async void ProbeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is null) return;
        try
        {
            await _probe.ProbeAsync(SelectedGame);
            _runtime.Refresh(SelectedGame);
            ShowToast(SelectedGame.Status, true);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void InstallSelected_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedGame;
        if (target is null) { ShowToast("Select a game first."); return; }
        if (_installOperation) { ShowToast("Another installation or restore is in progress."); return; }
        _installOperation = true;
        try
        {
            switch (target.Route)
            {
                case InstallRoute.OptiScalerPreSr: await UpdatePreSrAsync(target); break;
                case InstallRoute.PostFsrRuntime: await InstallPostFsrAsync(target, update: true); break;
                default: await SetUpNewRouteAsync(target); break;
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _installOperation = false; }
    }

    private async Task InstallPostFsrAsync(GameEntry target, bool update)
    {
        ShowToast("Preparing verified runtime sources…");
        var sources = await ResolveRuntimeSourcesAsync(promptForNr: true, target);
        ShowToast(update ? "Updating AMD Neural Rendering…" : "Installing AMD Neural Rendering…");
        var result = await _installer.InstallAsync(target, sources.SetupPath, sources.NrDllPath!, update);
        _runtime.Refresh(target);
        if (ReferenceEquals(SelectedGame, target)) await RefreshDiagnosticsAsync(false);
        RecordActivity(update ? "Game updated" : "Game installed", target.Name);
        ShowToast($"{(update ? "Updated" : "Installed")} {target.Name} · upstream {result.UpstreamTag} · rich config verified", true);
    }

    private async void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedGame;
        if (target is null) return;
        if (_installOperation) { ShowToast("Another installation or restore is in progress."); return; }
        _installOperation = true;
        try
        {
            var result = target.Route == InstallRoute.OptiScalerPreSr
                ? await OptiInstaller.RemoveAsync(target)
                : await _installer.RemoveAsync(target, removeModel: true);
            _runtime.Refresh(target);
            RecordActivity("Game restored", target.Name);
            ShowToast(result.ManifestRetained
                ? "Restore preserved files that changed after installation. The manifest was kept for safety."
                : $"Restored {target.Name}. Removed {result.Removed.Count} managed file(s).", true);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _installOperation = false; }
    }

    private async void DirectEnabledSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is null || SelectedGame.Route == InstallRoute.None) return;
        try
        {
            var desired = DirectEnabledSwitch.IsChecked == true;
            var target = SelectedGame;
            var result = target.IsPreSr ? await _optiControl.SetEnabledAsync(target, desired) : await _runtime.SetEnabledAsync(target, desired);
            RecordActivity("Toggle requested", $"{target.Name}: {(desired ? "on" : "off")} — {result.Message}");
            ShowToast(result.Message, result.LiveAcknowledged);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void StructureSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyScalarAsync(() => SelectedGame!.IsPreSr ? _optiControl.SetStructureAsync(SelectedGame!, StructureSlider.Value) : _runtime.SetStructureAsync(SelectedGame!, StructureSlider.Value));
    private async void ToneSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyScalarAsync(() => SelectedGame!.IsPreSr ? _optiControl.SetToneAsync(SelectedGame!, ToneSlider.Value) : _runtime.SetToneAsync(SelectedGame!, ToneSlider.Value));
    private async void SkinSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyScalarAsync(() => SelectedGame!.IsPreSr ? _optiControl.SetSkinAsync(SelectedGame!, SkinSlider.Value) : _runtime.SetSkinStructureAsync(SelectedGame!, SkinSlider.Value));

    private async Task ApplyScalarAsync(Func<Task<RuntimeChangeResult>> change)
    {
        if (SelectedGame is null || SelectedGame.Route == InstallRoute.None) return;
        try
        {
            var result = await change();
            ShowToast(result.Message, result.LiveAcknowledged);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void RefreshDiagnostics_Click(object sender, RoutedEventArgs e) => await RefreshDiagnosticsAsync(true);

    private async Task RefreshDiagnosticsAsync(bool toast)
    {
        if (Interlocked.CompareExchange(ref _diagnosticsRefreshInFlight, 1, 0) != 0)
        {
            _diagnosticsRefreshPending = true;
            if (toast) ShowToast("Evidence refresh already running.");
            return;
        }
        try
        {
            if (SelectedGame is null)
            {
                DiagnosticsSummary = "Select a game to inspect runtime evidence.";
                DiagnosticsDetail = string.Empty;
                return;
            }
            if (SelectedGame.IsPreSr)
            {
                try { await RefreshPreSrDiagnosticsAsync(SelectedGame, toast); }
                catch (Exception ex) { if (toast) ShowError(ex); }
                return;
            }
            try
            {
                var target = SelectedGame;
                var diag = await _diagnostics.InspectAsync(target);
                if (!ReferenceEquals(SelectedGame, target) || _closing) return;
                DiagnosticsSummary = diag.Summary;
                var parts = new List<string> { diag.EvidenceScope };
                if (diag.InputResolution is not null) parts.Add($"FSR input {diag.InputResolution}");
                if (diag.OutputResolution is not null) parts.Add($"output {diag.OutputResolution}");
                if (diag.MeanNetworkGpuMs is not null) parts.Add($"network {diag.MeanNetworkGpuMs:0.00} ms GPU mean ({diag.TimedJobs} samples)");
                if (diag.Interop is not null) parts.Add(diag.Interop);
                if (diag.FaultLines + diag.GpuErrorLines > 0) parts.Add($"{diag.FaultLines} fault / {diag.GpuErrorLines} GPU-error lines");
                if (diag.FullOutputResolutionInput) parts.Add("FSR input currently equals output resolution; an in-game FSR quality mode can reduce the hidden neural workload");
                DiagnosticsDetail = string.Join(" · ", parts);
                if (target.Running)
                {
                    if (diag.RichPathObserved) target.RuntimeStatus = "Neural Rendering active - log verified";
                    else if (diag.HooksFailed) target.RuntimeStatus = "Neural engine stalled - render hooks failed this launch";
                    else if (diag.StartupStalled) target.RuntimeStatus = "Neural engine stalled - effect inactive this launch";
                    else if (diag.FidelityFxDispatchObserved) target.RuntimeStatus = "FidelityFX hooked - waiting for neural jobs";
                    else if (diag.EngineInitialized) target.RuntimeStatus = "Neural engine initialized - waiting for FidelityFX";
                    else if (diag.SessionScoped) target.RuntimeStatus = "Runtime loaded - Neural Rendering not active yet";
                }
                if (toast) ShowToast(diag.Summary, diag.RichPathObserved);
            }
            catch (Exception ex) { if (toast) ShowError(ex); }
        }
        finally
        {
            Interlocked.Exchange(ref _diagnosticsRefreshInFlight, 0);
            if (_diagnosticsRefreshPending) { _diagnosticsRefreshPending = false; _ = RefreshDiagnosticsAsync(false); }
        }
    }

    private void RuntimeTimer_Tick(object? sender, EventArgs e)
    {
        RefreshProcessSnapshot();
        _pollCount++;
        foreach (var game in Games)
        {
            var processName = Path.GetFileNameWithoutExtension(game.ExePath);
            var running = _runningProcessNames.Contains(processName) && RuntimeControlService.IsRunning(game.ExePath);
            var wasRunning = game.Running;
            if (running || game.Running != running || ReferenceEquals(game, SelectedGame) || _pollCount % 15 == 0)
                _runtime.Refresh(game, running);
            if (ReferenceEquals(game, SelectedGame) && running && (!wasRunning || _pollCount % 5 == 0))
                _ = RefreshDiagnosticsAsync(false);
        }
        UpdateHotkeyRegistration();
    }

    private void RefreshProcessSnapshot()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try { names.Add(process.ProcessName); }
            catch { }
            finally { process.Dispose(); }
        }
        _runningProcessNames = names;
    }

    private void UpdateHotkeyRegistration()
    {
        if (_closing) return;
        var bridgeRunning = _runningProcessNames.Contains("DlssNrBridge");
        var losslessRunning = _runningProcessNames.Contains("LosslessScaling");
        var target = Games.FirstOrDefault(game => game.Installed && game.Running) ?? (SelectedGame?.Installed == true && SelectedGame.Running ? SelectedGame : null);
        HotkeySet? wanted = null;
        if (RegisterHotkeys && !bridgeRunning && target is not null) wanted = HotkeySet.DirectGame;
        else if (RegisterHotkeys && losslessRunning && LosslessRuntimeIni is { } ini && File.Exists(ini)) wanted = HotkeySet.LosslessLayers;

        if (wanted is null || (_hotkeys is not null && _hotkeys.Set != wanted))
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
        }
        if (wanted is null)
        {
            HotkeyStatus = bridgeRunning ? "Lossless Scaling bridge owns F6/F7/F8" : RegisterHotkeys ? "Waiting for a managed game or Lossless Scaling" : "Hotkeys disabled";
            return;
        }
        var label = wanted == HotkeySet.DirectGame ? $"Ctrl+Alt+F6/F7/F8 → {target!.Name}" : $"Ctrl+Alt+F9/F10/F11 → Lossless Scaling layers ({_losslessLayerTarget})";
        if (_hotkeys is not null) { HotkeyStatus = label; return; }
        try
        {
            _hotkeys = new HotkeyService(HandleHotkey);
            _hotkeys.Attach(new WindowInteropHelper(this).Handle, wanted.Value);
            HotkeyStatus = label;
        }
        catch (Exception ex)
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            HotkeyStatus = "Hotkeys unavailable: " + ex.Message;
        }
    }

    private async void HandleHotkey(SwapperHotkey hotkey)
    {
        if (hotkey is SwapperHotkey.CycleLayer or SwapperHotkey.LayerDecrease or SwapperHotkey.LayerIncrease) { try { await HandleLayerHotkeyAsync(hotkey); } catch (Exception ex) { ShowError(ex); } return; }
        var target = Games.FirstOrDefault(game => game.Installed && game.Running) ?? SelectedGame;
        if (target?.Installed != true) return;
        try
        {
            RuntimeChangeResult result = hotkey switch
            {
                SwapperHotkey.Toggle => target.IsPreSr ? await _optiControl.ToggleEnabledAsync(target) : await _runtime.ToggleEnabledAsync(target),
                SwapperHotkey.Decrease => target.IsPreSr ? await _optiControl.SetStructureAsync(target, target.LocalStructure - 0.1) : await _runtime.AdjustStructureAsync(target, -0.1),
                SwapperHotkey.Increase => target.IsPreSr ? await _optiControl.SetStructureAsync(target, target.LocalStructure + 0.1) : await _runtime.AdjustStructureAsync(target, 0.1),
                _ => new RuntimeChangeResult(false, "No action")
            };
            ShowToast(result.Message, result.LiveAcknowledged);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshLossless_Click(object sender, RoutedEventArgs e) => RefreshLosslessStatus();

    private async void RefreshRuntimeSources_Click(object sender, RoutedEventArgs e) => await RefreshRuntimeSourcesAsync(true);

    private async Task RefreshRuntimeSourcesAsync(bool showToast)
    {
        try
        {
            var result = await ResolveRuntimeSourcesAsync(promptForNr: false);
            if (showToast)
                ShowToast(result.Ready
                    ? $"Runtime sources ready · upstream {result.UpstreamTag}"
                    : "Official setup is ready. A local nvngx_dlssnr.dll is still needed.", result.Ready);
        }
        catch (Exception ex)
        {
            RuntimeSourcesStatus = "Automatic source check needs attention";
            RuntimeSetupStatus = "Official setup: could not verify or download";
            RuntimeNrStatus = RuntimeSourceService.IsValidNrDll(NvngxDlssNrPath)
                ? "NVIDIA runtime: local x64 DLL ready"
                : "NVIDIA runtime: no validated local copy selected";
            if (showToast) ShowError(ex);
        }
    }

    private async Task<RuntimeSourceResult> ResolveRuntimeSourcesAsync(bool promptForNr, GameEntry? target = null)
    {
        var preferredTag = RuntimeSourceService.GetPreferredUpstreamTag(target);
        RuntimeSourcesStatus = preferredTag is null ? "Checking automatic runtime sources…" : $"Checking {preferredTag} compatibility runtime…";
        RuntimeSetupStatus = preferredTag is null ? "Official setup: checking GitHub release and SHA-256" : $"Official setup: verifying {preferredTag} for {target!.Name}";
        RuntimeNrStatus = "NVIDIA runtime: searching local copies";

        var result = await _runtimeSources.EnsureAsync(
            UpstreamSetupPath,
            NvngxDlssNrPath,
            Games,
            [LosslessNrPath],
            preferredTag);

        if (!string.Equals(UpstreamSetupPath, result.SetupPath, StringComparison.OrdinalIgnoreCase))
            UpstreamSetupPath = result.SetupPath;
        if (result.NrDllPath is not null && !string.Equals(NvngxDlssNrPath, result.NrDllPath, StringComparison.OrdinalIgnoreCase))
            NvngxDlssNrPath = result.NrDllPath;

        if (!result.NrDllFound && promptForNr)
        {
            var selected = PickFile(
                "Select your local nvngx_dlssnr.dll",
                "nvngx_dlssnr.dll|nvngx_dlssnr.dll|DLL files (*.dll)|*.dll");
            if (selected is null)
                throw new InvalidOperationException("The official AMD setup is ready, but no local nvngx_dlssnr.dll was found. Select your legitimate local DLL to continue.");
            if (!RuntimeSourceService.IsValidNrDll(selected))
                throw new InvalidOperationException("The selected nvngx_dlssnr.dll is not a valid 64-bit Neural Rendering DLL.");
            NvngxDlssNrPath = selected;
            result = result with { NrDllPath = selected, NrDllFound = true };
        }

        RuntimeSetupStatus = $"Official setup: {result.UpstreamTag} verified{(result.SetupDownloaded ? " and cached" : string.Empty)}{(preferredTag is null ? string.Empty : " · compatibility selected")}";
        RuntimeNrStatus = result.NrDllFound
            ? "NVIDIA runtime: validated local x64 DLL ready"
            : "NVIDIA runtime: no local copy found yet — it will be requested only when needed";
        RuntimeSourcesStatus = result.Ready ? "Ready for one-click game setup" : "Official setup ready · NVIDIA runtime pending";
        return result;
    }

    private void RefreshLosslessStatus()
    {
        var install = _lossless.DetectInstall(_settings.LosslessScalingPath);
        if (install is null)
        {
            LosslessInstallPath = string.Empty;
            LosslessStatusText = "Lossless Scaling was not detected";
            return;
        }
        _settings.LosslessScalingPath = install;
        LosslessInstallPath = install;
        var status = _lossless.GetStatus(install);
        if (!_losslessDeviceInitialized && status.Installed && !string.IsNullOrWhiteSpace(status.HipDevice))
        {
            _losslessDeviceInitialized = true;
            _settings.HipVisibleDevices = status.HipDevice;
            OnPropertyChanged(nameof(HipVisibleDevices));
        }
        LosslessStatusText = status.Installed
            ? $"{status.Message} · neural max {status.NeuralMaxHeight?.ToString() ?? "?"}p{(status.Running ? " · running" : string.Empty)}"
            : status.Message;
        SaveSettings();
    }

    private async void InstallLossless_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshLosslessStatus();
            if (string.IsNullOrWhiteSpace(LosslessInstallPath)) throw new InvalidOperationException("Lossless Scaling is not installed or could not be detected.");
            if (!File.Exists(LosslessProxyPath) || !File.Exists(LosslessNrPath)) throw new InvalidOperationException("Select the private AMD proxy and nvngx_dlssnr.dll first.");
            ShowToast("Installing the Lossless Scaling bridge…");
            await _lossless.InstallOrUpdateAsync(FindRepoRoot(), LosslessInstallPath, LosslessProxyPath, LosslessNrPath, HipVisibleDevices);
            RefreshLosslessStatus();
            ShowToast("Lossless Scaling bridge installed. Press Scale normally to start it.", true);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void UninstallLossless_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshLosslessStatus();
            if (string.IsNullOrWhiteSpace(LosslessInstallPath)) throw new InvalidOperationException("Lossless Scaling is not installed or could not be detected.");
            await _lossless.UninstallAsync(FindRepoRoot(), LosslessInstallPath);
            RefreshLosslessStatus();
            ShowToast("Lossless Scaling bridge removed.", true);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void BrowseSetup_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Select official dlssnr_on_amd_setup.exe", "dlssnr_on_amd_setup.exe|dlssnr_on_amd_setup.exe|Executable files (*.exe)|*.exe");
        if (path is not null) UpstreamSetupPath = path;
    }

    private void BrowseNr_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Select nvngx_dlssnr.dll", "nvngx_dlssnr.dll|nvngx_dlssnr.dll|DLL files (*.dll)|*.dll");
        if (path is not null) NvngxDlssNrPath = path;
    }

    private void BrowseLosslessProxy_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Select AMD compatibility proxy version.dll", "version.dll|version.dll|DLL files (*.dll)|*.dll");
        if (path is not null) LosslessProxyPath = path;
    }

    private void BrowseLosslessNr_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Select nvngx_dlssnr.dll for Lossless Scaling", "nvngx_dlssnr.dll|nvngx_dlssnr.dll|DLL files (*.dll)|*.dll");
        if (path is not null) LosslessNrPath = path;
    }

    private void HotkeysSetting_Click(object sender, RoutedEventArgs e) => UpdateHotkeyRegistration();

    private static string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private string? FindRepoRoot()
    {
        var candidates = new List<string> { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
        foreach (var start in candidates)
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; dir is not null && i < 10; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "direct-game", "amd_dlss5.py")) && Directory.Exists(Path.Combine(dir.FullName, "auto-scale"))) return dir.FullName;
            }
        }
        return null;
    }

    private void SaveSettings()
    {
        try { _settingsService.Save(_settings); } catch { }
    }

    private void ShowToast(string message, bool success = false)
    {
        ToastText.Text = message;
        Toast.BorderBrush = success ? (System.Windows.Media.Brush)FindResource("SuccessBrush") : (System.Windows.Media.Brush)FindResource("LineBrush");
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ShowError(Exception ex)
    {
        ShowToast(ex.Message);
        Debug.WriteLine(ex);
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
