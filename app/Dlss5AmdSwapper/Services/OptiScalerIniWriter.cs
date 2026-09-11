using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public static class OptiScalerIniWriter
{
    private const string MinimalHeader = "; Written by DLSS5 AMD Swapper. Unlisted OptiScaler keys keep their defaults.\n; Open the in-game OptiScaler menu (Insert) to change anything else.\n";

    public static bool RequiresEnabler(OptiScalerPreset preset) => preset == OptiScalerPreset.Performance;

    public static string Build(string? baseIniText, OptiScalerPreset preset, bool enablerAvailable)
    {
        var ini = IniDocument.FromText(string.IsNullOrWhiteSpace(baseIniText) ? MinimalHeader : baseIniText);
        ini.Set("Upscalers", "Dx12Upscaler", "ffx");
        ini.Set("DlssNr", "Enabled", "true");
        ini.Set("DlssNr", "RunBeforeSR", "true");
        ini.Set("DlssNr", "Passes", "1");
        ini.Set("DlssNr", "LocalTone", "0");
        ini.Set("DlssNr", "LocalStructure", "1");
        ini.Set("DlssNr", "SkinStructure", "1");
        ini.Set("DlssNr", "ApplyAfterRR", "false");
        ini.Set("Log", "LogToFile", "true");
        ini.Set("Log", "LogLevel", "2");
        if (preset == OptiScalerPreset.Performance)
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
