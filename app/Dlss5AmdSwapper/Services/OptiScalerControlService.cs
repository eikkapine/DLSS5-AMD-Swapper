using System.Globalization;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class OptiScalerControlService
{
    public const string Section = "DlssNr";
    private static readonly object WriteLock = new();
    private readonly RuntimeControlService _runtime = new();

    public Task<RuntimeChangeResult> SetEnabledAsync(GameEntry game, bool enabled, CancellationToken cancellationToken = default) =>
        ChangeAsync(game, ini => ini.Set(Section, "Enabled", enabled ? "true" : "false"), cancellationToken);

    public Task<RuntimeChangeResult> SetPassesAsync(GameEntry game, int passes, CancellationToken cancellationToken = default)
    {
        if (passes is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(passes), "Passes must be 1, 2 or 3.");
        return ChangeAsync(game, ini => ini.Set(Section, "Passes", passes.ToString(CultureInfo.InvariantCulture)), cancellationToken);
    }

    public Task<RuntimeChangeResult> SetStructureAsync(GameEntry game, double value, CancellationToken cancellationToken = default) => SetScalarAsync(game, "LocalStructure", value, cancellationToken);
    public Task<RuntimeChangeResult> SetSkinAsync(GameEntry game, double value, CancellationToken cancellationToken = default) => SetScalarAsync(game, "SkinStructure", value, cancellationToken);
    public Task<RuntimeChangeResult> SetToneAsync(GameEntry game, double value, CancellationToken cancellationToken = default) => SetScalarAsync(game, "LocalTone", value, cancellationToken);

    private Task<RuntimeChangeResult> SetScalarAsync(GameEntry game, string key, double value, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return ChangeAsync(game, ini => ini.Set(Section, key, Math.Clamp(value, 0.0, 2.0).ToString("0.0", CultureInfo.InvariantCulture)), cancellationToken);
    }

    private Task<RuntimeChangeResult> ChangeAsync(GameEntry game, Action<IniDocument> change, CancellationToken cancellationToken)
    {
        lock (WriteLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (game.Busy) throw new InvalidOperationException("Wait for the current game operation to finish before changing settings.");
            if (!File.Exists(game.OptiScalerIniPath)) throw new InvalidOperationException("OptiScaler.ini is not installed for this game.");
            var ini = IniDocument.Load(game.OptiScalerIniPath);
            change(ini);
            ini.SaveAtomic(game.OptiScalerIniPath);
            _runtime.Refresh(game);
            return Task.FromResult(new RuntimeChangeResult(false, game.Running
                ? "Saved. OptiScaler reads OptiScaler.ini at startup; use the Insert menu for live changes."
                : "Saved for the next launch"));
        }
    }
}
