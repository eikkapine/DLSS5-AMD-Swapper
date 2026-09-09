using System.Net.Http;
using System.Text.Json;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class RuntimeSourceService
{
    private const string UpstreamApi = "https://api.github.com/repos/danielblnc/DLSS-NR-on-AMD/releases/latest";
    private const string UpstreamAssetName = "dlssnr_on_amd_setup.exe";
    private readonly HttpClient _http = CreateHttpClient();

    public async Task<RuntimeSourceResult> EnsureAsync(
        string configuredSetupPath,
        string configuredNrPath,
        IEnumerable<GameEntry> games,
        IEnumerable<string>? additionalNrCandidates = null,
        CancellationToken cancellationToken = default)
    {
        var release = await FetchLatestSetupAsync(cancellationToken);
        var setup = await EnsureSetupAsync(configuredSetupPath, release, cancellationToken);
        var nrPath = FindNrDll(configuredNrPath, games, additionalNrCandidates);
        return new RuntimeSourceResult(
            setup.Path,
            nrPath,
            release.Tag ?? "latest",
            setup.Downloaded,
            nrPath is not null);
    }

    public static bool IsValidNrDll(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (!Path.GetFileName(path).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)) return false;
        try { return GameProbeService.ReadPeMachine(path) == 0x8664; }
        catch { return false; }
    }

    private async Task<CachedSetup> EnsureSetupAsync(string configuredPath, SetupAsset release, CancellationToken cancellationToken)
    {
        if (await MatchesReleaseAsync(configuredPath, release, cancellationToken))
            return new CachedSetup(Path.GetFullPath(configuredPath), false);

        var safeTag = string.Concat((release.Tag ?? "latest").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLSS5 AMD Swapper",
            "runtime-sources",
            safeTag);
        Directory.CreateDirectory(cacheFolder);
        var destination = Path.Combine(cacheFolder, UpstreamAssetName);
        if (await MatchesReleaseAsync(destination, release, cancellationToken))
            return new CachedSetup(destination, false);

        var temp = destination + ".download";
        if (File.Exists(temp)) File.Delete(temp);
        try
        {
            using var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(temp))
                await input.CopyToAsync(output, cancellationToken);

            if (!await MatchesReleaseAsync(temp, release, cancellationToken))
                throw new InvalidOperationException("The downloaded official setup failed its GitHub size/SHA-256 verification.");
            File.Move(temp, destination, true);
            return new CachedSetup(destination, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static async Task<bool> MatchesReleaseAsync(string? path, SetupAsset release, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length != release.Size) return false;
            var hash = await DirectGameInstallerService.Sha256Async(path, cancellationToken);
            return hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string? FindNrDll(string configuredPath, IEnumerable<GameEntry> games, IEnumerable<string>? additionalCandidates)
    {
        var exact = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) exact.Add(configuredPath);
        if (additionalCandidates is not null) exact.AddRange(additionalCandidates.Where(path => !string.IsNullOrWhiteSpace(path)));

        foreach (var game in games)
        {
            if (string.IsNullOrWhiteSpace(game.DirectoryPath)) continue;
            exact.Add(Path.Combine(game.DirectoryPath, "nvngx_dlssnr.dll"));

            var dir = new DirectoryInfo(game.DirectoryPath).Parent;
            for (var depth = 0; dir is not null && depth < 3; depth++, dir = dir.Parent)
            {
                if (IsLibraryBoundary(dir.Name)) break;
                exact.Add(Path.Combine(dir.FullName, "nvngx_dlssnr.dll"));
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        exact.Add(Path.Combine(profile, "Downloads", "nvngx_dlssnr.dll"));
        exact.Add(Path.Combine(desktop, "nvngx_dlssnr.dll"));
        exact.Add(Path.Combine(documents, "nvngx_dlssnr.dll"));

        return exact
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(IsValidNrDll);
    }

    private async Task<SetupAsset> FetchLatestSetupAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(UpstreamApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), UpstreamAssetName, StringComparison.Ordinal)) continue;
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlValue) ? urlValue.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GitHub did not publish a SHA-256 digest for the current official setup.");
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("GitHub did not publish a download URL for the current official setup.");
            return new SetupAsset(tag, size, digest[7..].ToLowerInvariant(), url);
        }
        throw new InvalidOperationException("The latest DLSS-NR-on-AMD release has no setup asset.");
    }

    private static bool IsLibraryBoundary(string name) =>
        name.Equals("common", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SteamLibrary", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Epic Games", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("XboxGames", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-AMD-Swapper/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed record SetupAsset(string? Tag, long Size, string Sha256, string DownloadUrl);
    private sealed record CachedSetup(string Path, bool Downloaded);
}

public sealed record RuntimeSourceResult(
    string SetupPath,
    string? NrDllPath,
    string UpstreamTag,
    bool SetupDownloaded,
    bool NrDllFound)
{
    public bool Ready => NrDllFound && File.Exists(SetupPath) && RuntimeSourceService.IsValidNrDll(NrDllPath);
}
