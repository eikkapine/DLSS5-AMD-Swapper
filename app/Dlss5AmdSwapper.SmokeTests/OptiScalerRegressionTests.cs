using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;
using static OptiScalerTests;

internal static class OptiScalerRegressionTests
{
    private const string Startup = "[12:00:00.000000] [W] OptiScaler v10.0.0-dev (amd-presr) loaded\n";
    private const string Cost = "[I] DLSS-NR cost: 12.40 ms total = 10.10 ms model\n";

    internal static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Pre-SR distinguishes the loader, waiting input and completed neural work", () =>
        {
            var diag = OptiScalerDiagnosticsService.Parse("AMD pre-SR: waiting for a DirectX 12 SR frame\n", Startup + "DlssNr.Enabled: true\nDLSS-NR running before SR (no sizes)\nQuirk: Disable FSR 3.0 Inputs\nWndProc is not subclassed\n");
            Check(diag.LoaderObserved && !diag.PreSrActive, "loaded/announced is not a completed dispatch");
            Check(diag.LastFault is null, "waiting is not a fault");
            Check(diag.FsrInputsDisabled && diag.InputHookWarning, "runtime input compatibility diagnostics");
            Check(diag.Summary.Contains("choose DLSS or XeSS", StringComparison.Ordinal), "actionable input guidance");
            Check(OptiScalerDiagnosticsService.Parse(string.Empty, Startup + Cost).PreSrActive, "a session timing proves completed work");
            return Task.CompletedTask;
        });

        await run("Pre-SR separates upscaling never switched on from a failed neural dispatch", () =>
        {
            // Real Assetto Corsa Rally session: loader healthy, exposure scan running, but the
            // game never created an ffx upscaler context, so no pre-SR log is ever written.
            var noUpscaler = OptiScalerDiagnosticsService.Parse(string.Empty,
                Startup + "DlssNr.Enabled: true\nQuirk: Disable FSR 3.0 Inputs\nDlssNr::ExposureScan::Adopt DLSS-NR exposure scan: candidate 1 -- buffer, 104 bytes\n");
            Check(noUpscaler.LoaderObserved && !noUpscaler.UpscalerObserved, "no upscaler context means no upscaler ran");
            Check(noUpscaler.Summary.Contains("no upscaler ran this session", StringComparison.Ordinal), "summary must name the real cause");

            var withUpscaler = OptiScalerDiagnosticsService.Parse(string.Empty,
                Startup + "ffxCreateContext_Dx12 context created: 1EE8\nffxDispatch_Dx12 Not in _contexts\n");
            Check(withUpscaler.UpscalerObserved, "an ffx context is proof the game asked for upscaling");
            Check(!withUpscaler.Summary.Contains("no upscaler ran this session", StringComparison.Ordinal), "do not blame settings once an upscaler ran");
            return Task.CompletedTask;
        });

        await run("Pre-SR latest startup and later failures cannot inherit earlier success", () =>
        {
            var reset = OptiScalerDiagnosticsService.Parse("Completed AMD pre-SR passes=1\n", Startup + Cost + Startup + "Init done\n");
            Check(!reset.PreSrActive && reset.CostSamples == 0, "new startup invalidates the old OptiScaler timing and uncorrelated AMD log");
            Check(!OptiScalerDiagnosticsService.Parse("Completed AMD pre-SR passes=1\nHIP completion timeout pass 1\n", Startup + Cost).PreSrActive, "later AMD failure wins");
            Check(!OptiScalerDiagnosticsService.Parse("Completed AMD pre-SR passes=1\n", Startup + Cost + "DLSS-NR initialization failed\n").PreSrActive, "later OptiScaler failure wins");
            var recoveredAmd = OptiScalerDiagnosticsService.Parse("HIP completion timeout pass 1\nCompleted AMD pre-SR passes=1\n", Startup + Cost + "DLSS-NR initialization failed\n");
            Check(recoveredAmd.LastFault == "DLSS-NR initialization failed", "current OptiScaler fault takes priority over recovered AMD fault");
            Check(OptiScalerDiagnosticsService.Parse(string.Empty, Startup + "DLSS-NR initialization failed\n" + Cost).PreSrActive, "new completed timing proves recovery in the same log");
            Check(!OptiScalerDiagnosticsService.Parse("Completed AMD pre-SR passes=1\nHIP runtime: amdhip64_7.dll\nInitialized independent AMD pass 1\n", string.Empty).PreSrActive, "new AMD startup invalidates an earlier completed pass");
            Check(OptiScalerDiagnosticsService.Parse("HIP completion timeout pass 1\nHIP runtime: amdhip64_7.dll\nCompleted AMD pre-SR passes=1\n", string.Empty).LastFault is null, "an earlier AMD session fault is not assigned to the new session");
            var malformed = OptiScalerDiagnosticsService.Parse("Completed AMD pre-SR passes=99999999999999999999\n", "DLSS-NR cost: 1.2.3 ms total = 4.5.6 ms model\n");
            Check(!malformed.PreSrActive, "malformed numeric log fields do not throw or prove work");
            Check(!OptiScalerDiagnosticsService.Parse(string.Empty, Startup + "DLSS-NR cost: 0.00 ms total = 0.00 ms model\n").PreSrActive, "zero model time is not completion evidence");
            Check(!OptiScalerDiagnosticsService.Parse(string.Empty, Startup + "DLSS-NR cost: 1.00 ms total = 0.00 ms model\n").PreSrActive, "overhead without model work is not neural evidence");
            return Task.CompletedTask;
        });

        await run("Pre-SR inspection rejects logs from before the current install", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var temp = f.Temp;
            await File.WriteAllTextAsync(f.Game.PreSrLogPath, "Completed AMD pre-SR passes=1\n");
            File.SetLastWriteTimeUtc(f.Game.PreSrLogPath, DateTime.UtcNow.AddDays(-1));
            await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Balanced, false);
            var diag = await new OptiScalerDiagnosticsService().InspectAsync(f.Game);
            Check(!diag.PreSrActive && diag.HistoricalEvidence && !diag.HasLogEvidence, "previous install log excluded");
            Check(diag.PreSrLogBytes > 0 && diag.PreSrLogSha256?.Length == 64, "stale evidence remains hash referenced");
        });

        await run("Pre-SR inspection recognizes a newer proxy launch without fresh AMD work", async () =>
        {
            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            await File.WriteAllTextAsync(game.PreSrLogPath, "Completed AMD pre-SR passes=1\n");
            File.SetLastWriteTimeUtc(game.PreSrLogPath, DateTime.UtcNow.AddDays(-1));
            var stamp = DateTime.Now.ToString("HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(game.OptiScalerLogPath, $"[{stamp}] [W] OptiScaler v10.0.0-dev (amd-presr) loaded\nInit done\n");
            var diag = await new OptiScalerDiagnosticsService().InspectAsync(game);
            Check(!diag.PreSrActive && diag.LoaderObserved && diag.HistoricalEvidence, "old pass output cannot prove new loader dispatch");
        });

        await run("Pre-SR missing configured passes and removed proxy are repairable incomplete installs", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var temp = f.Temp;
            await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Balanced, false);
            File.Delete(Path.Combine(f.GameDir, "dlssnr_amd_pass3.dll"));
            Check(OptiScalerInstallerService.GetMissingRequiredFiles(f.Game).Count == 0, "unused pass 3 does not block a one-pass launch");
            await new OptiScalerControlService().SetPassesAsync(f.Game, 3);
            Check(OptiScalerInstallerService.GetMissingRequiredFiles(f.Game).Contains("dlssnr_amd_pass3.dll"), "selected pass is required");
            File.Delete(Path.Combine(f.GameDir, "dxgi.dll"));
            await File.WriteAllBytesAsync(Path.Combine(f.GameDir, "version.dll"), [1]);
            await File.WriteAllTextAsync(f.Game.OptiScalerLogPath, Startup + Cost);
            new RuntimeControlService().Refresh(f.Game, false);
            var diag = await new OptiScalerDiagnosticsService().InspectAsync(f.Game);
            Check(!f.Game.Installed && !diag.PreSrActive && diag.InstallComplete == false, "another proxy plus old success cannot hide missing managed files");
            Check(diag.MissingFiles.Contains("dxgi.dll") && diag.Summary.Contains("Use Repair"), "names missing recorded proxy and recovery");
            Check(f.Game.HasManagedInstall && f.Game.SetupLabel == "Repair", "incomplete install still offers repair and managed restore");
        });

        await run("Pre-SR activity requires current-session evidence even when the startup left the tail", async () =>
        {
            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            await File.WriteAllTextAsync(game.OptiScalerLogPath, Startup + new string('x', 2 * 1024 * 1024) + "\nInit done\n");
            await File.WriteAllTextAsync(game.PreSrLogPath, "Completed AMD pre-SR passes=1\n");
            var diag = await new OptiScalerDiagnosticsService().InspectAsync(game);
            Check(diag.LoaderObserved && !diag.PreSrActive, "whole-log startup still scopes the activity gate");
            Check(diag.OptiLogSha256 == await DirectGameInstallerService.Sha256Async(game.OptiScalerLogPath), "single read hash matches sampled bytes");
        });

        await run("Pre-SR boot timestamps scope short completed-pass logs to the current launch", async () =>
        {
            var observed = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
            var start = observed.AddSeconds(-5);
            const long uptime = 100000;
            var log = "90000 Completed AMD pre-SR passes=3 at 320x180\n96000 HIP runtime: amdhip64_7.dll\n97000 Completed AMD pre-SR passes=2 at 320x180\n98000 Workers stopped outside loader lock\n";
            var scoped = OptiScalerDiagnosticsService.ScopePreSrSession(log, observed.AddSeconds(-2), start, observed, uptime);
            Check(scoped.Correlated && !scoped.Text.Contains("passes=3") && scoped.Text.Contains("passes=2"), "old successful pass is excluded by launch time");
            Check(!OptiScalerDiagnosticsService.ScopePreSrSession(log, observed.AddDays(-1), start, observed, uptime).Correlated, "prior boot metadata is not accepted");
            Check(!OptiScalerDiagnosticsService.ScopePreSrSession(log, observed.AddSeconds(-30), start, observed, uptime).Correlated, "timestamp clock must agree with file write time");
            Check(!OptiScalerDiagnosticsService.ScopePreSrSession("99999999999999 Completed AMD pre-SR passes=1\n", observed, start, observed, uptime).Correlated, "future/wrapped clock is not accepted");
            Check(!OptiScalerDiagnosticsService.ScopePreSrSession("97000 Completed AMD pre-SR passes=1\n", observed.AddSeconds(-3), start, observed, uptime).Correlated, "numeric completion without an identified AMD startup is uncorrelated");
            Check(!OptiScalerDiagnosticsService.ScopePreSrSession("94000 HIP runtime: amdhip64_7.dll\n97000 Completed AMD pre-SR passes=1\n", observed.AddSeconds(-3), start, observed, uptime).Correlated, "a startup from before the current OptiScaler session cannot inherit it");
            Check(!OptiScalerDiagnosticsService.ScopePreSrSession("96000 HIP runtime: amdhip64_7.dll\nCompleted AMD pre-SR passes=1\n98000 Workers stopped outside loader lock\n", observed.AddSeconds(-2), start, observed, uptime).Correlated, "untimed completion cannot borrow a neighboring timestamp");
            Check(!OptiScalerDiagnosticsService.ScopePreSrSession("96000 HIP runtime: amdhip64_7.dll\n999999999999999999999 Completed AMD pre-SR passes=1\n98000 Workers stopped outside loader lock\n", observed.AddSeconds(-2), start, observed, uptime).Correlated, "overflowing completion clock is rejected");
            var faultScoped = OptiScalerDiagnosticsService.ScopePreSrSession(log + "HIP completion timeout pass 1\n", observed.AddSeconds(-2), start, observed, uptime);
            Check(faultScoped.Correlated && faultScoped.Text.Contains("HIP completion timeout"), "untimed faults survive valid clock correlation");

            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            var now = DateTime.UtcNow;
            var tick = Environment.TickCount64;
            var localStart = now.AddSeconds(-2).ToLocalTime().ToString("HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(game.OptiScalerLogPath, $"[{localStart}] [W] OptiScaler v10.0.0-dev (amd-presr) loaded\n");
            await File.WriteAllTextAsync(game.PreSrLogPath, $"{tick - 500} HIP runtime: amdhip64_7.dll\n{tick} Completed AMD pre-SR passes=2 at 320x180\n");
            var diag = await new OptiScalerDiagnosticsService().InspectAsync(game);
            Check(diag.PreSrActive && diag.PassesCompleted == 2 && diag.CostSamples == 0, "fresh completed AMD passes prove a short run without a timing sample");
            await File.AppendAllTextAsync(game.PreSrLogPath, "HIP completion timeout pass 1\n");
            diag = await new OptiScalerDiagnosticsService().InspectAsync(game);
            Check(!diag.PreSrActive && diag.LastFault?.Contains("HIP completion timeout") == true, "a later untimed fault invalidates correlated completed work");
            localStart = DateTime.UtcNow.AddSeconds(1).ToLocalTime().ToString("HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(game.OptiScalerLogPath, $"[{localStart}] [W] OptiScaler v10.0.0-dev (amd-presr) loaded\n");
            File.SetLastWriteTimeUtc(game.OptiScalerLogPath, DateTime.UtcNow.AddSeconds(2));
            diag = await new OptiScalerDiagnosticsService().InspectAsync(game);
            Check(!diag.PreSrActive && diag.HistoricalEvidence, "a newer launch invalidates the previous short success");
        });

        await run("Pre-SR update preserves user controls and unrelated OptiScaler settings", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var temp = f.Temp;
            await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Balanced, false);
            var ini = IniDocument.Load(f.Game.OptiScalerIniPath);
            ini.Set("DlssNr", "Enabled", "false"); ini.Set("DlssNr", "Passes", "2"); ini.Set("DlssNr", "LocalTone", "1.7");
            ini.Set("Menu", "ShortcutKey", "0x24"); ini.Set("Inputs", "EnableFsr3Inputs", "false");
            ini.SaveAtomic(f.Game.OptiScalerIniPath);
            await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Balanced, true);
            ini = IniDocument.Load(f.Game.OptiScalerIniPath);
            Check(ini.Get("DlssNr", "Enabled") == "false" && ini.Get("DlssNr", "Passes") == "2" && ini.Get("DlssNr", "LocalTone") == "1.7", "update retains neural controls");
            Check(ini.Get("Menu", "ShortcutKey") == "0x24" && ini.Get("Inputs", "EnableFsr3Inputs") == "false", "update retains runtime hotkey and input preferences");
        });

        await run("Pre-SR update refuses a newly introduced unmanaged proxy before modifying files", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var temp = f.Temp;
            await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Balanced, false);
            var before = await File.ReadAllBytesAsync(f.Game.ManifestPath);
            await File.WriteAllBytesAsync(Path.Combine(f.GameDir, "version.dll"), [5, 6, 7]);
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Balanced, true); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("additional unmanaged proxy"), "clear proxy conflict reason"); }
            Check(before.SequenceEqual(await File.ReadAllBytesAsync(f.Game.ManifestPath)), "conflict does not modify previous manifest");
        });

        await run("Pre-SR install refuses to replace a different user-owned dependency", async () =>
        {
            var f = await MakeInstallFixtureAsync(preexistingLibxess: true);
            using var temp = f.Temp;
            var path = Path.Combine(f.GameDir, "OptiScaler", "libxess.dll");
            await File.WriteAllBytesAsync(path, [8, 9, 10]);
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Balanced, false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("pre-existing dependency"), "clear existing library conflict reason"); }
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(new byte[] { 8, 9, 10 }), "original dependency remains untouched");
            Check(!File.Exists(Path.Combine(f.GameDir, "dxgi.dll")) && !File.Exists(f.Game.ManifestPath), "failed preflight performs no install writes");
        });

        await run("OptiScaler activation guidance respects Assetto and Cyberpunk input compatibility", () =>
        {
            var assetto = OptiScalerInstallerService.GetActivationGuidance(new GameEntry { ExePath = @"C:\Games\ACR.EXE" });
            var cyberpunk = OptiScalerInstallerService.GetActivationGuidance(new GameEntry { ExePath = @"C:\Games\Cyberpunk2077.exe" });
            Check(assetto.Contains("DLSS or XeSS") && assetto.Contains("FSR input hooks are disabled"), "Assetto avoids its disabled input route");
            Check(cyberpunk.Contains("XeSS or FSR") && cyberpunk.Contains("path tracing"), "Cyberpunk avoids forced DLSS ray reconstruction with PT");
            return Task.CompletedTask;
        });
    }
}
