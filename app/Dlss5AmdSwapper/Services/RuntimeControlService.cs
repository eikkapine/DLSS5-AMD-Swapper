using System.Diagnostics;
using System.Globalization;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class RuntimeControlService
{
    private const string Section = "DlssNrOnAmd";
    // Keep each read/modify/replace together across service instances and duplicate library entries.
    private static readonly object ConfigWriteLock = new();

    public void Refresh(GameEntry game, bool? runningOverride = null)
    {
        lock (ConfigWriteLock)
        {
            game.LiveAcknowledged = false;
            game.Route = ManagedManifest.ReadRoute(game);
            game.Running = runningOverride ?? IsRunning(game.ExePath);
            if (game.Route == InstallRoute.OptiScalerPreSr)
            {
                game.Installed = File.Exists(game.OptiScalerIniPath) && HasProxy(game.DirectoryPath);
                if (game.Installed)
                {
                    var ini = IniDocument.Load(game.OptiScalerIniPath);
                    game.Enabled = ini.GetBool(OptiScalerControlService.Section, "Enabled", true);
                    game.LocalStructure = Math.Clamp(ini.GetDouble(OptiScalerControlService.Section, "LocalStructure", 1.0), 0.0, 2.0);
                    game.LocalTone = Math.Clamp(ini.GetDouble(OptiScalerControlService.Section, "LocalTone", 0.0), 0.0, 2.0);
                    game.SkinStructure = Math.Clamp(ini.GetDouble(OptiScalerControlService.Section, "SkinStructure", 1.0), 0.0, 2.0);
                    game.Passes = int.TryParse(ini.Get(OptiScalerControlService.Section, "Passes"), out var passes) ? Math.Clamp(passes, 1, 3) : 1;
                }
                else ResetControls(game);
                game.RuntimeStatus = !game.Installed ? "Not installed" : game.Running ? "Game running - Insert opens the OptiScaler menu" : "Ready for next launch";
                return;
            }

            game.Installed = File.Exists(game.ConfigPath) && HasProxy(game.DirectoryPath);
            if (File.Exists(game.ConfigPath))
            {
                var ini = IniDocument.Load(game.ConfigPath);
                game.Enabled = ini.GetBool(Section, "Enabled", true);
                game.LocalStructure = Math.Clamp(ini.GetDouble(Section, "LocalStructure", 1.0), 0.0, 2.0);
                game.LocalTone = Math.Clamp(ini.GetDouble(Section, "LocalTone", 1.0), 0.0, 2.0);
                game.SkinStructure = Math.Clamp(ini.GetDouble(Section, "SkinStructure", 1.0), 0.0, 2.0);
            }
            else ResetControls(game);
            game.Passes = 1;
            game.RuntimeStatus = !game.Installed ? "Not installed" : game.Running ? "Game running - End opens AMD live controls" : "Ready for next launch";
        }
    }

    private static void ResetControls(GameEntry game)
    {
        game.Enabled = false;
        game.LocalStructure = game.LocalTone = game.SkinStructure = 1.0;
        game.Passes = 1;
    }

    public async Task<RuntimeChangeResult> SetEnabledAsync(GameEntry game, bool enabled, CancellationToken cancellationToken = default)
    {
        return await ChangeAsync(game, ini => ini.Set(Section, "Enabled", enabled ? "1" : "0"), cancellationToken);
    }

    public async Task<RuntimeChangeResult> AdjustStructureAsync(GameEntry game, double delta, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta));
        return await ChangeAsync(game, ini =>
        {
            var next = Math.Clamp(Math.Round(ini.GetDouble(Section, "LocalStructure", 1.0) + delta, 1, MidpointRounding.AwayFromZero), 0.0, 2.0);
            ini.Set(Section, "LocalStructure", next.ToString("0.0", CultureInfo.InvariantCulture));
        }, cancellationToken);
    }

    public Task<RuntimeChangeResult> SetStructureAsync(GameEntry game, double value, CancellationToken cancellationToken = default) =>
        SetScalarAsync(game, "LocalStructure", value, cancellationToken);

    public Task<RuntimeChangeResult> SetToneAsync(GameEntry game, double value, CancellationToken cancellationToken = default) =>
        SetScalarAsync(game, "LocalTone", value, cancellationToken);

    public Task<RuntimeChangeResult> SetSkinStructureAsync(GameEntry game, double value, CancellationToken cancellationToken = default) =>
        SetScalarAsync(game, "SkinStructure", value, cancellationToken);

    public void SetInline(GameEntry game, bool inline)
    {
        lock (ConfigWriteLock)
        {
            EnsureEditable(game);
            var ini = IniDocument.Load(game.ConfigPath);
            ini.Set(Section, "Inline", inline ? "1" : "0");
            ini.SaveAtomic(game.ConfigPath);
            Refresh(game);
        }
    }

    private static readonly string[] LayerKeys = ["LocalStructure", "SkinStructure", "LocalTone"];

    public LayerState ReadLayers(string iniPath)
    {
        lock (ConfigWriteLock)
        {
            var ini = IniDocument.Load(iniPath);
            var structure = Math.Clamp(ini.GetDouble(Section, "LocalStructure", 1.0), 0.0, 2.0);
            var skin = ini.GetDouble(Section, "SkinStructure", 1.0);
            return new LayerState(
                structure,
                skin < 0 ? structure : Math.Clamp(skin, 0.0, 2.0),
                Math.Clamp(ini.GetDouble(Section, "LocalTone", 0.0), 0.0, 2.0),
                skin < 0);
        }
    }

    public Task<RuntimeChangeResult> SetLayerAsync(string iniPath, string key, double value, CancellationToken cancellationToken = default)
    {
        if (!LayerKeys.Contains(key, StringComparer.Ordinal)) throw new ArgumentException("Unknown layer key.", nameof(key));
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var stored = key == "SkinStructure" && value < 0 ? -1.0 : Math.Clamp(value, 0.0, 2.0);
        return ChangeIniAsync(iniPath, ini => ini.Set(Section, key, stored.ToString("0.0", CultureInfo.InvariantCulture)), cancellationToken);
    }

    public Task<RuntimeChangeResult> AdjustLayerAsync(string iniPath, string key, double delta, CancellationToken cancellationToken = default)
    {
        if (!LayerKeys.Contains(key, StringComparer.Ordinal)) throw new ArgumentException("Unknown layer key.", nameof(key));
        if (!double.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta));
        return ChangeIniAsync(iniPath, ini =>
        {
            var current = ini.GetDouble(Section, key, key == "LocalTone" ? 0.0 : 1.0);
            if (key == "SkinStructure" && current < 0) current = ini.GetDouble(Section, "LocalStructure", 1.0);
            var next = Math.Clamp(Math.Round(current + delta, 1, MidpointRounding.AwayFromZero), 0.0, 2.0);
            ini.Set(Section, key, next.ToString("0.0", CultureInfo.InvariantCulture));
        }, cancellationToken);
    }

    private Task<RuntimeChangeResult> ChangeIniAsync(string iniPath, Action<IniDocument> change, CancellationToken cancellationToken)
    {
        lock (ConfigWriteLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(iniPath)) throw new InvalidOperationException("The runtime config was not found: " + Path.GetFileName(iniPath));
            var ini = IniDocument.Load(iniPath);
            change(ini);
            ini.SaveAtomic(iniPath);
            return Task.FromResult(new RuntimeChangeResult(false, "Saved. The runtime reloads this file while it runs."));
        }
    }

    private Task<RuntimeChangeResult> SetScalarAsync(GameEntry game, string key, double value, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return ChangeAsync(game, ini => ini.Set(Section, key, Math.Clamp(value, 0.0, 2.0).ToString("0.0", CultureInfo.InvariantCulture)), cancellationToken);
    }

    private Task<RuntimeChangeResult> ChangeAsync(GameEntry game, Action<IniDocument> change, CancellationToken cancellationToken)
    {
        lock (ConfigWriteLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureEditable(game);
            var ini = IniDocument.Load(game.ConfigPath);
            change(ini);
            ini.SaveAtomic(game.ConfigPath);
            Refresh(game);
            // The upstream log's generic reload message does not identify a key or value.
            // It cannot prove this particular request was applied to the running renderer.
            return Task.FromResult(new RuntimeChangeResult(false, game.Running
                ? "Saved. Live application is unverified; press End for AMD runtime controls or relaunch the game."
                : "Saved for the next launch"));
        }
    }

    private static void EnsureEditable(GameEntry game)
    {
        if (game.Busy) throw new InvalidOperationException("Wait for the current game operation to finish before changing settings.");
        if (!File.Exists(game.ConfigPath)) throw new InvalidOperationException("The AMD runtime config is not installed for this game.");
    }

    private static bool HasProxy(string directory)
    {
        string[] proxies = ["version.dll", "winmm.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll", "dxgi.dll"];
        return proxies.Any(name => File.Exists(Path.Combine(directory, name)));
    }

    public static bool IsRunning(string exePath)
    {
        Process[] processes = [];
        try
        {
            var fullPath = Path.GetFullPath(exePath);
            processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fullPath));
            foreach (var process in processes)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (NotSupportedException) { }
            }
            return false;
        }
        catch { return false; }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

}

public sealed record RuntimeChangeResult(bool LiveAcknowledged, string Message);

public sealed record LayerState(double Structure, double Skin, double Tone, bool SkinFollowsStructure);
