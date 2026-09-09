using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class RuntimeControlService
{
    private const string Section = "DlssNrOnAmd";

    public void Refresh(GameEntry game, bool? runningOverride = null)
    {
        game.Installed = File.Exists(game.ConfigPath) && HasProxy(game.DirectoryPath);
        game.Running = runningOverride ?? IsRunning(game.ExePath);
        if (File.Exists(game.ConfigPath))
        {
            var ini = IniDocument.Load(game.ConfigPath);
            game.Enabled = ini.GetBool(Section, "Enabled", true);
            game.LocalStructure = Math.Clamp(ini.GetDouble(Section, "LocalStructure", 1.0), 0.0, 2.0);
            game.LocalTone = Math.Clamp(ini.GetDouble(Section, "LocalTone", 1.0), 0.0, 2.0);
            game.SkinStructure = Math.Clamp(ini.GetDouble(Section, "SkinStructure", 1.0), 0.0, 2.0);
        }
        game.RuntimeStatus = game.Running ? "Game running — End opens AMD live controls" : game.Installed ? "Ready for next launch" : "Not installed";
    }

    public async Task<RuntimeChangeResult> SetEnabledAsync(GameEntry game, bool enabled, CancellationToken cancellationToken = default)
    {
        return await ChangeAsync(game, ini => ini.Set(Section, "Enabled", enabled ? "1" : "0"), cancellationToken);
    }

    public async Task<RuntimeChangeResult> AdjustStructureAsync(GameEntry game, double delta, CancellationToken cancellationToken = default)
    {
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
        if (!File.Exists(game.ConfigPath)) throw new InvalidOperationException("The AMD runtime config is not installed for this game.");
        var ini = IniDocument.Load(game.ConfigPath);
        ini.Set(Section, "Inline", inline ? "1" : "0");
        ini.SaveAtomic(game.ConfigPath);
        Refresh(game);
    }

    private Task<RuntimeChangeResult> SetScalarAsync(GameEntry game, string key, double value, CancellationToken cancellationToken) =>
        ChangeAsync(game, ini => ini.Set(Section, key, Math.Clamp(value, 0.0, 2.0).ToString("0.0", CultureInfo.InvariantCulture)), cancellationToken);

    private async Task<RuntimeChangeResult> ChangeAsync(GameEntry game, Action<IniDocument> change, CancellationToken cancellationToken)
    {
        if (!File.Exists(game.ConfigPath)) throw new InvalidOperationException("The AMD runtime config is not installed for this game.");
        var logLength = SafeLength(game.LogPath);
        var ini = IniDocument.Load(game.ConfigPath);
        change(ini);
        ini.SaveAtomic(game.ConfigPath);
        Refresh(game);

        if (!game.Running) return new RuntimeChangeResult(false, "Saved for the next launch");
        var acknowledged = await WaitForRuntimeAcknowledgementAsync(game.LogPath, logLength, cancellationToken);
        game.LiveAcknowledged = acknowledged;
        return acknowledged
            ? new RuntimeChangeResult(true, "AMD runtime acknowledged the live settings change")
            : new RuntimeChangeResult(false, "Saved. If the image does not change immediately, press End for the AMD runtime overlay or relaunch the game.");
    }

    private static async Task<bool> WaitForRuntimeAcknowledgementAsync(string logPath, long initialLength, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2.5);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(125, cancellationToken);
            if (!File.Exists(logPath)) continue;
            try
            {
                using var stream = File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length <= initialLength) continue;
                stream.Position = Math.Min(initialLength, stream.Length);
                using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: false);
                var added = (await reader.ReadToEndAsync(cancellationToken)).ToLowerInvariant();
                if (added.Contains("settings change") || added.Contains("ini changed")) return true;
            }
            catch { }
        }
        return false;
    }

    private static bool HasProxy(string directory)
    {
        string[] proxies = ["version.dll", "winmm.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll", "dxgi.dll"];
        return proxies.Any(name => File.Exists(Path.Combine(directory, name)));
    }

    public static bool IsRunning(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            return Process.GetProcessesByName(name).Any(process =>
            {
                using (process)
                {
                    try { return process.MainModule?.FileName?.Equals(Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase) ?? true; }
                    catch { return true; }
                }
            });
        }
        catch { return false; }
    }

    private static long SafeLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}

public sealed record RuntimeChangeResult(bool LiveAcknowledged, string Message);
