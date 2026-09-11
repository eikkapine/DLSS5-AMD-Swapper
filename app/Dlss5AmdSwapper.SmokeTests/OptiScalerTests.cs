using System.Text;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

internal static class OptiScalerTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Manifest route reader distinguishes routes", async () =>
        {
            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            Check(ManagedManifest.ReadRoute(game) == InstallRoute.None, "Missing manifest must be None.");
            await File.WriteAllTextAsync(game.ManifestPath, "{\"route\":\"amd-optiscaler-presr\"}");
            Check(ManagedManifest.ReadRoute(game) == InstallRoute.OptiScalerPreSr, "Pre-SR route not read.");
            await File.WriteAllTextAsync(game.ManifestPath, "{\"schema_version\":2,\"route\":\"amd-fsr-direct\"}");
            Check(ManagedManifest.ReadRoute(game) == InstallRoute.PostFsrRuntime, "Post-FSR route not read.");
            await File.WriteAllTextAsync(game.ManifestPath, "{\"schema_version\":1}");
            Check(ManagedManifest.ReadRoute(game) == InstallRoute.PostFsrRuntime, "Legacy manifest without route must be post-FSR.");
            await File.WriteAllTextAsync(game.ManifestPath, "not json");
            Check(ManagedManifest.ReadRoute(game) == InstallRoute.None, "Corrupt manifest must be None.");
        });

        await run("Route strings round-trip", () =>
        {
            Check(InstallRoutes.Parse("amd-optiscaler-presr") == InstallRoute.OptiScalerPreSr, "parse presr");
            Check(InstallRoutes.Parse("amd-fsr-direct") == InstallRoute.PostFsrRuntime, "parse post-fsr");
            Check(InstallRoutes.Parse(null) == InstallRoute.PostFsrRuntime, "null means legacy post-FSR");
            Check(InstallRoutes.Parse("other") == InstallRoute.None, "unknown is None");
            Check(InstallRoutes.ToManifestString(InstallRoute.OptiScalerPreSr) == "amd-optiscaler-presr", "to string");
            return Task.CompletedTask;
        });
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed class OptiTemp : IDisposable
    {
        public OptiTemp()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dlss5-amd-swapper-opti", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    // Copies the running test executable (a real x64 PE) and appends a marker so
    // PE and marker checks pass without shipping any third-party binary.
    internal static string WritePe(string path, string? appendMarker = null)
    {
        File.Copy(Environment.ProcessPath ?? throw new InvalidOperationException("No process path."), path, true);
        if (appendMarker is not null)
        {
            using var stream = File.Open(path, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(Encoding.ASCII.GetBytes(appendMarker));
        }
        return path;
    }
}
