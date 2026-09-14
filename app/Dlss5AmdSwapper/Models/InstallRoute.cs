namespace Dlss5AmdSwapper.Models;

public enum InstallRoute { None, PostFsrRuntime, OptiScalerPreSr }

public enum OptiScalerPreset { Light, Balanced, Detail, Max }

// Upscale ratios as documented by OptiScaler's [QualityOverrides] section. GameControlled writes
// no override, leaving the game's own upscaler quality setting in charge.
public enum OptiScalerScaling { GameControlled, Dlaa, UltraQuality, Quality, Balanced, Performance, UltraPerformance }

public static class OptiScalerPresets
{
    public static readonly string[] Names = ["Light", "Balanced", "Detail", "Max"];

    // Older manifests and settings recorded only "quality" or "performance". The previous
    // "performance" preset forced a 3.0 ratio, so it maps to Max.
    public static OptiScalerPreset Parse(string? preset) => preset?.Trim().ToLowerInvariant() switch
    {
        "light" => OptiScalerPreset.Light,
        "detail" => OptiScalerPreset.Detail,
        "max" or "performance" => OptiScalerPreset.Max,
        _ => OptiScalerPreset.Balanced
    };

    // Maxing every slider looks worse, not better; tone in particular is kept low.
    public static (int Passes, string Structure, string Skin, string Tone, OptiScalerScaling Scaling) Values(OptiScalerPreset preset) => preset switch
    {
        OptiScalerPreset.Light => (1, "1.0", "1.0", "0", OptiScalerScaling.Quality),
        OptiScalerPreset.Detail => (2, "2.0", "2.0", "0", OptiScalerScaling.Performance),
        OptiScalerPreset.Max => (3, "2.0", "2.0", "0.5", OptiScalerScaling.UltraPerformance),
        _ => (1, "1.5", "1.5", "0", OptiScalerScaling.Balanced)
    };

    // Null when the installed values match no preset, which the UI reports as Custom rather than
    // pretending a preset is selected.
    public static OptiScalerPreset? Match(int passes, double structure, double skin, double tone)
    {
        foreach (var preset in new[] { OptiScalerPreset.Light, OptiScalerPreset.Balanced, OptiScalerPreset.Detail, OptiScalerPreset.Max })
        {
            var v = Values(preset);
            if (v.Passes == passes
                && Math.Abs(double.Parse(v.Structure, System.Globalization.CultureInfo.InvariantCulture) - structure) < 0.05
                && Math.Abs(double.Parse(v.Skin, System.Globalization.CultureInfo.InvariantCulture) - skin) < 0.05
                && Math.Abs(double.Parse(v.Tone, System.Globalization.CultureInfo.InvariantCulture) - tone) < 0.05)
                return preset;
        }
        return null;
    }

    public static string Describe(OptiScalerPreset preset)
    {
        var v = Values(preset);
        return $"{v.Passes} pass(es), structure {v.Structure}, skin {v.Skin}, tone {v.Tone}, {OptiScalerScalings.Label(v.Scaling)} scaling";
    }
}

public static class OptiScalerScalings
{
    public static readonly string[] Names = ["Game controlled", "DLAA", "Ultra Quality", "Quality", "Balanced", "Performance", "Ultra Performance"];

    public static string? Ratio(OptiScalerScaling scaling) => scaling switch
    {
        OptiScalerScaling.Dlaa => "1.0",
        OptiScalerScaling.UltraQuality => "1.3",
        OptiScalerScaling.Quality => "1.5",
        OptiScalerScaling.Balanced => "1.7",
        OptiScalerScaling.Performance => "2.0",
        OptiScalerScaling.UltraPerformance => "3.0",
        _ => null
    };

    public static string Label(OptiScalerScaling scaling) => Names[(int)scaling];

    // Null for a forced ratio that is not one of the documented tiers, so a hand-edited value is
    // shown as Custom instead of being mislabelled "Game controlled".
    public static OptiScalerScaling? FromRatio(bool overrideEnabled, string? ratio) =>
        !overrideEnabled ? OptiScalerScaling.GameControlled : ratio?.Trim() switch
        {
            "1.0" => OptiScalerScaling.Dlaa,
            "1.3" => OptiScalerScaling.UltraQuality,
            "1.5" => OptiScalerScaling.Quality,
            "1.7" => OptiScalerScaling.Balanced,
            "2.0" => OptiScalerScaling.Performance,
            "3.0" => OptiScalerScaling.UltraPerformance,
            _ => null
        };

    public static OptiScalerScaling Parse(string? value) => value?.Trim().ToLowerInvariant().Replace(" ", string.Empty) switch
    {
        "dlaa" => OptiScalerScaling.Dlaa,
        "ultraquality" => OptiScalerScaling.UltraQuality,
        "quality" => OptiScalerScaling.Quality,
        "balanced" => OptiScalerScaling.Balanced,
        "performance" => OptiScalerScaling.Performance,
        "ultraperformance" => OptiScalerScaling.UltraPerformance,
        _ => OptiScalerScaling.GameControlled
    };
}

public static class InstallRoutes
{
    public const string PostFsr = "amd-fsr-direct";
    public const string OptiScalerPreSr = "amd-optiscaler-presr";

    // A manifest without a route field predates route support and belongs to the post-FSR installer.
    public static InstallRoute Parse(string? route) => route switch
    {
        null or "" => InstallRoute.PostFsrRuntime,
        PostFsr => InstallRoute.PostFsrRuntime,
        OptiScalerPreSr => InstallRoute.OptiScalerPreSr,
        _ => InstallRoute.None
    };

    public static string ToManifestString(InstallRoute route) => route switch
    {
        InstallRoute.PostFsrRuntime => PostFsr,
        InstallRoute.OptiScalerPreSr => OptiScalerPreSr,
        _ => string.Empty
    };

    public static string Label(InstallRoute route) => route switch
    {
        InstallRoute.PostFsrRuntime => "Official AMD runtime",
        InstallRoute.OptiScalerPreSr => "OptiScaler pre-SR",
        _ => "Not installed"
    };
}
