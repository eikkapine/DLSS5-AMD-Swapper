using System.Buffers;
using System.Text;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class GameProbeService
{
    private static readonly HashSet<string> FsrMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ffx_fsr3upscaler_x64.dll",
        "ffx_fsr3_x64.dll",
        "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_upscaler_dx12.dll",
        "amd_fidelityfx_loader_dx12.dll",
        "ffx_fsr2_api_x64.dll"
    };

    private static readonly string[] AntiCheatMarkers =
    [
        "easyanticheat", "easyanticheat_eos", "battleye", "beservice", "vgc", "vgk",
        "faceit", "faceitclient", "ace-base", "ace-guard", "equ8",
        "eaanticheat", "javelinanticheat", "ricochet", "xigncode", "gameguard", "nprotect",
        "punkbuster", "pbsvc", "blackcipher"
    ];

    public async Task ProbeAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        var exe = new FileInfo(game.ExePath);
        if (!exe.Exists) throw new FileNotFoundException("Game executable was not found.", game.ExePath);

        game.Status = "Scanning";
        var result = await Task.Run(() => Probe(exe.FullName, cancellationToken), cancellationToken);
        game.X64 = result.X64;
        game.HasFsr = result.FsrMarkers.Count > 0;
        game.HasDx12 = result.Dx12Evidence.Count > 0;
        game.HasAntiCheat = result.AntiCheatMarkers.Count > 0;
        game.FsrMarkers = result.FsrMarkers;
        game.AntiCheatMarkers = result.AntiCheatMarkers;
        game.Status = game.HasAntiCheat ? "Anti-cheat detected" : game.Eligible ? "Ready for AMD Neural Rendering" : "Compatibility uncertain";
        game.NotifyComputed();
    }

    public ProbeResult Probe(string exePath, CancellationToken cancellationToken = default)
    {
        var exe = Path.GetFullPath(exePath);
        var root = Path.GetDirectoryName(exe) ?? throw new InvalidOperationException("Game executable has no directory.");
        var x64 = ReadPeMachine(exe) == 0x8664;
        var fsr = new List<string>();
        var antiCheat = new List<string>();

        foreach (var path in EnumerateFilesBounded(root, 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (FsrMarkers.Contains(name)) fsr.Add(Path.GetRelativePath(root, path));
            var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            if (AntiCheatMarkers.Any(marker => stem.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                antiCheat.Add(Path.GetRelativePath(root, path));
        }

        foreach (var directory in EnumerateDirectoriesBounded(root, 4))
        {
            var name = Path.GetFileName(directory).ToLowerInvariant();
            if (AntiCheatMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                antiCheat.Add(Path.GetRelativePath(root, directory));
        }

        var dx12 = new List<string>();
        if (BinaryContainsDx12(exe)) dx12.Add(Path.GetFileName(exe));
        dx12.AddRange(fsr.Where(marker => marker.Contains("dx12", StringComparison.OrdinalIgnoreCase)));

        var checkedDlls = 0;
        foreach (var dll in SafeEnumerateTopFiles(root, "*.dll").OrderByDescending(SafeLength))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checkedDlls >= 24) break;
            if (ReadPeMachine(dll) != 0x8664) continue;
            checkedDlls++;
            if (BinaryContainsDx12(dll)) dx12.Add(Path.GetFileName(dll));
        }

        return new ProbeResult(x64, fsr.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
            antiCheat.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
            dx12.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray());
    }

    public bool HasFsrMarkerQuick(string root)
    {
        return EnumerateFilesBounded(root, 4).Any(path => FsrMarkers.Contains(Path.GetFileName(path)));
    }

    public string? FindBestExecutable(string root, string? gameName = null, bool lenient = true)
    {
        var normalizedGameName = NormalizeName(gameName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(root)));
        var candidates = EnumerateExecutableCandidates(root, 4)
            .Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsUtilityExecutable(path))
            .Select(path => new
            {
                Path = path,
                Score = ExecutableScore(root, path, normalizedGameName),
                Size = SafeLength(path)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Size)
            .ToList();
        var best = candidates.FirstOrDefault();
        if (best is null) return null;
        return lenient || best.Score > -5 ? best.Path : null;
    }

    private static int ExecutableScore(string root, string path, string normalizedGameName)
    {
        var length = SafeLength(path);
        var score = 0;
        if (length >= 10 * 1024 * 1024) score += 3;
        if (length < 1024 * 1024) score -= 3;
        if (ReadPeMachine(path) == 0x8664) score += 2;
        if (BinaryContainsDx12(path)) score += 3;

        var stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var normalizedStem = NormalizeName(stem);
        if (normalizedGameName.Length > 0 && normalizedStem.Length > 0 &&
            (normalizedStem == normalizedGameName || normalizedStem.StartsWith(normalizedGameName, StringComparison.OrdinalIgnoreCase) || normalizedGameName.StartsWith(normalizedStem, StringComparison.OrdinalIgnoreCase)))
            score += 5;
        if (stem.Contains("game") || stem.Contains("win64") || stem.Contains("shipping")) score += 2;

        var relativeDir = Path.GetRelativePath(root, Path.GetDirectoryName(path) ?? root).Replace('/', '\\').ToLowerInvariant();
        string[] preferred = ["binaries", @"binaries\win64", "bin", "bin64", "x64", @"binaries\retail", @"binaries\steamretail"];
        if (preferred.Any(dir => relativeDir.Equals(dir, StringComparison.OrdinalIgnoreCase) || relativeDir.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))) score += 2;
        return score;
    }

    private static bool IsUtilityExecutable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        string[] blocked = ["unins", "uninstall", "crash", "report", "launcher", "redist", "setup", "installer", "config", "vc_redist", "vcredist", "directx", "dotnetfx", "physx", "easyanticheat", "beservice", "battleye"];
        return blocked.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateExecutableCandidates(string root, int maxDepth)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            foreach (var file in SafeEnumerateTopFiles(current, "*.exe")) yield return file;
            if (depth >= maxDepth) continue;
            foreach (var directory in SafeEnumerateTopDirectories(current))
            {
                var name = Path.GetFileName(directory).ToLowerInvariant();
                if (name is "_commonredist" or "redist" or "vc_redist" or "vcredist" or "directx" or "support" or "dxsetup" or "engine") continue;
                stack.Push((directory, depth + 1));
            }
        }
    }

    private static string NormalizeName(string value)
    {
        var buffer = new StringBuilder(value.Length);
        foreach (var character in value)
            if (char.IsLetterOrDigit(character)) buffer.Append(char.ToLowerInvariant(character));
        return buffer.ToString();
    }

    public static ushort? ReadPeMachine(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[64];
            if (stream.Read(head) < 64 || head[0] != (byte)'M' || head[1] != (byte)'Z') return null;
            var offset = BitConverter.ToInt32(head[0x3C..0x40]);
            stream.Position = offset;
            Span<byte> pe = stackalloc byte[6];
            if (stream.Read(pe) < 6 || pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0) return null;
            return BitConverter.ToUInt16(pe[4..6]);
        }
        catch
        {
            return null;
        }
    }

    private static bool BinaryContainsDx12(string path)
    {
        byte[]? rented = null;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            rented = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024 + 64);
            var overlap = 0;
            while (true)
            {
                var read = stream.Read(rented, overlap, 4 * 1024 * 1024);
                if (read <= 0) return false;
                var length = overlap + read;
                var text = Encoding.ASCII.GetString(rented, 0, length);
                if (text.Contains("d3d12.dll", StringComparison.OrdinalIgnoreCase) || text.Contains("d3d12core.dll", StringComparison.OrdinalIgnoreCase)) return true;
                overlap = Math.Min(63, length);
                Buffer.BlockCopy(rented, length - overlap, rented, 0, overlap);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static IEnumerable<string> EnumerateFilesBounded(string root, int maxDepth)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            foreach (var file in SafeEnumerateTopFiles(current, "*")) yield return file;
            if (depth >= maxDepth) continue;
            foreach (var directory in SafeEnumerateTopDirectories(current)) stack.Push((directory, depth + 1));
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesBounded(string root, int maxDepth)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            if (depth >= maxDepth) continue;
            foreach (var directory in SafeEnumerateTopDirectories(current))
            {
                yield return directory;
                stack.Push((directory, depth + 1));
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateTopFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToArray(); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateTopDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch { return []; }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}

public sealed record ProbeResult(bool X64, IReadOnlyList<string> FsrMarkers, IReadOnlyList<string> AntiCheatMarkers, IReadOnlyList<string> Dx12Evidence);
