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

            using var updatedDoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(f.Game.ManifestPath));
            var preexistingAfterUpdate = updatedDoc.RootElement.GetProperty("preexisting_dependencies").EnumerateArray().Select(e => e.GetString()).ToArray();
            Check(preexistingAfterUpdate.Contains(@"OptiScaler\libxess.dll"), "update must keep tracking the originally preexisting dependency");
            Check(!preexistingAfterUpdate.Contains(@"OptiScaler\amd_fidelityfx_upscaler_dx12.dll"), "update must not relabel an app-installed dependency as preexisting");
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

        await run("Pre-SR failed update restores the previous install and manifest", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var _ = f.Temp;
            await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, update: false);
            var manifestBytes = await File.ReadAllBytesAsync(f.Game.ManifestPath);
            var proxyPath = Path.Combine(f.GameDir, "dxgi.dll");
            var proxyHash = await DirectGameInstallerService.Sha256Async(proxyPath);

            // Swap the INI path for a directory so the update's snapshot still succeeds but the final write fails.
            var iniPath = Path.Combine(f.GameDir, "OptiScaler.ini");
            File.Delete(iniPath);
            Directory.CreateDirectory(iniPath);
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Performance, update: true); throw new InvalidOperationException("accepted"); }
            catch (InvalidOperationException error) when (error.Message == "accepted") { throw; }
            catch (Exception) { }
            Directory.Delete(iniPath);

            Check((await File.ReadAllBytesAsync(f.Game.ManifestPath)).SequenceEqual(manifestBytes), "manifest must be restored byte-for-byte after a failed update");
            Check(File.Exists(proxyPath) && await DirectGameInstallerService.Sha256Async(proxyPath) == proxyHash, "proxy must be restored to its pre-update content");
            foreach (var name in new[] { "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll", "dlssnr_on_amd_weights.bin" })
                Check(File.Exists(Path.Combine(f.GameDir, name)), $"missing {name} after failed-update rollback");
            Check(!f.Game.Busy, "busy flag cleared after failed update");
        });

        await run("Pre-SR update keeps the manifest when the backup phase fails", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var _ = f.Temp;
            await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, update: false);
            var manifestBytes = await File.ReadAllBytesAsync(f.Game.ManifestPath);

            // Point the backup root under a path whose parent is a FILE, so Directory.CreateDirectory
            // fails inside the backup phase, after the manifest bytes are read but before any copy runs.
            var blocker = Path.Combine(f.Temp.Path, "backup-blocker");
            await File.WriteAllBytesAsync(blocker, [1]);
            OptiScalerInstallerService.BackupRootOverride = () => Path.Combine(blocker, Guid.NewGuid().ToString("N"));
            try
            {
                try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Performance, update: true); throw new InvalidOperationException("accepted"); }
                catch (InvalidOperationException error) when (error.Message == "accepted") { throw; }
                catch (Exception) { }
            }
            finally { OptiScalerInstallerService.BackupRootOverride = null; }

            Check((await File.ReadAllBytesAsync(f.Game.ManifestPath)).SequenceEqual(manifestBytes), "manifest must be untouched when the backup phase itself fails");
            Check(ManagedManifest.ReadRoute(f.Game) == InstallRoute.OptiScalerPreSr, "route must still read as pre-SR");
            Check(File.Exists(Path.Combine(f.GameDir, "dxgi.dll")), "proxy must still be present");
            Check(!f.Game.Busy, "busy flag cleared after a backup-phase failure");
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

        await run("Pre-SR restore removes only unchanged managed files and keeps pre-existing dependencies", async () =>
        {
            var f = await MakeInstallFixtureAsync(preexistingLibxess: true);
            using var _ = f.Temp;
            await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false);
            Check(f.Installer.HasManagedInstall(f.Game), "managed after install");
            await File.AppendAllTextAsync(Path.Combine(f.GameDir, "OptiScaler.ini"), "\n; user edit\n");

            var result = await f.Installer.RemoveAsync(f.Game);
            Check(!File.Exists(Path.Combine(f.GameDir, "dxgi.dll")) && !File.Exists(Path.Combine(f.GameDir, "dlssnr_amd_pass3.dll")) && !File.Exists(Path.Combine(f.GameDir, "dlssnr_on_amd_weights.bin")), "managed files removed");
            Check(!File.Exists(Path.Combine(f.GameDir, "OptiScaler", "amd_fidelityfx_upscaler_dx12.dll")), "copied dependency removed");
            Check(File.Exists(Path.Combine(f.GameDir, "OptiScaler", "libxess.dll")), "pre-existing dependency kept");
            Check(File.Exists(Path.Combine(f.GameDir, "OptiScaler.ini")) && result.Preserved.Contains("OptiScaler.ini"), "edited INI preserved");
            Check(result.ManifestRetained && File.Exists(f.Game.ManifestPath), "manifest retained while a created file remains");
            File.Delete(Path.Combine(f.GameDir, "OptiScaler.ini"));
            var second = await f.Installer.RemoveAsync(f.Game);
            Check(!second.ManifestRetained && !File.Exists(f.Game.ManifestPath), "manifest deleted once nothing created remains");
            Check(!f.Installer.HasManagedInstall(f.Game), "not managed after restore");
        });

        await run("Pre-SR restore removes the dependency folder when nothing user-owned remains", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var _ = f.Temp;
            await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false);
            Directory.CreateDirectory(Path.Combine(f.GameDir, "OptiScaler", "empty-sub"));

            var result = await f.Installer.RemoveAsync(f.Game);
            Check(!Directory.Exists(Path.Combine(f.GameDir, "OptiScaler")), "dependency folder removed despite an empty subdirectory");
            Check(!result.ManifestRetained, "manifest not retained when nothing user-owned remains");
        });

        await run("Manifest file maps stay case-insensitive after reload", async () =>
        {
            using var temp = new OptiTemp();
            var manifestPath = Path.Combine(temp.Path, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                before = new Dictionary<string, FileState> { ["OptiScaler\\LibXess.dll"] = new FileState(3, "abc") },
                after = new Dictionary<string, FileState> { ["OptiScaler\\libxess.dll"] = new FileState(3, "abc") }
            }));
            var manifest = await OptiScalerInstallerService.ReadManifestAsync(manifestPath, CancellationToken.None);
            Check(manifest is not null, "manifest must deserialize");
            Check(manifest!.Before.ContainsKey("optiscaler\\libxess.dll"), "Before lookup must be case-insensitive after reload");
            Check(manifest.After.ContainsKey("OPTISCALER\\LIBXESS.DLL"), "After lookup must be case-insensitive after reload");
        });

        await run("Pre-SR diagnostics parse pass counts, resolution, cost and faults", () =>
        {
            var presr = string.Join('\n',
                "HIP runtime: 70260201",
                "HIP adapter: AMD Radeon RX 9070 XT",
                "Initialized independent AMD pass 1",
                "Initialized independent AMD pass 2",
                "AMD pre-SR: waiting for a DirectX 12 SR frame",
                "Completed AMD pre-SR passes=2",
                "HIP completion timeout pass 2");
            var opti = string.Join('\n',
                "[info] DlssNr_Dx12::Dispatch DLSS-NR running before SR: target 3840x2160, model 1280x720, guides 1280x720 (preset 0, intensity 1, style 0, build epoch 3)",
                "[info] DlssNr_Dx12::Dispatch DLSS-NR cost: 12.40 ms total = 10.10 ms model + 2.30 ms ours (19% ours)",
                "[info] DlssNr_Dx12::Dispatch DLSS-NR cost: 11.60 ms total = 9.90 ms model + 1.70 ms ours (15% ours)");
            var result = OptiScalerDiagnosticsService.Parse(presr, opti);
            Check(result.PreSrActive, "active");
            Check(result.HipAdapter == "AMD Radeon RX 9070 XT", "adapter");
            Check(result.PassesInitialized == 2 && result.PassesCompleted == 2, "passes");
            Check(result.ModelSize == "1280x720" && result.TargetSize == "3840x2160", "sizes");
            Check(result.CostSamples == 2 && Math.Abs(result.MeanTotalMs!.Value - 12.0) < 0.001 && Math.Abs(result.MeanModelMs!.Value - 10.0) < 0.001, "cost");
            Check(result.LastFault == "HIP completion timeout pass 2", "last fault");
            Check(result.Summary.Contains("1280x720") && result.Summary.Contains("12.0 ms"), "summary");
            var idle = OptiScalerDiagnosticsService.Parse("AMD pre-SR: idle\n", string.Empty);
            Check(!idle.PreSrActive && idle.LastFault is null, "idle is not a fault");
            var missing = OptiScalerDiagnosticsService.Parse("dlssnr_on_amd_weights.bin is required\n", string.Empty);
            Check(missing.LastFault == "dlssnr_on_amd_weights.bin is required", "weights fault");
            return Task.CompletedTask;
        });

        await run("Pre-SR diagnostics hash the whole log while sampling the tail", async () =>
        {
            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            var content = "Initialized independent AMD pass 1\n" + new string('x', 2 * 1024 * 1024) + "\nCompleted AMD pre-SR passes=1\n";
            await File.WriteAllTextAsync(game.PreSrLogPath, content);
            var result = await new OptiScalerDiagnosticsService().InspectAsync(game);
            Check(result.PreSrActive && result.PassesCompleted == 1, "tail parsed");
            Check(result.PreSrLogBytes == new FileInfo(game.PreSrLogPath).Length && result.PreSrLogSha256?.Length == 64, "hash covers full file");
            Check(result.OptiLogSha256 is null && result.OptiLogBytes == 0, "absent OptiScaler.log tolerated");
        });

        await run("Pre-SR diagnostics tolerate prefixed and drifted log lines", () =>
        {
            var presr = "[12:00:01] HIP adapter: AMD Radeon RX 9070 XT\r\n[12:00:02] AMD engine initialization failed\r\n";
            var opti = "[info] DlssNr_Dx12::Dispatch DLSS-NR running before SR (no sizes)\n";
            var result = OptiScalerDiagnosticsService.Parse(presr, opti);
            Check(result.PreSrActive, "active from opti log");
            Check(result.HipAdapter == "AMD Radeon RX 9070 XT", "adapter from prefixed line");
            Check(result.LastFault?.EndsWith("AMD engine initialization failed", StringComparison.Ordinal) ?? false, "generic fault pattern matches real fork text");
            Check(result.ModelSize is null, "no resolution when running format drifts");
            return Task.CompletedTask;
        });

        await run("Pre-SR diagnostics survive a log that shrinks during inspection", async () =>
        {
            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            var content = "Initialized independent AMD pass 1\n" + new string('x', 3 * 1024 * 1024) + "\nCompleted AMD pre-SR passes=1\n";
            await File.WriteAllTextAsync(game.PreSrLogPath, content);
            var truncationTask = Task.Run(async () =>
            {
                await Task.Delay(5);
                await File.WriteAllBytesAsync(game.PreSrLogPath, new byte[100]);
            });
            try
            {
                var result = await new OptiScalerDiagnosticsService().InspectAsync(game);
                Check(result.PreSrLogBytes >= 0, "call must complete without throwing");
            }
            catch (Exception ex) { throw new InvalidOperationException("InspectAsync must tolerate concurrent truncation", ex); }
            await truncationTask;
        });

        await run("Runtime refresh reads route and pre-SR controls from OptiScaler.ini", async () =>
        {
            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            await File.WriteAllTextAsync(game.ManifestPath, "{\"route\":\"amd-optiscaler-presr\",\"installed_proxy_names\":[\"dxgi.dll\"]}");
            await File.WriteAllBytesAsync(Path.Combine(temp.Path, "dxgi.dll"), [1]);
            await File.WriteAllTextAsync(game.OptiScalerIniPath, "[DlssNr]\nEnabled=true\nPasses=2\nLocalStructure=1.5\nSkinStructure=0.5\nLocalTone=0\n");
            new RuntimeControlService().Refresh(game, false);
            Check(game.Route == InstallRoute.OptiScalerPreSr && game.IsPreSr, "route");
            Check(game.Installed && game.Enabled && game.Passes == 2, "installed/enabled/passes");
            Check(Math.Abs(game.LocalStructure - 1.5) < 0.001 && Math.Abs(game.SkinStructure - 0.5) < 0.001 && game.LocalTone == 0, "layers");
            Check(game.RouteLabel == "OptiScaler pre-SR", "label");

            var control = new OptiScalerControlService();
            await control.SetPassesAsync(game, 3);
            await control.SetEnabledAsync(game, false);
            await control.SetSkinAsync(game, 2.0);
            var ini = IniDocument.Load(game.OptiScalerIniPath);
            Check(ini.Get("DlssNr", "Passes") == "3" && ini.Get("DlssNr", "Enabled") == "false" && ini.Get("DlssNr", "SkinStructure") == "2.0", "control writes");
            try { await control.SetPassesAsync(game, 4); throw new Exception("accepted"); }
            catch (ArgumentOutOfRangeException) { }
        });

        await run("Layer API edits an arbitrary runtime INI and supports skin follow", async () =>
        {
            using var temp = new OptiTemp();
            var ini = Path.Combine(temp.Path, "dlssnr_on_amd.ini");
            await File.WriteAllTextAsync(ini, "[DlssNrOnAmd]\nEnabled=1\nLocalStructure=1\nLocalTone=0\nSkinStructure=-1\n");
            var service = new RuntimeControlService();
            var state = service.ReadLayers(ini);
            Check(state.SkinFollowsStructure && state.Structure == 1 && state.Tone == 0, "read");
            await service.SetLayerAsync(ini, "SkinStructure", 1.2);
            await service.AdjustLayerAsync(ini, "LocalTone", 0.3);
            state = service.ReadLayers(ini);
            Check(!state.SkinFollowsStructure && Math.Abs(state.Skin - 1.2) < 0.001 && Math.Abs(state.Tone - 0.3) < 0.001, "write");
            await service.SetLayerAsync(ini, "SkinStructure", -1);
            Check(service.ReadLayers(ini).SkinFollowsStructure, "follow restored");
            try { await service.SetLayerAsync(ini, "Enabled", 1); throw new Exception("accepted"); }
            catch (ArgumentException) { }

            await File.WriteAllTextAsync(ini, "[DlssNrOnAmd]\nLocalStructure=1.4\nSkinStructure=-1\n");
            state = service.ReadLayers(ini);
            Check(Math.Abs(state.Skin - 1.4) < 0.001 && state.SkinFollowsStructure, "skin follows structure value");
            await service.AdjustLayerAsync(ini, "SkinStructure", 0.1);
            state = service.ReadLayers(ini);
            Check(Math.Abs(state.Skin - 1.5) < 0.001 && !state.SkinFollowsStructure, "adjust steps from structure");
        });

        await run("Pre-SR control writes structure and tone and reports next-launch message", async () =>
        {
            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            await File.WriteAllTextAsync(game.ManifestPath, "{\"route\":\"amd-optiscaler-presr\",\"installed_proxy_names\":[\"dxgi.dll\"]}");
            await File.WriteAllBytesAsync(Path.Combine(temp.Path, "dxgi.dll"), [1]);
            await File.WriteAllTextAsync(game.OptiScalerIniPath, "[DlssNr]\nEnabled=true\n");

            var control = new OptiScalerControlService();
            var structureResult = await control.SetStructureAsync(game, 1.7);
            var toneResult = await control.SetToneAsync(game, 0.4);

            var ini = IniDocument.Load(game.OptiScalerIniPath);
            Check(ini.Get("DlssNr", "LocalStructure") == "1.7" && ini.Get("DlssNr", "LocalTone") == "0.4", "INI structure and tone");
            Check(structureResult.Message == "Saved for the next launch" && toneResult.Message == "Saved for the next launch", "next-launch message");
        });

        await run("Hotkey sets map to distinct virtual keys", () =>
        {
            var direct = HotkeyService.Bindings(HotkeySet.DirectGame);
            var layers = HotkeyService.Bindings(HotkeySet.LosslessLayers);
            Check(direct.Select(b => b.VirtualKey).SequenceEqual([0x75u, 0x76u, 0x77u]), "F6-F8");
            Check(layers.Select(b => b.VirtualKey).SequenceEqual([0x78u, 0x79u, 0x7Au]), "F9-F11");
            Check(layers.Select(b => b.Key).SequenceEqual([SwapperHotkey.CycleLayer, SwapperHotkey.LayerDecrease, SwapperHotkey.LayerIncrease]), "layer keys");
            Check(HotkeyService.Describe(HotkeySet.LosslessLayers) == "F9/F10/F11", "describe");
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
