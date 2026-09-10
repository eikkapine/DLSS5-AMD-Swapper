using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Dlss5AmdSwapper.Services;

public sealed class GameDiscoveryService
{
    public GameDiscoveryService(GameProbeService probe) { ArgumentNullException.ThrowIfNull(probe); }

    private static readonly string[] HotZoneNames =
    [
        "Games", "SteamLibrary", "GOG Games", "Epic Games", "Origin Games", "EA Games",
        "Ubisoft Games", "Battle.net", "XboxGames", "Amazon Games", "itch", "itch.io", "Rockstar Games"
    ];

    private static readonly HashSet<string> UtilityDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steam", "SteamLibrary", "GalaxyClient", "GOG Galaxy", "Epic Games Launcher", "EpicGamesLauncher",
        "Ubisoft Connect", "Ubisoft Game Launcher", "EA Desktop", "EA", "Battle.net", "Riot Games", "Riot Client",
        "CurseForge", "Overwolf", "LaunchBox", "Mods", "Saves", "SaveGame", "GameSave", "Backup", "Backups",
        "Emulators", "Emulator", "Windows", "$Recycle.Bin", "System Volume Information"
    };

    private static readonly string[] UtilityPrefixes = ["nitrox_", "darkbot", "trainer", "savescummer"];

    public async Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
        bool includeHeuristics = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? additionalFolders = null,
        bool includeAllDrives = false)
    {
        return await Task.Run(() =>
        {
            var all = new List<DiscoveredGame>();
            var scanners = new (string Name, Func<CancellationToken, IReadOnlyList<DiscoveredGame>> Scan)[]
            {
                ("Steam", ScanSteam),
                ("Epic", ScanEpic),
                ("GOG", ScanGog),
                ("Ubisoft", ScanUbisoft),
                ("EA / Battle.net / Rockstar", ScanUninstallLaunchers),
                ("Xbox / Game Pass", ScanXbox)
            };

            foreach (var scanner in scanners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Scanning {scanner.Name}…");
                try { all.AddRange(scanner.Scan(cancellationToken)); }
                catch (OperationCanceledException) { throw; }
                catch { /* One inaccessible launcher must not abort discovery. */ }
            }

            var launcherResults = Deduplicate(all);
            if (includeHeuristics)
            {
                progress?.Report("Scanning common game folders…");
                var knownRoots = launcherResults
                    .Select(game => NormalizePath(game.RootPath))
                    .Where(path => path.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                all.AddRange(ScanHotZones(knownRoots, progress, cancellationToken));
            }

            var known = all.Select(game => NormalizePath(game.RootPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in (additionalFolders ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                all.AddRange(ScanFolder(folder, known, progress, cancellationToken, maxDepth: 5));
            }
            if (includeAllDrives)
            {
                foreach (var drive in FixedDrives())
                    all.AddRange(ScanFolder(drive, known, progress, cancellationToken, maxDepth: 6));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Deduplicate(all)
                .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    private IReadOnlyList<DiscoveredGame> ScanSteam(CancellationToken cancellationToken)
    {
        var games = new List<DiscoveredGame>();
        foreach (var library in FindSteamLibraries())
        {
            var steamApps = Path.Combine(library, "steamapps");
            foreach (var manifest in SafeFiles(steamApps, "appmanifest_*.acf"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = SafeRead(manifest);
                var appId = ReadAcfValue(text, "appid");
                var name = ReadAcfValue(text, "name");
                var installDir = ReadAcfValue(text, "installdir");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir)) continue;
                if (appId == "228980" || name.Contains("dedicated server", StringComparison.OrdinalIgnoreCase)) continue;

                var root = Path.Combine(steamApps, "common", installDir);
                if (!Directory.Exists(root)) continue;
                var exe = FindBestExecutable(root, name, lenient: true, cancellationToken);
                if (exe is null) continue;
                games.Add(new DiscoveredGame(name, exe, "Steam", root));
            }
        }
        return games;
    }

    private IReadOnlyList<DiscoveredGame> ScanEpic(CancellationToken cancellationToken)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData)) programData = @"C:\ProgramData";
        var folder = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        var byRoot = new Dictionary<string, EpicCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in SafeFiles(folder, "*.item"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var json = document.RootElement;
                var name = JsonString(json, "DisplayName");
                var root = JsonString(json, "InstallLocation");
                var launchExe = JsonString(json, "LaunchExecutable");
                var appName = JsonString(json, "AppName");
                var mainAppName = JsonString(json, "MainGameAppName");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

                var categories = JsonStrings(json, "AppCategories");
                var addon = string.IsNullOrWhiteSpace(launchExe) ||
                            (!string.IsNullOrWhiteSpace(mainAppName) && !mainAppName.Equals(appName, StringComparison.OrdinalIgnoreCase)) ||
                            categories.Any(value => value.Equals("addons", StringComparison.OrdinalIgnoreCase));
                if (addon) continue;

                var score = 5;
                if (string.IsNullOrWhiteSpace(mainAppName) || mainAppName.Equals(appName, StringComparison.OrdinalIgnoreCase)) score += 3;
                if (categories.Any(value => value.Equals("games", StringComparison.OrdinalIgnoreCase))) score += 2;
                var key = NormalizePath(root);
                var candidate = new EpicCandidate(name, root, launchExe, score);
                if (!byRoot.TryGetValue(key, out var current) || candidate.Score > current.Score) byRoot[key] = candidate;
            }
            catch { }
        }

        var games = new List<DiscoveredGame>();
        foreach (var item in byRoot.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exe = string.IsNullOrWhiteSpace(item.LaunchExecutable) ? null : Path.Combine(item.Root, item.LaunchExecutable);
            if (exe is null || !File.Exists(exe)) exe = FindBestExecutable(item.Root, item.Name, lenient: true, cancellationToken);
            if (exe is not null && IsRegularDirectory(Path.GetDirectoryName(exe)!)) games.Add(new DiscoveredGame(item.Name, exe, "Epic Games", item.Root));
        }
        return games;
    }

    private IReadOnlyList<DiscoveredGame> ScanGog(CancellationToken cancellationToken)
    {
        var games = new List<DiscoveredGame>();
        foreach (var view in RegistryViews())
        {
            using var hklm = OpenHive(RegistryHive.LocalMachine, view);
            using var gamesKey = hklm?.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (gamesKey is null) continue;
            foreach (var id in SafeSubKeys(gamesKey))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var key = gamesKey.OpenSubKey(id);
                if (key is null) continue;
                var name = ReadRegistryString(key, "gameName");
                var root = ReadRegistryString(key, "path");
                var rawExe = ReadRegistryString(key, "exe");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                var exe = rawExe;
                if (!string.IsNullOrWhiteSpace(exe) && !Path.IsPathRooted(exe)) exe = Path.Combine(root, exe);
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) exe = FindBestExecutable(root, name, lenient: true, cancellationToken);
                if (exe is not null && IsRegularDirectory(Path.GetDirectoryName(exe)!)) games.Add(new DiscoveredGame(name, exe, "GOG", root));
            }
        }
        return games;
    }

    private IReadOnlyList<DiscoveredGame> ScanUbisoft(CancellationToken cancellationToken)
    {
        var games = new List<DiscoveredGame>();
        foreach (var view in RegistryViews())
        {
            using var hklm = OpenHive(RegistryHive.LocalMachine, view);
            using var installs = hklm?.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
            if (installs is null) continue;
            foreach (var id in SafeSubKeys(installs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var key = installs.OpenSubKey(id);
                var root = key is null ? string.Empty : ReadRegistryString(key, "InstallDir");
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
                var exe = FindBestExecutable(root, name, lenient: true, cancellationToken);
                if (exe is not null) games.Add(new DiscoveredGame(name, exe, "Ubisoft Connect", root));
            }
        }
        return games;
    }

    private IReadOnlyList<DiscoveredGame> ScanUninstallLaunchers(CancellationToken cancellationToken)
    {
        var games = new List<DiscoveredGame>();
        foreach (var entry in ReadUninstallEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publisher = entry.Publisher.ToLowerInvariant();
            var uninstall = entry.UninstallString.ToLowerInvariant();
            var source = publisher.Contains("electronic arts") || publisher.Contains("ea games") || uninstall.Contains("ea desktop")
                ? "EA app"
                : publisher.Contains("blizzard") || uninstall.Contains("battle.net")
                    ? "Battle.net"
                    : publisher.Contains("rockstar") || uninstall.Contains("rockstar games")
                        ? "Rockstar Games"
                        : null;
            if (source is null || string.IsNullOrWhiteSpace(entry.InstallLocation) || !Directory.Exists(entry.InstallLocation)) continue;
            var exe = FindBestExecutable(entry.InstallLocation, entry.DisplayName, lenient: true, cancellationToken);
            if (exe is not null) games.Add(new DiscoveredGame(entry.DisplayName, exe, source, entry.InstallLocation));
        }
        return games;
    }

    private IReadOnlyList<DiscoveredGame> ScanXbox(CancellationToken cancellationToken)
    {
        var games = new List<DiscoveredGame>();
        foreach (var drive in FixedDrives())
        {
            var xboxRoot = Path.Combine(drive, "XboxGames");
            foreach (var directory in SafeDirectories(xboxRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                var content = Path.Combine(directory, "Content");
                var searchRoot = Directory.Exists(content) ? content : directory;
                var exe = FindBestExecutable(searchRoot, name, lenient: true, cancellationToken);
                if (exe is not null) games.Add(new DiscoveredGame(name, exe, "Xbox / Game Pass", directory));
            }
        }
        return games;
    }

    private IReadOnlyList<DiscoveredGame> ScanHotZones(HashSet<string> knownRoots, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var games = new List<DiscoveredGame>();
        foreach (var drive in FixedDrives())
        {
            foreach (var hotZone in HotZoneNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = Path.Combine(drive, hotZone);
                if (!Directory.Exists(root)) continue;
                progress?.Report($"Checking {root}");

                var searchRoot = hotZone.Equals("SteamLibrary", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(root, "steamapps", "common"))
                    ? Path.Combine(root, "steamapps", "common")
                    : root;
                foreach (var directory in SafeDirectories(searchRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(directory);
                    if (ShouldSkipDirectory(name) || LooksLikeLauncherInstall(directory)) continue;
                    var normalized = NormalizePath(directory);
                    if (knownRoots.Contains(normalized)) continue;
                    var exe = FindBestExecutable(directory, name, lenient: false, cancellationToken);
                    if (exe is null) continue;
                    games.Add(new DiscoveredGame(name, exe, "Standalone", directory));
                    knownRoots.Add(normalized);
                }
            }
        }
        return games;
    }

    // A user folder can be a game itself or contain several nested game folders.
    // Stop descending once a game is found; do not reinterpret its tool/content folders as games.
    private IReadOnlyList<DiscoveredGame> ScanFolder(string root, HashSet<string> knownRoots,
        IProgress<string>? progress, CancellationToken cancellationToken, int maxDepth)
    {
        var games = new List<DiscoveredGame>();
        var pending = new Stack<(string Path, int Depth)>();
        if (!IsRegularDirectory(root)) return games;
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (++visited > 20000)
            {
                progress?.Report("Folder scan reached its directory limit. Add a more specific game folder to scan further.");
                break;
            }
            var normalized = NormalizePath(directory);
            if (knownRoots.Contains(normalized)) continue;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            if ((depth > 0 && ShouldSkipDirectory(name)) || name.Equals("Users", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)) continue;
            if (visited % 50 == 1) progress?.Report($"Checking {directory}");
            // Only probe likely game roots, avoiding repeated recursive probes of entire libraries.
            var likelyRoot = SafeFiles(directory, "*.exe").Length > 0 ||
                Directory.Exists(Path.Combine(directory, "Binaries")) || Directory.Exists(Path.Combine(directory, "bin64"));
            if (likelyRoot && !LooksLikeLauncherInstall(directory))
            {
                var exe = FindBestExecutable(directory, name, false, cancellationToken);
                if (exe is not null)
                {
                    games.Add(new DiscoveredGame(name, exe, "Standalone", directory));
                    knownRoots.Add(normalized);
                    continue;
                }
            }
            if (depth < maxDepth)
                foreach (var child in SafeDirectories(directory)) pending.Push((child, depth + 1));
        }
        return games;
    }

    private static bool IsRegularDirectory(string path)
    {
        try
        {
            // Reject junctions in ancestors too, including explicit custom/launcher roots.
            for (DirectoryInfo? directory = new(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return Directory.Exists(path);
        }
        catch { return false; }
    }

    private static string? FindBestExecutable(string root, string? gameName, bool lenient, CancellationToken cancellationToken)
    {
        if (!IsRegularDirectory(root)) return null;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        string? best = null;
        var bestScore = int.MinValue;
        long bestLength = -1;
        var visited = 0;
        var normalizedName = NormalizeName(gameName ?? Path.GetFileName(root));
        while (pending.Count > 0 && visited++ < 4096)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            foreach (var exe in SafeFiles(directory, "*.exe"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { if ((File.GetAttributes(exe) & FileAttributes.ReparsePoint) != 0) continue; }
                catch { continue; }
                var stem = Path.GetFileNameWithoutExtension(exe);
                string[] utility = ["unins", "crash", "report", "launcher", "redist", "setup", "installer", "config", "directx", "dotnetfx", "physx", "easyanticheat", "beservice", "battleye"];
                if (utility.Any(value => stem.Contains(value, StringComparison.OrdinalIgnoreCase))) continue;
                var machine = GameProbeService.ReadPeMachine(exe);
                if (machine is not (0x8664 or 0x014c)) continue;
                var length = SafeLength(exe);
                var score = (machine == 0x8664 ? 2 : 0) + (length >= 10 * 1024 * 1024 ? 3 : length < 1024 * 1024 ? -3 : 0);
                var normalizedStem = NormalizeName(stem);
                if (normalizedName.Length > 0 && normalizedStem.Length > 0 &&
                    (normalizedStem.StartsWith(normalizedName, StringComparison.Ordinal) || normalizedName.StartsWith(normalizedStem, StringComparison.Ordinal))) score += 5;
                if (stem.Contains("shipping", StringComparison.OrdinalIgnoreCase) || stem.Contains("game", StringComparison.OrdinalIgnoreCase)) score += 2;
                if (score > bestScore || (score == bestScore && length > bestLength))
                { best = exe; bestScore = score; bestLength = length; }
            }
            if (depth >= 4) continue;
            foreach (var child in SafeDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (ShouldSkipDirectory(name) || name.Equals("Engine", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("redist", StringComparison.OrdinalIgnoreCase) || name.Equals("Support", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push((child, depth + 1));
            }
        }
        return lenient || bestScore >= 0 ? best : null;
    }

    private static string NormalizeName(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IReadOnlyList<DiscoveredGame> Deduplicate(IEnumerable<DiscoveredGame> games)
    {
        var priority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Steam"] = 100, ["GOG"] = 90, ["Epic Games"] = 80, ["EA app"] = 70,
            ["Ubisoft Connect"] = 65, ["Battle.net"] = 65, ["Rockstar Games"] = 60,
            ["Xbox / Game Pass"] = 55, ["Standalone"] = 10
        };
        return games
            .Where(game => File.Exists(game.ExePath))
            .GroupBy(game => NormalizePath(game.RootPath).Length > 0 ? NormalizePath(game.RootPath) : NormalizePath(game.ExePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(game => priority.GetValueOrDefault(game.Store, 1)).ThenByDescending(game => SafeLength(game.ExePath)).First())
            .ToArray();
    }

    private static IEnumerable<string> FindSteamLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view, keyPath, valueName) in new[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
                     (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath")
                 })
        {
            try
            {
                using var baseKey = OpenHive(hive, view);
                using var key = baseKey?.OpenSubKey(keyPath);
                var value = key?.GetValue(valueName) as string;
                if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value)) roots.Add(Path.GetFullPath(value));
            }
            catch { }
        }

        foreach (var root in roots.ToArray())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            foreach (Match match in Regex.Matches(SafeRead(vdf), "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
            {
                var value = match.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(value)) roots.Add(Path.GetFullPath(value));
            }
        }
        return roots;
    }

    private static IEnumerable<UninstallEntry> ReadUninstallEntries()
    {
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default)
                 })
        {
            RegistryKey? baseKey = null;
            RegistryKey? uninstall = null;
            try
            {
                baseKey = OpenHive(hive, view);
                uninstall = baseKey?.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in SafeSubKeys(uninstall))
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null) continue;
                    var displayName = ReadRegistryString(key, "DisplayName");
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    var install = ReadRegistryString(key, "InstallLocation");
                    var icon = ReadRegistryString(key, "DisplayIcon");
                    if (string.IsNullOrWhiteSpace(install) && !string.IsNullOrWhiteSpace(icon))
                    {
                        var iconPath = icon.Split(',')[0].Trim().Trim('"');
                        if (File.Exists(iconPath)) install = Path.GetDirectoryName(iconPath) ?? string.Empty;
                    }
                    yield return new UninstallEntry(displayName, ReadRegistryString(key, "Publisher"), install, ReadRegistryString(key, "UninstallString"));
                }
            }
            finally
            {
                uninstall?.Dispose();
                baseKey?.Dispose();
            }
        }
    }

    private static bool ShouldSkipDirectory(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('$') || name.StartsWith('.')) return true;
        if (UtilityDirectories.Contains(name)) return true;
        return UtilityPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeLauncherInstall(string root)
    {
        string[] launchers = ["Steam.exe", "GalaxyClient.exe", "EpicGamesLauncher.exe", "EADesktop.exe", "UbisoftConnect.exe", "Battle.net Launcher.exe", "RiotClientServices.exe"];
        return launchers.Any(name => File.Exists(Path.Combine(root, name)) || File.Exists(Path.Combine(root, "bin", name)));
    }

    private static string ReadAcfValue(string text, string key)
    {
        var match = Regex.Match(text, $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string JsonString(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> JsonStrings(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray();
    }

    private static RegistryKey? OpenHive(RegistryHive hive, RegistryView view)
    {
        try { return RegistryKey.OpenBaseKey(hive, view); }
        catch { return null; }
    }

    private static IEnumerable<RegistryView> RegistryViews() => [RegistryView.Registry64, RegistryView.Registry32];
    private static string ReadRegistryString(RegistryKey key, string name) { try { return key.GetValue(name) as string ?? string.Empty; } catch { return string.Empty; } }
    private static string[] SafeSubKeys(RegistryKey key) { try { return key.GetSubKeyNames(); } catch { return []; } }
    private static string[] SafeFiles(string root, string pattern) { try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToArray(); } catch { return []; } }
    private static string[] SafeDirectories(string root) { try { return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Where(IsRegularDirectory).ToArray(); } catch { return []; } }
    private static string SafeRead(string path) { try { return File.ReadAllText(path); } catch { return string.Empty; } }
    private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    private static string NormalizePath(string path) { try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); } catch { return string.Empty; } }
    private static IEnumerable<string> FixedDrives() => DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed).Select(drive => drive.RootDirectory.FullName);

    private sealed record EpicCandidate(string Name, string Root, string LaunchExecutable, int Score);
    private sealed record UninstallEntry(string DisplayName, string Publisher, string InstallLocation, string UninstallString);
}

public sealed record DiscoveredGame(string Name, string ExePath, string Store, string RootPath);
