namespace Dlss5AmdSwapper.Models;

public enum InstallRoute { None, PostFsrRuntime, OptiScalerPreSr }

public enum OptiScalerPreset { Quality, Performance }

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
        InstallRoute.PostFsrRuntime => "Post-FSR runtime",
        InstallRoute.OptiScalerPreSr => "OptiScaler pre-SR",
        _ => "Not installed"
    };
}
