using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dlss5AmdSwapper.Models;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Dlss5AmdSwapper.SmokeTests")]

namespace Dlss5AmdSwapper.Services;

public sealed class OptiScalerInstallerService(GameProbeService probe, DirectGameInstallerService postFsr)
{
    public static readonly string[] ProxyNames = ["dxgi.dll", "version.dll", "winmm.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll"];
    public static readonly string[] RootManagedNames = ["OptiScaler.ini", "OptiScaler.log", "amd_presr.log", "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll", "dlssnr_on_amd_weights.bin"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveFolders = new(StringComparer.OrdinalIgnoreCase);

    // Test-only hook: when set, overrides where the pre-operation backup is staged, so tests can force the
    // backup phase itself to fail (e.g. by pointing it under a path whose parent is a file). Null in production.
    internal static Func<string>? BackupRootOverride;

    public async Task<OptiScalerInstallResult> InstallAsync(GameEntry game, OptiScalerPackage package, LocalWeights weights, OptiScalerPreset preset, bool update, string proxyName = "dxgi.dll", CancellationToken cancellationToken = default)
    {
        if (!ProxyNames.Contains(proxyName, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unsupported proxy name.", nameof(proxyName));
        var folder = Path.GetFullPath(game.DirectoryPath);
        if (game.Busy || !ActiveFolders.TryAdd(folder, 0)) throw new InvalidOperationException("An operation is already running for this game folder.");
        game.Busy = true;
        game.Status = update ? "Updating" : "Installing";
        try
        {
            if (game.Running) throw new InvalidOperationException("Close the game before installing or updating Neural Rendering.");
            var compatibility = await Task.Run(() => probe.Probe(game.ExePath, cancellationToken), cancellationToken);
            if (!compatibility.X64) throw new InvalidOperationException("Direct-game AMD support requires a 64-bit game executable.");
            if (compatibility.AntiCheatMarkers.Count > 0) throw new InvalidOperationException("Anti-cheat markers were found. Direct-game installation is blocked for this target.");
            if (compatibility.FsrMarkers.Count == 0) throw new InvalidOperationException("No supported FSR runtime marker was found near this game.");
            if (compatibility.Dx12Evidence.Count == 0) throw new InvalidOperationException("No DirectX 12 evidence was found near this game.");
            if (!OptiScalerPackageService.IsRealWeightsFile(weights.Path)) throw new InvalidOperationException("The selected weights file is not a generated dlssnr_on_amd_weights.bin.");

            var packageUpscaler = package.DependencyFolder is null ? null : Path.Combine(package.DependencyFolder, OptiScalerPackageService.RequiredUpscalerDependency);
            if (!(packageUpscaler is not null && File.Exists(packageUpscaler)) && !File.Exists(Path.Combine(folder, OptiScalerPackageService.RequiredUpscalerDependency)))
                throw new InvalidOperationException($"{OptiScalerPackageService.RequiredUpscalerDependency} is missing from both the package and the game folder; Dx12Upscaler=ffx cannot work.");

            var existingRoute = ManagedManifest.ReadRoute(game);
            RemoveResult? previousRemoval = null;
            OptiScalerManifest? previousManifest = null;
            if (existingRoute == InstallRoute.PostFsrRuntime)
            {
                if (update) throw new InvalidOperationException("This game has the post-FSR route installed; set it up as a new pre-SR install instead of updating.");
                game.Busy = false; // the post-FSR installer owns the busy flag while it runs
                try { previousRemoval = await postFsr.RemoveAsync(game, false, cancellationToken); }
                finally { game.Busy = true; game.Status = "Installing"; }
                if (ManagedManifest.ReadRoute(game) != InstallRoute.None)
                    throw new InvalidOperationException("The post-FSR route left changed files behind. Review them, then set up again.");
            }
            else if (existingRoute == InstallRoute.OptiScalerPreSr)
            {
                if (!update) throw new InvalidOperationException("This game already has the pre-SR route. Use Update instead.");
                previousManifest = await ReadManifestAsync(game.ManifestPath, cancellationToken) ?? throw new InvalidOperationException("The managed install manifest could not be read.");
            }
            else if (update) throw new InvalidOperationException("Update requires an existing managed install.");

            var dependencyRelatives = package.DependencyFolder is null ? [] : Directory.EnumerateFiles(package.DependencyFolder, "*", SearchOption.AllDirectories)
                .Select(file => Path.Combine(OptiScalerPackageService.DependencyFolderName, Path.GetRelativePath(package.DependencyFolder, file))).ToArray();
            var enablerRelative = Path.Combine(OptiScalerPackageService.DependencyFolderName, OptiScalerPackageService.EnablerName);
            var managed = ProxyNames.Concat(RootManagedNames).Concat(dependencyRelatives).Append(enablerRelative)
                .Concat(previousManifest?.After.Keys ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var before = await SnapshotAsync(folder, managed, cancellationToken);

            if (!update)
            {
                var unmanaged = ProxyNames.Where(before.ContainsKey).ToArray();
                if (unmanaged.Length > 0) throw new InvalidOperationException("A proxy DLL already exists and is not managed by this app: " + string.Join(", ", unmanaged));
            }
            else if (previousManifest is not null && !previousManifest.InstalledProxyNames.Contains(proxyName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Update must keep the proxy name recorded in the manifest.");

            var backupRoot = BackupRootOverride?.Invoke() ?? Path.Combine(Path.GetTempPath(), "dlss5-amd-swapper", Guid.NewGuid().ToString("N"));
            byte[]? previousManifestBytes = null;
            var originalBefore = previousManifest?.Before ?? before;

            var written = new List<string>();
            var preexisting = new List<string>();
            var keepBackup = false;
            try
            {
                // Read the previous manifest bytes before anything that can fail (backup directory
                // creation, the backup copy loop), so a mid-backup failure can still restore it in the
                // catch below instead of falling through to the "no previous manifest" delete branch.
                previousManifestBytes = File.Exists(game.ManifestPath) ? await File.ReadAllBytesAsync(game.ManifestPath, cancellationToken) : null;

                Directory.CreateDirectory(backupRoot);
                foreach (var relative in before.Keys)
                {
                    var target = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(folder, relative), target, true);
                }

                async Task CopyVerifiedAsync(string source, string relative)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.Combine(folder, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, true);
                    if (!string.Equals(await DirectGameInstallerService.Sha256Async(source, cancellationToken), await DirectGameInstallerService.Sha256Async(destination, cancellationToken), StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Copy verification failed for {relative}.");
                    written.Add(relative);
                }

                await CopyVerifiedAsync(package.OptiScalerDllPath, proxyName);
                foreach (var passName in OptiScalerPackageService.PassNames)
                {
                    var match = package.PassDllPaths.FirstOrDefault(path => string.Equals(Path.GetFileName(path), passName, StringComparison.OrdinalIgnoreCase));
                    await CopyVerifiedAsync(match ?? package.PassDllPaths[0], passName);
                }
                await CopyVerifiedAsync(weights.Path, OptiScalerPackageService.WeightsName);

                foreach (var relative in dependencyRelatives)
                {
                    var source = Path.Combine(package.Root, relative);
                    if (before.TryGetValue(relative, out var existing)
                        && existing.Sha256.Equals(await DirectGameInstallerService.Sha256Async(source, cancellationToken), StringComparison.OrdinalIgnoreCase))
                    {
                        if (originalBefore.ContainsKey(relative)) preexisting.Add(relative);
                        continue;
                    }
                    await CopyVerifiedAsync(source, relative);
                }
                var enablerAvailable = package.EnablerDllPath is not null;
                if (OptiScalerIniWriter.RequiresEnabler(preset) && enablerAvailable)
                    await CopyVerifiedAsync(package.EnablerDllPath!, enablerRelative);

                var baseIni = package.IniPath is null ? null : await File.ReadAllTextAsync(package.IniPath, cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(folder, "OptiScaler.ini"), OptiScalerIniWriter.Build(baseIni, preset, enablerAvailable), new UTF8Encoding(false), cancellationToken);
                written.Add("OptiScaler.ini");

                var after = await SnapshotAsync(folder, managed, cancellationToken);
                var manifest = new OptiScalerManifest
                {
                    SchemaVersion = 3,
                    CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Route = InstallRoutes.OptiScalerPreSr,
                    GameExe = Path.GetFileName(game.ExePath),
                    ProxyName = proxyName,
                    Preset = preset.ToString().ToLowerInvariant(),
                    Package = new PackageState { Root = package.Root, Layout = package.Layout, ForkVersion = package.ForkVersion, Sha256SumsVerified = package.Sha256SumsVerified, Files = new Dictionary<string, FileState>(package.Files, StringComparer.OrdinalIgnoreCase) },
                    Weights = new WeightsState { Source = weights.Path, Size = weights.Size, Sha256 = weights.Sha256 },
                    Compatibility = new OptiCompatibility { X64 = compatibility.X64, FsrMarkers = compatibility.FsrMarkers.ToArray(), Dx12Evidence = compatibility.Dx12Evidence.ToArray(), AntiCheatMarkers = compatibility.AntiCheatMarkers.ToArray() },
                    PreviousRoute = previousRemoval is null ? previousManifest?.PreviousRoute : new PreviousRouteState { Route = InstallRoutes.PostFsr, Removed = previousRemoval.Removed.ToArray(), Preserved = previousRemoval.Preserved.ToArray() },
                    Before = originalBefore,
                    After = after,
                    PreexistingDependencies = (previousManifest?.PreexistingDependencies ?? []).Concat(preexisting).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    InstalledProxyNames = [proxyName]
                };
                await File.WriteAllTextAsync(game.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
                if (File.Exists(game.LegacyManifestPath)) File.Delete(game.LegacyManifestPath);
                game.Status = update ? "Updated" : "Installed";
                return new OptiScalerInstallResult(true, proxyName, preset, written, previousRemoval);
            }
            catch
            {
                try { RestoreSnapshot(folder, managed, before, backupRoot); }
                catch (Exception restoreError)
                {
                    keepBackup = true;
                    throw new IOException($"Rollback could not finish. Recovery files were retained at {backupRoot}.", restoreError);
                }
                if (previousManifestBytes is not null) await File.WriteAllBytesAsync(game.ManifestPath, previousManifestBytes, CancellationToken.None);
                else if (File.Exists(game.ManifestPath)) File.Delete(game.ManifestPath);
                game.Status = "Install failed";
                throw;
            }
            finally
            {
                if (!keepBackup) { try { Directory.Delete(backupRoot, true); } catch { } }
            }
        }
        finally
        {
            game.Busy = false;
            ActiveFolders.TryRemove(folder, out _);
        }
    }

    private static async Task<Dictionary<string, FileState>> SnapshotAsync(string folder, IEnumerable<string> relatives, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in relatives)
        {
            var path = Path.Combine(folder, relative);
            if (!File.Exists(path)) continue;
            result[relative] = new FileState(new FileInfo(path).Length, await DirectGameInstallerService.Sha256Async(path, cancellationToken));
        }
        return result;
    }

    private static void RestoreSnapshot(string folder, IEnumerable<string> relatives, Dictionary<string, FileState> before, string backupRoot)
    {
        foreach (var relative in relatives)
        {
            var target = Path.Combine(folder, relative);
            if (before.ContainsKey(relative))
            {
                var backup = Path.Combine(backupRoot, relative);
                if (File.Exists(backup)) File.Copy(backup, target, true);
            }
            else if (File.Exists(target)) File.Delete(target);
        }
        var dependencyFolder = Path.Combine(folder, OptiScalerPackageService.DependencyFolderName);
        if (Directory.Exists(dependencyFolder) && !Directory.EnumerateFiles(dependencyFolder, "*", SearchOption.AllDirectories).Any()) Directory.Delete(dependencyFolder, true);
    }

    internal static async Task<OptiScalerManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<OptiScalerManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException) { return null; }
    }

    public sealed class OptiScalerManifest
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("created_unix")] public long CreatedUnix { get; set; }
        [JsonPropertyName("route")] public string Route { get; set; } = string.Empty;
        [JsonPropertyName("game_exe")] public string GameExe { get; set; } = string.Empty;
        [JsonPropertyName("proxy_name")] public string ProxyName { get; set; } = "dxgi.dll";
        [JsonPropertyName("preset")] public string Preset { get; set; } = "quality";
        [JsonPropertyName("package")] public PackageState? Package { get; set; }
        [JsonPropertyName("weights")] public WeightsState? Weights { get; set; }
        [JsonPropertyName("compatibility")] public OptiCompatibility? Compatibility { get; set; }
        [JsonPropertyName("previous_route")] public PreviousRouteState? PreviousRoute { get; set; }
        [JsonPropertyName("before")] public Dictionary<string, FileState> Before { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("after")] public Dictionary<string, FileState> After { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("preexisting_dependencies")] public string[] PreexistingDependencies { get; set; } = [];
        [JsonPropertyName("installed_proxy_names")] public string[] InstalledProxyNames { get; set; } = [];
    }

    public sealed class PackageState
    {
        [JsonPropertyName("root")] public string Root { get; set; } = string.Empty;
        [JsonPropertyName("layout")] public string Layout { get; set; } = string.Empty;
        [JsonPropertyName("fork_version")] public string ForkVersion { get; set; } = string.Empty;
        [JsonPropertyName("sha256sums_verified")] public bool Sha256SumsVerified { get; set; }
        [JsonPropertyName("files")] public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class WeightsState
    {
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    }

    public sealed class OptiCompatibility
    {
        [JsonPropertyName("x64")] public bool X64 { get; set; }
        [JsonPropertyName("fsr_markers")] public string[] FsrMarkers { get; set; } = [];
        [JsonPropertyName("dx12_evidence")] public string[] Dx12Evidence { get; set; } = [];
        [JsonPropertyName("anti_cheat_markers")] public string[] AntiCheatMarkers { get; set; } = [];
    }

    public sealed class PreviousRouteState
    {
        [JsonPropertyName("route")] public string Route { get; set; } = string.Empty;
        [JsonPropertyName("removed")] public string[] Removed { get; set; } = [];
        [JsonPropertyName("preserved")] public string[] Preserved { get; set; } = [];
    }
}

public sealed record OptiScalerInstallResult(bool Success, string ProxyName, OptiScalerPreset Preset, IReadOnlyList<string> Written, RemoveResult? PreviousRouteRemoval);
