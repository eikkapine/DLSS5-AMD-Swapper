using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class DirectGameInstallerService(GameProbeService probe)
{
    private const string UpstreamApi = "https://api.github.com/repos/danielblnc/DLSS-NR-on-AMD/releases/latest";
    public const string UpstreamReleasePage = "https://github.com/danielblnc/DLSS-NR-on-AMD/releases";
    private const string UpstreamAsset = "dlssnr_on_amd_setup.exe";
    private static readonly string[] ProxyNames = ["version.dll", "winmm.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll", "dxgi.dll"];
    private static readonly string[] RuntimeNames = ["dlssnr_on_amd.ini", "dlssnr_on_amd_weights.bin", "dlssnr_on_amd.log"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = CreateHttpClient();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveFolders = new(StringComparer.OrdinalIgnoreCase);

    public async Task<InstallResult> InstallAsync(GameEntry game, string setupSource, string nrSource, bool update, CancellationToken cancellationToken = default)
    {
        var operationFolder = Path.GetFullPath(game.DirectoryPath);
        if (game.Busy || !ActiveFolders.TryAdd(operationFolder, 0)) throw new InvalidOperationException("An operation is already running for this game folder.");
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

            var release = await ValidateSetupAsync(setupSource, cancellationToken);
            var nrMeta = await ValidateNrDllAsync(nrSource, cancellationToken);
            var folder = game.DirectoryPath;
            var existingManifest = FindManifest(game);
            if (existingManifest is not null && ManagedManifest.ReadRoute(existingManifest) == InstallRoute.OptiScalerPreSr)
                throw new InvalidOperationException("This game is managed by the OptiScaler pre-SR route. Restore it before installing the post-FSR runtime.");
            if (update && existingManifest is null) throw new InvalidOperationException("Update requires an existing managed install.");
            if (!update && existingManifest is not null) throw new InvalidOperationException("This game already has a managed install. Use Update instead.");

            var originalManifest = existingManifest is null ? null : await ReadManifestAsync(existingManifest, cancellationToken)
                ?? throw new InvalidOperationException("The managed install manifest could not be read.");
            var before = await SnapshotAsync(folder, cancellationToken);
            if (!update)
            {
                var unmanagedProxy = ProxyNames.Where(before.ContainsKey).ToArray();
                if (unmanagedProxy.Length > 0)
                    throw new InvalidOperationException("A proxy DLL already exists and is not managed by this app: " + string.Join(", ", unmanagedProxy));
            }

            var localSetup = Path.Combine(folder, UpstreamAsset);
            var localNr = Path.Combine(folder, "nvngx_dlssnr.dll");
            if (File.Exists(localSetup) && !string.Equals(await Sha256Async(localSetup, cancellationToken), release.Sha256, StringComparison.OrdinalIgnoreCase)
                && !(update && originalManifest?.ManagedSetupWasCreated == true && originalManifest.After.TryGetValue(UpstreamAsset, out var oldSetup) && oldSetup == before[UpstreamAsset]))
                throw new InvalidOperationException($"A different {UpstreamAsset} already exists in the game folder.");
            if (File.Exists(localNr) && !string.Equals(await Sha256Async(localNr, cancellationToken), nrMeta.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A different nvngx_dlssnr.dll already exists in the game folder.");

            var previousManifestBytes = existingManifest is not null ? await File.ReadAllBytesAsync(existingManifest, cancellationToken) : null;
            var backupRoot = Path.Combine(Path.GetTempPath(), "dlss5-amd-swapper", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupRoot);
            foreach (var name in before.Keys)
            {
                var source = Path.Combine(folder, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(backupRoot, name), true);
            }

            var keepBackup = false;
            try
            {
                if (!Path.GetFullPath(setupSource).Equals(localSetup, StringComparison.OrdinalIgnoreCase))
                    File.Copy(Path.GetFullPath(setupSource), localSetup, true);
                if (!File.Exists(localNr)) File.Copy(Path.GetFullPath(nrSource), localNr, false);

                if (!string.Equals(await Sha256Async(localSetup, cancellationToken), release.Sha256, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(await Sha256Async(localNr, cancellationToken), nrMeta.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Runtime sources changed while preparing the installation.");

                var setup = await RunSetupAsync(localSetup, folder, update, cancellationToken);
                var after = await SnapshotAsync(folder, cancellationToken);
                var changedProxy = ProxyNames.Where(name => after.ContainsKey(name) && (!before.TryGetValue(name, out var old) || old != after[name])).ToArray();
                string[] installedProxy;
                if (update && existingManifest is not null)
                {
                    var oldManifest = await ReadManifestAsync(existingManifest, cancellationToken);
                    installedProxy = oldManifest?.InstalledProxyNames?.Where(after.ContainsKey).ToArray() ?? [];
                    if (installedProxy.Length == 0)
                        throw new InvalidOperationException("The existing managed proxy is missing; refusing to adopt unrelated proxy DLLs.");
                }
                else installedProxy = changedProxy;

                if (setup.ExitCode != 0 || !after.ContainsKey("dlssnr_on_amd.ini") || !after.ContainsKey("dlssnr_on_amd_weights.bin") || installedProxy.Length == 0)
                {
                    var tail = setup.Output.Length > 2000 ? setup.Output[^2000..] : setup.Output;
                    throw new InvalidOperationException($"The upstream setup did not produce a verifiable install (exit {setup.ExitCode}).\n{tail}");
                }

                var verifiedConfig = VerifyRichConfig(game.ConfigPath);
                var manifest = new DirectManifest
                {
                    SchemaVersion = 2,
                    CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Route = "amd-fsr-direct",
                    GameExe = Path.GetFileName(game.ExePath),
                    Upstream = release,
                    NvngxDlssNr = nrMeta,
                    Compatibility = new CompatibilityState
                    {
                        X64 = compatibility.X64,
                        FsrMarkers = compatibility.FsrMarkers.ToArray(),
                        Dx12Evidence = compatibility.Dx12Evidence.ToArray(),
                        AntiCheatMarkers = compatibility.AntiCheatMarkers.ToArray()
                    },
                    VerifiedConfig = verifiedConfig,
                    Before = originalManifest?.Before ?? before,
                    After = after,
                    InstalledProxyNames = installedProxy,
                    ManagedSetupWasCreated = originalManifest?.ManagedSetupWasCreated ?? !before.ContainsKey(UpstreamAsset),
                    ModelWasCopied = originalManifest?.ModelWasCopied ?? !before.ContainsKey("nvngx_dlssnr.dll")
                };

                var manifestText = JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine;
                await File.WriteAllTextAsync(game.ManifestPath, manifestText, new UTF8Encoding(false), cancellationToken);
                if (existingManifest is not null && !Path.GetFullPath(existingManifest).Equals(Path.GetFullPath(game.ManifestPath), StringComparison.OrdinalIgnoreCase) && File.Exists(existingManifest))
                    File.Delete(existingManifest);

                game.Status = update ? "Updated" : "Installed";
                return new InstallResult(true, release.Tag ?? "latest", installedProxy, verifiedConfig, setup.Output);
            }
            catch
            {
                try { await RestoreSnapshotAsync(folder, before, backupRoot, CancellationToken.None); }
                catch (Exception restoreError)
                {
                    keepBackup = true;
                    throw new IOException($"Rollback could not finish. Recovery files were retained at {backupRoot}.", restoreError);
                }
                if (previousManifestBytes is not null && existingManifest is not null)
                    await File.WriteAllBytesAsync(existingManifest, previousManifestBytes, CancellationToken.None);
                if (File.Exists(game.ManifestPath) && (existingManifest is null || !Path.GetFullPath(existingManifest).Equals(Path.GetFullPath(game.ManifestPath), StringComparison.OrdinalIgnoreCase)))
                    File.Delete(game.ManifestPath);
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
            ActiveFolders.TryRemove(operationFolder, out _);
        }
    }

    public async Task<RemoveResult> RemoveAsync(GameEntry game, bool removeModel, CancellationToken cancellationToken = default)
    {
        var operationFolder = Path.GetFullPath(game.DirectoryPath);
        if (game.Busy || !ActiveFolders.TryAdd(operationFolder, 0)) throw new InvalidOperationException("An operation is already running for this game folder.");
        game.Busy = true;
        try
        {
        if (game.Running) throw new InvalidOperationException("Close the game before restoring its files.");
        var manifestPath = FindManifest(game) ?? throw new InvalidOperationException("No managed direct-game install was found.");
        if (ManagedManifest.ReadRoute(manifestPath) == InstallRoute.OptiScalerPreSr)
            throw new InvalidOperationException("This game is managed by the OptiScaler pre-SR route. Use its Restore.");
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken) ?? throw new InvalidOperationException("The managed install manifest could not be read.");
        var removed = new List<string>();
        var preserved = new List<string>();

        foreach (var name in ProxyNames.Concat(RuntimeNames))
        {
            var path = Path.Combine(game.DirectoryPath, name);
            if (manifest.Before.ContainsKey(name)) { preserved.Add(name); continue; }
            if (!File.Exists(path)) continue;
            if (manifest.After.TryGetValue(name, out var expected) && expected == await GetFileStateAsync(path, cancellationToken))
            {
                File.Delete(path);
                removed.Add(name);
            }
            else preserved.Add(name);
        }

        if (manifest.ManagedSetupWasCreated)
            await RemoveIfUnchangedAsync(game.DirectoryPath, UpstreamAsset, manifest.After, removed, preserved, cancellationToken);
        if (removeModel && manifest.ModelWasCopied)
            await RemoveIfUnchangedAsync(game.DirectoryPath, "nvngx_dlssnr.dll", manifest.After, removed, preserved, cancellationToken);

        var remainingCreated = ProxyNames.Concat(RuntimeNames)
            .Where(name => !manifest.Before.ContainsKey(name) && File.Exists(Path.Combine(game.DirectoryPath, name)))
            .ToArray();
        if (remainingCreated.Length == 0) File.Delete(manifestPath);

        game.Status = remainingCreated.Length == 0 ? "Restored" : "Some changed files were preserved";
        return new RemoveResult(removed, preserved.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), remainingCreated, remainingCreated.Length != 0);
        }
        finally
        {
            game.Busy = false;
            ActiveFolders.TryRemove(operationFolder, out _);
        }
    }

    public bool HasManagedInstall(GameEntry game) => ManagedManifest.ReadRoute(game) == InstallRoute.PostFsrRuntime;

    private static string? FindManifest(GameEntry game) => ManagedManifest.FindManifestPath(game);

    private async Task<ReleaseAsset> ValidateSetupAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Official DLSS-NR-on-AMD setup was not found.", path);
        if (!Path.GetFileName(path).Equals(UpstreamAsset, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The setup file must be named {UpstreamAsset}.");

        var release = await FetchLatestReleaseAsync(cancellationToken);
        var info = new FileInfo(path);
        var digest = await Sha256Async(path, cancellationToken);
        if (info.Length != release.Size || !digest.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied setup does not match the SHA-256 and size published for the latest official DLSS-NR-on-AMD release.");
        return release;
    }

    private static async Task<FileState> ValidateNrDllAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("nvngx_dlssnr.dll was not found.", path);
        if (!Path.GetFileName(path).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Neural Rendering DLL must be named nvngx_dlssnr.dll.");
        if (GameProbeService.ReadPeMachine(path) != 0x8664)
            throw new InvalidOperationException("nvngx_dlssnr.dll is not a 64-bit Windows PE file.");
        return await GetFileStateAsync(path, cancellationToken) ?? throw new InvalidOperationException("Could not inspect nvngx_dlssnr.dll.");
    }

    private async Task<ReleaseAsset> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(UpstreamApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (!asset.GetProperty("name").GetString()!.Equals(UpstreamAsset, StringComparison.Ordinal)) continue;
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var digestProp) ? digestProp.GetString() : null;
            if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return new ReleaseAsset(tag, UpstreamAsset, size, digest[7..].ToLowerInvariant(), UpstreamReleasePage);

            if (string.Equals(tag, "v0.2.17", StringComparison.OrdinalIgnoreCase) && size == 7_538_418)
                return new ReleaseAsset(tag, UpstreamAsset, size, "4fcd167d07bc4964eaf9162aa8f4f11e852b91bf866b28cb48d45934022440bc", UpstreamReleasePage);
            throw new InvalidOperationException("GitHub did not publish a SHA-256 digest for the current setup asset.");
        }
        throw new InvalidOperationException("The latest official DLSS-NR-on-AMD release has no setup asset.");
    }

    private static Dictionary<string, int> VerifyRichConfig(string path)
    {
        var ini = IniDocument.Load(path);
        var required = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = 1,
            ["UseFsrInputs"] = 1,
            ["UseDepth"] = 1,
            ["Temporal"] = 1,
            ["Interop"] = 1,
            ["Inline"] = 1
        };
        var verified = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var pair in required)
        {
            var raw = ini.Get("DlssNrOnAmd", pair.Key);
            if (int.TryParse(raw, out var value) && value == pair.Value) verified[pair.Key] = value;
            else missing.Add($"{pair.Key}={pair.Value}");
        }
        if (missing.Count > 0)
            throw new InvalidOperationException("The upstream setup did not enable the full AMD Neural Rendering path: " + string.Join(", ", missing));
        return verified;
    }

    private static async Task<SetupRun> RunSetupAsync(string setup, string folder, bool update, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = setup,
            WorkingDirectory = folder,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(update ? "y\nu\n\n\n\n" : "y\n\n\n\n");
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The upstream setup did not finish within two minutes.");
        }
        var output = (await stdoutTask) + Environment.NewLine + (await stderrTask);
        return new SetupRun(process.ExitCode, output.Trim());
    }

    private static async Task<Dictionary<string, FileState>> SnapshotAsync(string folder, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ManagedNames())
        {
            var state = await GetFileStateAsync(Path.Combine(folder, name), cancellationToken);
            if (state is not null) result[name] = state;
        }
        return result;
    }

    private static IEnumerable<string> ManagedNames() => ProxyNames.Concat(RuntimeNames).Concat([UpstreamAsset, "nvngx_dlssnr.dll"]);

    private static async Task RestoreSnapshotAsync(string folder, Dictionary<string, FileState> before, string backupRoot, CancellationToken cancellationToken)
    {
        foreach (var name in ManagedNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(folder, name);
            if (before.ContainsKey(name))
            {
                var backup = Path.Combine(backupRoot, name);
                if (File.Exists(backup)) File.Copy(backup, target, true);
            }
            else if (File.Exists(target)) File.Delete(target);
        }
        await Task.CompletedTask;
    }

    private static async Task RemoveIfUnchangedAsync(string folder, string name, Dictionary<string, FileState> after, List<string> removed, List<string> preserved, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) return;
        if (after.TryGetValue(name, out var expected) && expected == await GetFileStateAsync(path, cancellationToken))
        {
            File.Delete(path);
            removed.Add(name);
        }
        else preserved.Add(name);
    }

    private static async Task<DirectManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var manifest = await JsonSerializer.DeserializeAsync<DirectManifest>(stream, JsonOptions, cancellationToken);
            if (manifest is not null)
            {
                // System.Text.Json discards the StringComparer.OrdinalIgnoreCase initializer of
                // Dictionary<string, FileState> properties on deserialization, so rebuild them here
                // to keep key lookups case-insensitive after a round-trip through disk.
                manifest.Before = new Dictionary<string, FileState>(manifest.Before, StringComparer.OrdinalIgnoreCase);
                manifest.After = new Dictionary<string, FileState>(manifest.After, StringComparer.OrdinalIgnoreCase);
            }
            return manifest;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<FileState?> GetFileStateAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        return new FileState(new FileInfo(path).Length, await Sha256Async(path, cancellationToken));
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-AMD-Swapper/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed class DirectManifest
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("created_unix")] public long CreatedUnix { get; set; }
        [JsonPropertyName("route")] public string Route { get; set; } = string.Empty;
        [JsonPropertyName("game_exe")] public string GameExe { get; set; } = string.Empty;
        [JsonPropertyName("upstream")] public ReleaseAsset? Upstream { get; set; }
        [JsonPropertyName("nvngx_dlssnr")] public FileState? NvngxDlssNr { get; set; }
        [JsonPropertyName("compatibility")] public CompatibilityState? Compatibility { get; set; }
        [JsonPropertyName("verified_config")] public Dictionary<string, int> VerifiedConfig { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("before")] public Dictionary<string, FileState> Before { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("after")] public Dictionary<string, FileState> After { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("installed_proxy_names")] public string[] InstalledProxyNames { get; set; } = [];
        [JsonPropertyName("managed_setup_was_created")] public bool ManagedSetupWasCreated { get; set; }
        [JsonPropertyName("model_was_copied")] public bool ModelWasCopied { get; set; }
    }

    private sealed class CompatibilityState
    {
        [JsonPropertyName("x64")] public bool X64 { get; set; }
        [JsonPropertyName("fsr_markers")] public string[] FsrMarkers { get; set; } = [];
        [JsonPropertyName("dx12_evidence")] public string[] Dx12Evidence { get; set; } = [];
        [JsonPropertyName("anti_cheat_markers")] public string[] AntiCheatMarkers { get; set; } = [];
    }

    private sealed record SetupRun(int ExitCode, string Output);
}

public sealed record ReleaseAsset(
    [property: JsonPropertyName("tag")] string? Tag,
    [property: JsonPropertyName("asset")] string Asset,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("release_page")] string ReleasePage);

public sealed record FileState(
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record InstallResult(bool Success, string UpstreamTag, IReadOnlyList<string> ProxyNames, IReadOnlyDictionary<string, int> VerifiedConfig, string SetupOutput);
public sealed record RemoveResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Preserved, IReadOnlyList<string> RemainingManagedFiles, bool ManifestRetained);
