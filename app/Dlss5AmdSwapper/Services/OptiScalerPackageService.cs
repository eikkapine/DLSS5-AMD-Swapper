using System.Diagnostics;
using System.Text;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class OptiScalerPackageService
{
    public const string ForkMarker = "amd-presr";
    public const string PassMarker = "dlssnr_amd";
    public const string WeightsName = "dlssnr_on_amd_weights.bin";
    public const string EnablerName = "dlss-enabler-headless.dll";
    public const string DependencyFolderName = "OptiScaler";
    public const string RequiredUpscalerDependency = "amd_fidelityfx_upscaler_dx12.dll";
    public static readonly string[] PassNames = ["dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll"];

    public static OptiScalerPackage Validate(string root, Func<string, PeVersion>? versionReader = null)
    {
        versionReader ??= ReadPeVersion;
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new InvalidOperationException("The OptiScaler package folder does not exist.");

        var packageDll = Path.Combine(root, "OptiScaler.dll");
        var vodkamanDll = Path.Combine(root, "dxgi.dll");
        string fork;
        string layout;
        if (File.Exists(packageDll)) { fork = packageDll; layout = OptiScalerPackage.LayoutPackage; }
        else if (File.Exists(vodkamanDll)) { fork = vodkamanDll; layout = OptiScalerPackage.LayoutVodkaman; }
        else throw new InvalidOperationException("No OptiScaler.dll or dxgi.dll was found in the package folder.");

        if (GameProbeService.ReadPeMachine(fork) != 0x8664)
            throw new InvalidOperationException($"{Path.GetFileName(fork)} is not a 64-bit Windows PE file.");
        var version = versionReader(fork);
        if (!string.Equals(version.ProductName, "OptiScaler", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{Path.GetFileName(fork)} does not identify itself as OptiScaler.");
        if (version.ProductVersion is null || !version.ProductVersion.Contains(ForkMarker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This OptiScaler build is not the AMD pre-SR fork; the pre-SR route needs a build whose version contains amd-presr.");

        var passes = new List<string>();
        foreach (var name in PassNames)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path))
            {
                if (passes.Count == 0) throw new InvalidOperationException("dlssnr_amd_pass1.dll was not found in the package folder.");
                continue;
            }
            if (GameProbeService.ReadPeMachine(path) != 0x8664) throw new InvalidOperationException($"{name} is not a 64-bit Windows PE file.");
            if (!ContainsAsciiMarker(path, PassMarker)) throw new InvalidOperationException($"{name} does not look like a DLSS-NR-on-AMD runtime (marker {PassMarker} missing).");
            passes.Add(path);
        }

        var ini = Path.Combine(root, "OptiScaler.ini");
        var dependencies = Path.Combine(root, DependencyFolderName);
        var enabler = Path.Combine(root, EnablerName);
        var weights = Path.Combine(root, WeightsName);
        var sums = Path.Combine(root, "SHA256SUMS.txt");

        var files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        void Record(string path)
        {
            var relative = Path.GetRelativePath(root, path);
            files[relative] = new FileState(new FileInfo(path).Length, DirectGameInstallerService.Sha256Async(path).GetAwaiter().GetResult());
        }
        Record(fork);
        foreach (var pass in passes) Record(pass);
        if (File.Exists(ini)) Record(ini);
        if (File.Exists(enabler)) Record(enabler);
        if (Directory.Exists(dependencies))
            foreach (var file in Directory.EnumerateFiles(dependencies, "*", SearchOption.AllDirectories)) Record(file);

        var sumsVerified = false;
        if (File.Exists(sums))
        {
            foreach (var (relative, expected) in ParseSha256Sums(sums))
            {
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"SHA256SUMS.txt lists a path outside the package: {relative}");
                if (!File.Exists(full)) continue; // optional file absent; the required ones were checked above
                var actual = files.TryGetValue(Path.GetRelativePath(root, full), out var state) ? state.Sha256 : DirectGameInstallerService.Sha256Async(full).GetAwaiter().GetResult();
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"SHA256SUMS.txt does not match {relative}. Re-download the package before installing.");
            }
            sumsVerified = true;
        }

        return new OptiScalerPackage(
            root, fork, passes,
            File.Exists(ini) ? ini : null,
            Directory.Exists(dependencies) ? dependencies : null,
            File.Exists(enabler) ? enabler : null,
            IsRealWeightsFile(weights) ? weights : null,
            File.Exists(sums) ? sums : null,
            version.ProductVersion,
            files, sumsVerified, layout);
    }

    public static bool IsRealWeightsFile(string path)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length <= 1024 * 1024) return false;
        using var stream = File.OpenRead(path);
        var head = new byte[32];
        var read = stream.Read(head, 0, head.Length);
        return !Encoding.ASCII.GetString(head, 0, read).StartsWith("version https://git-lfs", StringComparison.Ordinal);
    }

    public static bool ContainsAsciiMarker(string path, string marker)
    {
        var needle = Encoding.ASCII.GetBytes(marker);
        using var stream = File.OpenRead(path);
        var buffer = new byte[4 * 1024 * 1024 + needle.Length];
        var carry = 0;
        while (true)
        {
            var read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read == 0) return false;
            var length = carry + read;
            if (buffer.AsSpan(0, length).IndexOf(needle) >= 0) return true;
            carry = Math.Min(needle.Length - 1, length);
            Array.Copy(buffer, length - carry, buffer, 0, carry);
        }
    }

    public static PeVersion ReadPeVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new PeVersion(info.ProductName, info.ProductVersion);
    }

    public static IEnumerable<(string Relative, string Sha256)> ParseSha256Sums(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length < 66) continue;
            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var rest = line[64..].TrimStart(' ', '*');
            if (rest.Length == 0) continue;
            yield return (rest.Replace('/', '\\'), hash);
        }
    }
}
