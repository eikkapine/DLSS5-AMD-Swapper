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

        await run("Package validation accepts the package layout and verifies SHA256SUMS", async () =>
        {
            using var temp = new OptiTemp();
            var root = MakePackage(temp.Path, layout: "package", withSums: true);
            var package = OptiScalerPackageService.Validate(root, FakeFork);
            Check(package.Layout == "package", "layout");
            Check(package.PassDllPaths.Count == 3, "three pass DLLs expected");
            Check(package.Sha256SumsVerified, "SHA256SUMS should verify");
            Check(package.WeightsPath is null, "LFS pointer must not count as weights");
            Check(package.DependencyFolder is not null && Directory.Exists(package.DependencyFolder), "dependency folder");
            Check(package.EnablerDllPath is null, "no enabler in fixture");
            Check(package.Files.ContainsKey("dlssnr_amd_pass1.dll"), "files recorded");
            Check(package.ForkVersion.Contains("amd-presr"), "fork version recorded");
        });

        await run("Package validation accepts the Vodkaman layout", () =>
        {
            using var temp = new OptiTemp();
            WritePe(Path.Combine(temp.Path, "dxgi.dll"));
            WritePe(Path.Combine(temp.Path, "dlssnr_amd_pass1.dll"), "dlssnr_amd");
            var package = OptiScalerPackageService.Validate(temp.Path, FakeFork);
            Check(package.Layout == "vodkaman", "layout");
            Check(Path.GetFileName(package.OptiScalerDllPath) == "dxgi.dll", "dxgi.dll is the fork binary");
            Check(package.PassDllPaths.Count == 1, "single pass DLL");
            Check(package.IniPath is null && package.DependencyFolder is null, "optional parts absent");
            return Task.CompletedTask;
        });

        await run("Package validation rejects a non-fork OptiScaler build", () =>
        {
            using var temp = new OptiTemp();
            var root = MakePackage(temp.Path, layout: "package", withSums: false);
            try { OptiScalerPackageService.Validate(root, _ => new PeVersion("OptiScaler", "10.0.0-dev (792f2f1)")); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("amd-presr"), "reason must name amd-presr"); }
            return Task.CompletedTask;
        });

        await run("Package validation rejects a SHA256SUMS mismatch and a pass DLL without marker", async () =>
        {
            using var temp = new OptiTemp();
            var root = MakePackage(temp.Path, layout: "package", withSums: true);
            await File.AppendAllTextAsync(Path.Combine(root, "OptiScaler.ini"), "\n; tampered\n");
            try { OptiScalerPackageService.Validate(root, FakeFork); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("OptiScaler.ini"), "mismatch must name the file"); }

            using var temp2 = new OptiTemp();
            WritePe(Path.Combine(temp2.Path, "OptiScaler.dll"));
            WritePe(Path.Combine(temp2.Path, "dlssnr_amd_pass1.dll"));
            try { OptiScalerPackageService.Validate(temp2.Path, FakeFork); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("dlssnr_amd_pass1.dll"), "marker failure must name pass 1"); }
        });

        await run("Weights detection rejects LFS pointers and small files", async () =>
        {
            using var temp = new OptiTemp();
            var pointer = Path.Combine(temp.Path, "dlssnr_on_amd_weights.bin");
            await File.WriteAllTextAsync(pointer, "version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 1\n");
            Check(!OptiScalerPackageService.IsRealWeightsFile(pointer), "LFS pointer accepted");
            await File.WriteAllBytesAsync(pointer, new byte[1024 * 1024 + 1]);
            Check(OptiScalerPackageService.IsRealWeightsFile(pointer), "large binary rejected");
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

    internal static readonly Func<string, PeVersion> FakeFork = _ => new PeVersion("OptiScaler", "10.0.0-dev (amd-presr-multipass-local) (20260907_075847)");

    internal static string MakePackage(string parent, string layout, bool withSums)
    {
        var root = Path.Combine(parent, "OptiScaler-AMD-PreSR-Multipass-v1.2");
        Directory.CreateDirectory(Path.Combine(root, "OptiScaler"));
        WritePe(Path.Combine(root, "OptiScaler.dll"));
        for (var i = 1; i <= 3; i++) WritePe(Path.Combine(root, $"dlssnr_amd_pass{i}.dll"), "dlssnr_amd");
        File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Upscalers]\nDx12Upscaler=auto\n\n[DlssNr]\nEnabled=auto\n");
        File.WriteAllText(Path.Combine(root, "dlssnr_on_amd_weights.bin"), "version https://git-lfs.github.com/spec/v1\noid sha256:6bf8\nsize 147689451\n");
        File.WriteAllBytes(Path.Combine(root, "OptiScaler", "amd_fidelityfx_upscaler_dx12.dll"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "OptiScaler", "libxess.dll"), [4, 5, 6]);
        if (withSums)
        {
            var lines = new List<string>();
            foreach (var relative in new[] { "OptiScaler.dll", "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll", "OptiScaler.ini", "OptiScaler\\amd_fidelityfx_upscaler_dx12.dll", "OptiScaler\\libxess.dll", "MISSING_OPTIONAL.txt" })
            {
                var full = Path.Combine(root, relative);
                var hash = File.Exists(full) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full))) : new string('0', 64);
                lines.Add($"{hash} *{relative}");
            }
            File.WriteAllLines(Path.Combine(root, "SHA256SUMS.txt"), lines);
        }
        return root;
    }
}
