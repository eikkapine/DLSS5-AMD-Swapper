using Dlss5AmdSwapper.Services;

namespace Dlss5AmdSwapper.Models;

public sealed record PeVersion(string? ProductName, string? ProductVersion);

public sealed record LocalWeights(string Path, long Size, string Sha256);

public sealed record OptiScalerPackage(
    string Root,
    string OptiScalerDllPath,
    IReadOnlyList<string> PassDllPaths,
    string? IniPath,
    string? DependencyFolder,
    string? EnablerDllPath,
    string? WeightsPath,
    string? Sha256SumsPath,
    string ForkVersion,
    IReadOnlyDictionary<string, FileState> Files,
    bool Sha256SumsVerified,
    string Layout)
{
    public const string LayoutPackage = "package";
    public const string LayoutVodkaman = "vodkaman";

    public string Summary =>
        $"{ForkVersion} · {Layout} layout · {PassDllPaths.Count} pass DLL(s)" +
        (Sha256SumsPath is null ? " · no SHA256SUMS" : Sha256SumsVerified ? " · SHA256SUMS verified" : " · SHA256SUMS mismatch") +
        (EnablerDllPath is null ? string.Empty : " · enabler present");
}
