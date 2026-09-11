using System.Security.AccessControl;
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

        await run("Discovery finds package folders, zips and Vodkaman folders under search roots", () =>
        {
            using var temp = new OptiTemp();
            var downloads = Path.Combine(temp.Path, "Downloads");
            MakePackage(downloads, layout: "package", withSums: false);
            var nested = Path.Combine(downloads, "Arquivos necessarios");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(nested, "OptiScaler-AMD-PreSR-Multipass-v1.3.zip"), [0x50, 0x4B]);
            var vodka = Path.Combine(downloads, "DLSS-NR-UE5-Opti-DLL");
            Directory.CreateDirectory(vodka);
            File.WriteAllBytes(Path.Combine(vodka, "dxgi.dll"), [1]);
            File.WriteAllBytes(Path.Combine(vodka, "dlssnr_amd_pass1.dll"), [1]);
            var configured = Path.Combine(temp.Path, "Configured");
            Directory.CreateDirectory(configured);
            var found = new OptiScalerPackageService().DiscoverCandidates(configured, [downloads]);
            Check(found.Count == 4, $"expected 4 candidates, got {found.Count}");
            Check(found[0] == configured, "configured path first");
            Check(found.Any(path => path.EndsWith("OptiScaler-AMD-PreSR-Multipass-v1.2", StringComparison.Ordinal)), "package folder");
            Check(found.Any(path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)), "zip");
            Check(found.Any(path => path.EndsWith("DLSS-NR-UE5-Opti-DLL", StringComparison.Ordinal)), "vodkaman folder");
            return Task.CompletedTask;
        });

        await run("Zip extraction is cached by hash and rejects escaping entries", async () =>
        {
            using var temp = new OptiTemp();
            var zip = Path.Combine(temp.Path, "OptiScaler-AMD-PreSR-Multipass-v1.2.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("OptiScaler-AMD-PreSR-Multipass-v1.2/OptiScaler.ini");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("[DlssNr]\nEnabled=auto\n");
            }
            var cache = Path.Combine(temp.Path, "cache");
            var service = new OptiScalerPackageService();
            var first = service.EnsureExtracted(zip, cache);
            var second = service.EnsureExtracted(zip, cache);
            Check(first == second, "extraction must be cached");
            Check(File.Exists(Path.Combine(first, "OptiScaler.ini")), "inner folder must be unwrapped");

            var evil = Path.Combine(temp.Path, "evil.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
                archive.CreateEntry("../escape.txt");
            try { service.EnsureExtracted(evil, cache); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("outside"), "zip-slip must be refused"); }
        });

        await run("Local weights come from generated copies and must agree", async () =>
        {
            using var temp = new OptiTemp();
            var ls = Path.Combine(temp.Path, "Lossless Scaling");
            var runtime = Path.Combine(ls, "nr-bridge", "runtime");
            var game = Path.Combine(temp.Path, "Game");
            Directory.CreateDirectory(runtime); Directory.CreateDirectory(game);
            var payload = new byte[1024 * 1024 + 7];
            payload[3] = 9;
            await File.WriteAllBytesAsync(Path.Combine(runtime, "dlssnr_on_amd_weights.bin"), payload);
            await File.WriteAllBytesAsync(Path.Combine(game, "dlssnr_on_amd_weights.bin"), payload);
            var weights = OptiScalerPackageService.FindLocalWeights(null, [game], ls);
            Check(weights is not null && weights.Size == payload.Length, "weights not found");
            Check(weights!.Path == Path.Combine(runtime, "dlssnr_on_amd_weights.bin"), "bridge runtime copy must win");
            payload[5] = 1;
            await File.WriteAllBytesAsync(Path.Combine(game, "dlssnr_on_amd_weights.bin"), payload);
            try { OptiScalerPackageService.FindLocalWeights(null, [game], ls); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("differ"), "disagreeing copies must fail"); }
            Check(OptiScalerPackageService.FindLocalWeights(null, [], Path.Combine(temp.Path, "nowhere")) is null, "no candidates must be null");
        });

        await run("Discovery skips unreadable folders and finds packages", () =>
        {
            using var temp = new OptiTemp();
            var root = Path.Combine(temp.Path, "Search");
            Directory.CreateDirectory(root);
            var unreadable = Path.Combine(root, "Locked");
            Directory.CreateDirectory(unreadable);
            var found = MakePackage(root, layout: "package", withSums: false);

            var security = new DirectorySecurity(unreadable, AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
                FileSystemRights.ListDirectory | FileSystemRights.ReadData,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Deny));
            try
            {
                System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(unreadable), security);
                var results = new OptiScalerPackageService().DiscoverCandidates(null, [root]);
                Check(results.Any(p => p.EndsWith("OptiScaler-AMD-PreSR-Multipass-v1.2", StringComparison.Ordinal)), "package folder found despite unreadable sibling");
            }
            finally
            {
                var restore = new DirectorySecurity();
                restore.SetAccessRuleProtection(false, false);
                System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(unreadable), restore);
            }
            return Task.CompletedTask;
        });

        await run("Preset INI writer patches a package INI in place and writes a minimal INI otherwise", () =>
        {
            var baseIni = "; comment stays\n[Upscalers]\nDx12Upscaler=auto\n\n[DlssNr]\nEnabled=auto\nPasses=auto\n\n[Log]\nLogToFile=auto\n";
            var quality = IniDocument.FromText(OptiScalerIniWriter.Build(baseIni, OptiScalerPreset.Quality, enablerAvailable: false));
            Check(quality.Get("Upscalers", "Dx12Upscaler") == "ffx", "Dx12Upscaler");
            Check(quality.Get("DlssNr", "Enabled") == "true" && quality.Get("DlssNr", "RunBeforeSR") == "true" && quality.Get("DlssNr", "Passes") == "1", "DlssNr core keys");
            Check(quality.Get("DlssNr", "LocalTone") == "0" && quality.Get("DlssNr", "LocalStructure") == "1" && quality.Get("DlssNr", "SkinStructure") == "1" && quality.Get("DlssNr", "ApplyAfterRR") == "false", "DlssNr layer keys");
            Check(quality.Get("Log", "LogToFile") == "true" && quality.Get("Log", "LogLevel") == "2", "Log keys");
            Check(quality.Get("FrameGen", "Enabled") is null && quality.Get("UpscaleRatio", "UpscaleRatioOverrideEnabled") is null, "Quality must not touch FG or ratio");
            Check(OptiScalerIniWriter.Build(baseIni, OptiScalerPreset.Quality, false).StartsWith("; comment stays", StringComparison.Ordinal), "comments preserved");

            var perf = IniDocument.FromText(OptiScalerIniWriter.Build(null, OptiScalerPreset.Performance, enablerAvailable: true));
            Check(perf.Get("UpscaleRatio", "UpscaleRatioOverrideEnabled") == "true" && perf.Get("UpscaleRatio", "UpscaleRatioOverrideValue") == "3.0", "ratio override");
            Check(perf.Get("FrameGen", "Enabled") == "true" && perf.Get("FrameGen", "FGInput") == "nvngxfg" && perf.Get("FrameGen", "FGNvngxReplacement") == "combo", "FG combo");
            Check(perf.Get("DLSSG", "InterpolationCount") == "2", "3x interpolation");
            var perfNoEnabler = IniDocument.FromText(OptiScalerIniWriter.Build(null, OptiScalerPreset.Performance, enablerAvailable: false));
            Check(perfNoEnabler.Get("FrameGen", "FGNvngxReplacement") == "ffx", "ffx without enabler");
            Check(perfNoEnabler.Get("DlssNr", "RunBeforeSR") == "true", "minimal INI still carries DlssNr");
            return Task.CompletedTask;
        });

        await run("Pre-SR install copies package, patches INI, records manifest and skips identical dependencies", async () =>
        {
            var f = await MakeInstallFixtureAsync(preexistingLibxess: true);
            using var _ = f.Temp;
            var result = await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, update: false);
            Check(result.Success && result.ProxyName == "dxgi.dll", "install result");
            foreach (var name in new[] { "dxgi.dll", "OptiScaler.ini", "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll", "dlssnr_on_amd_weights.bin", "OptiScaler\\amd_fidelityfx_upscaler_dx12.dll" })
                Check(File.Exists(Path.Combine(f.GameDir, name)), $"missing {name}");
            Check(IniDocument.Load(Path.Combine(f.GameDir, "OptiScaler.ini")).Get("DlssNr", "RunBeforeSR") == "true", "INI patched");
            Check(ManagedManifest.ReadRoute(f.Game) == InstallRoute.OptiScalerPreSr, "manifest route");
            var manifestText = await File.ReadAllTextAsync(f.Game.ManifestPath);
            Check(manifestText.Contains("\"schema_version\": 3") && manifestText.Contains("\"preset\": \"quality\""), "manifest content");
            Check(manifestText.Contains("OptiScaler\\\\libxess.dll") && manifestText.Contains("\"preexisting_dependencies\""), "preexisting dependency recorded");
            Check(f.Game.Status == "Installed" && !f.Game.Busy, "status");

            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, update: false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("Update"), "second install must ask for Update"); }

            var updated = await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Performance, update: true);
            Check(updated.Preset == OptiScalerPreset.Performance, "update preset");
            Check(IniDocument.Load(Path.Combine(f.GameDir, "OptiScaler.ini")).Get("UpscaleRatio", "UpscaleRatioOverrideValue") == "3.0", "update rewrote INI");
        });

        await run("Pre-SR install refuses unmanaged proxies, anti-cheat and running games", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var _ = f.Temp;
            await File.WriteAllBytesAsync(Path.Combine(f.GameDir, "winmm.dll"), [1]);
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("winmm.dll"), "unmanaged proxy must be named"); }
            File.Delete(Path.Combine(f.GameDir, "winmm.dll"));

            await File.WriteAllBytesAsync(Path.Combine(f.GameDir, "EasyAntiCheat.dll"), [1]);
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("Anti-cheat"), "anti-cheat must block"); }
            File.Delete(Path.Combine(f.GameDir, "EasyAntiCheat.dll"));

            f.Game.Running = true;
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("Close the game"), "running game must block"); }
            Check(!File.Exists(Path.Combine(f.GameDir, "dxgi.dll")) && !File.Exists(f.Game.ManifestPath), "refusals must leave the folder untouched");
        });

        await run("Pre-SR install rolls back exactly when a write fails", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var _ = f.Temp;
            // A directory at the INI path makes the INI write fail after the binaries were copied.
            Directory.CreateDirectory(Path.Combine(f.GameDir, "OptiScaler.ini"));
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false); throw new InvalidOperationException("accepted"); }
            catch (InvalidOperationException error) when (error.Message == "accepted") { throw; }
            catch (Exception) { }
            Check(!File.Exists(Path.Combine(f.GameDir, "dxgi.dll")) && !File.Exists(Path.Combine(f.GameDir, "dlssnr_amd_pass1.dll")) && !File.Exists(Path.Combine(f.GameDir, "dlssnr_on_amd_weights.bin")), "copied files must be rolled back");
            Check(!File.Exists(Path.Combine(f.GameDir, "OptiScaler", "libxess.dll")), "dependency copies must be rolled back");
            Check(!File.Exists(f.Game.ManifestPath), "manifest must not remain");
            Check(!f.Game.Busy, "busy flag cleared");
        });

        await run("Pre-SR install removes a managed post-FSR route first and records it", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var _ = f.Temp;
            var oldProxy = Path.Combine(f.GameDir, "winmm.dll");
            var oldIni = Path.Combine(f.GameDir, "dlssnr_on_amd.ini");
            await File.WriteAllTextAsync(oldProxy, "old proxy");
            await File.WriteAllTextAsync(oldIni, "[DlssNrOnAmd]\nEnabled=1\n");
            await File.WriteAllTextAsync(f.Game.ManifestPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                schema_version = 2, route = "amd-fsr-direct",
                before = new Dictionary<string, FileState>(),
                after = new Dictionary<string, FileState>
                {
                    ["winmm.dll"] = new(new FileInfo(oldProxy).Length, await DirectGameInstallerService.Sha256Async(oldProxy)),
                    ["dlssnr_on_amd.ini"] = new(new FileInfo(oldIni).Length, await DirectGameInstallerService.Sha256Async(oldIni))
                },
                installed_proxy_names = new[] { "winmm.dll" }
            }));
            var result = await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false);
            Check(result.PreviousRouteRemoval is not null && result.PreviousRouteRemoval.Removed.Contains("winmm.dll"), "old proxy removed");
            Check(!File.Exists(oldProxy) && !File.Exists(oldIni) && File.Exists(Path.Combine(f.GameDir, "dxgi.dll")), "old route gone, new route present");
            Check((await File.ReadAllTextAsync(f.Game.ManifestPath)).Contains("\"previous_route\""), "previous route recorded");
            Check(!f.Legacy.HasManagedInstall(f.Game), "legacy installer must not claim a pre-SR game");
            try { await f.Legacy.RemoveAsync(f.Game, false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("pre-SR"), "legacy remove must refuse other routes"); }
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

    internal sealed record InstallFixture(OptiTemp Temp, OptiScalerPackage Package, string GameDir, GameEntry Game, LocalWeights Weights, OptiScalerInstallerService Installer, DirectGameInstallerService Legacy);

    internal static async Task<InstallFixture> MakeInstallFixtureAsync(bool preexistingLibxess = false)
    {
        var temp = new OptiTemp();
        var root = MakePackage(temp.Path, layout: "package", withSums: false);
        var package = OptiScalerPackageService.Validate(root, FakeFork);
        var gameDir = Path.Combine(temp.Path, "Game");
        Directory.CreateDirectory(gameDir);
        var exe = WritePe(Path.Combine(gameDir, "FixtureGame.exe"), "d3d12.dll");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll"), [1]);
        if (preexistingLibxess)
        {
            Directory.CreateDirectory(Path.Combine(gameDir, "OptiScaler"));
            File.Copy(Path.Combine(root, "OptiScaler", "libxess.dll"), Path.Combine(gameDir, "OptiScaler", "libxess.dll"));
        }
        var weightsPath = Path.Combine(temp.Path, "weights.bin");
        await File.WriteAllBytesAsync(weightsPath, new byte[1024 * 1024 + 3]);
        var weights = new LocalWeights(weightsPath, new FileInfo(weightsPath).Length, await DirectGameInstallerService.Sha256Async(weightsPath));
        var probe = new GameProbeService();
        var legacy = new DirectGameInstallerService(probe);
        return new InstallFixture(temp, package, gameDir, new GameEntry { Name = "Fixture", ExePath = exe }, weights, new OptiScalerInstallerService(probe, legacy), legacy);
    }
}
