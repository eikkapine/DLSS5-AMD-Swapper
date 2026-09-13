using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public static class OptiScalerIniWriter
{
    private const string MinimalHeader = "; Written by DLSS5 AMD Swapper. Unlisted OptiScaler keys keep their defaults.\n; Open the in-game OptiScaler menu (Del) to change anything else.\n";

    // Virtual-key codes for OptiScaler's in-game overlay, surfaced so the UI can name real keys.
    // ponytail: the key name is repeated in user-facing strings below and in the docs; if it
    // changes again, grep for "Del" rather than relying on this constant alone.
    public const string MenuShortcutKey = "0x2E";      // Del
    public const string FpsShortcutKey = "0x21";       // Page Up
    public const string FpsCycleShortcutKey = "0x22";  // Page Down

    public static bool RequiresEnabler(OptiScalerPreset preset) => preset == OptiScalerPreset.Performance;

    public static string Build(string? baseIniText, OptiScalerPreset preset, bool enablerAvailable, string? gameExe = null, bool preserveControls = false)
    {
        var ini = IniDocument.FromText(string.IsNullOrWhiteSpace(baseIniText) ? MinimalHeader : baseIniText);
        ini.Set("Upscalers", "Dx12Upscaler", "ffx");
        void SetControl(string key, string value)
        {
            var current = ini.Get("DlssNr", key);
            if (!preserveControls || string.IsNullOrWhiteSpace(current) || current.Equals("auto", StringComparison.OrdinalIgnoreCase)) ini.Set("DlssNr", key, value);
        }
        SetControl("Enabled", "true");
        ini.Set("DlssNr", "RunBeforeSR", "true");
        SetControl("Passes", "1");
        SetControl("LocalTone", "0");
        SetControl("LocalStructure", "1");
        SetControl("SkinStructure", "1");
        ini.Set("DlssNr", "ApplyAfterRR", "false");
        ini.Set("Log", "LogToFile", "true");
        ini.Set("Log", "LogLevel", "2");

        // The in-game overlay is OptiScaler's own ImGui menu (arrow keys / Enter). Write its keys
        // explicitly instead of leaving them "auto" so the manager can state which keys work.
        // Only fill in defaults: the overlay saves settings back to this file, so a key the
        // player rebound in-game must survive the next update.
        void SetDefault(string section, string key, string value)
        {
            var current = ini.Get(section, key);
            if (string.IsNullOrWhiteSpace(current) || current.Equals("auto", StringComparison.OrdinalIgnoreCase))
                ini.Set(section, key, value);
        }
        SetDefault("Menu", "OverlayMenu", "true");
        SetDefault("Menu", "ShortcutKey", MenuShortcutKey);
        SetDefault("Menu", "FpsOverlayType", "2");
        SetDefault("Menu", "FpsShortcutKey", FpsShortcutKey);
        SetDefault("Menu", "FpsCycleShortcutKey", FpsCycleShortcutKey);

        // Assetto Corsa Rally's log shows "subclass lost to another WndProc", which leaves the
        // overlay unreachable. Upstream's remedy is polling input instead of hooking WndProc.
        if (string.Equals(gameExe, "acr.exe", StringComparison.OrdinalIgnoreCase))
            SetDefault("Hotfix", "ManualInputPolling", "true");

        // Upstream OptiScaler disables DXGI spoofing for Crimson Desert because the game can
        // otherwise reject the adapter as unsupported during startup. Set it explicitly here so
        // the compatibility rule also wins when an older/package INI already contains Dxgi=true.
        if (string.Equals(gameExe, "CrimsonDesert.exe", StringComparison.OrdinalIgnoreCase))
            ini.Set("Spoofing", "Dxgi", "false");

        if (preset == OptiScalerPreset.Performance && !preserveControls)
        {
            ini.Set("UpscaleRatio", "UpscaleRatioOverrideEnabled", "true");
            ini.Set("UpscaleRatio", "UpscaleRatioOverrideValue", "3.0");
            ini.Set("FrameGen", "Enabled", "true");
            ini.Set("FrameGen", "FGInput", "nvngxfg");
            ini.Set("FrameGen", "FGNvngxReplacement", enablerAvailable ? "combo" : "ffx");
            ini.Set("DLSSG", "InterpolationCount", "2");
        }
        return ini.ToText();
    }
}
