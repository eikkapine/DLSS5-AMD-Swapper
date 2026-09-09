using System.Diagnostics;
using Microsoft.Win32;

namespace Dlss5AmdSwapper.Services;

public sealed class LosslessScalingService
{
    public string? DetectInstall(string? configuredPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath);

        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 5 && !string.IsNullOrWhiteSpace(current); i++)
        {
            candidates.Add(current);
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        foreach (var root in FindSteamLibraries()) candidates.Add(Path.Combine(root, "steamapps", "common", "Lossless Scaling"));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(IsLosslessInstall);
    }

    public LosslessStatus GetStatus(string installPath)
    {
        if (!IsLosslessInstall(installPath)) return new LosslessStatus(false, false, false, null, null, null, "Lossless Scaling not found");
        var wrapper = Path.Combine(installPath, "Lossless.dll");
        var original = Path.Combine(installPath, "Lossless_original.dll");
        var config = Path.Combine(installPath, "NrAutoScale.ini");
        var fallbackBridge = Path.Combine(installPath, "NrAutoScale", "DlssNrBridge.exe");
        var running = Process.GetProcessesByName("LosslessScaling").Any();
        int? neuralHeight = null;
        string? configuredBridge = null;
        string? hipDevice = null;
        if (File.Exists(config))
        {
            var ini = IniDocument.Load(config);
            var raw = ini.Get("AutoScale", "NeuralMaxHeight") ?? ini.Get("Bridge", "NeuralMaxHeight");
            if (int.TryParse(raw, out var parsed)) neuralHeight = parsed;
            configuredBridge = ini.Get("AutoScale", "BridgeExe");
            hipDevice = ini.Get("AutoScale", "HipVisibleDevices");
        }
        var bridgeExists = (!string.IsNullOrWhiteSpace(configuredBridge) && File.Exists(configuredBridge)) || File.Exists(fallbackBridge);
        var manifest = Path.Combine(installPath, "nr-bridge", "install-manifest.json");
        var installed = File.Exists(original) && File.Exists(config) && bridgeExists && File.Exists(manifest);
        var message = installed ? "Automatic Scale bridge installed" : File.Exists(wrapper) ? "Lossless Scaling found; bridge not installed" : "Lossless Scaling files are incomplete";
        return new LosslessStatus(true, installed, running, neuralHeight, configuredBridge, hipDevice, message);
    }

    public async Task<CommandResult> InstallOrUpdateAsync(string? repoRoot, string installPath, string proxyVersionSource, string nrSource, string hipDevice, CancellationToken cancellationToken = default)
    {
        var payload = ResolvePayload(repoRoot);
        if (!File.Exists(proxyVersionSource) || !Path.GetFileName(proxyVersionSource).Equals("version.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select the user-supplied AMD compatibility proxy named version.dll.");
        if (!File.Exists(nrSource) || !Path.GetFileName(nrSource).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select your nvngx_dlssnr.dll.");

        var existingConfig = Path.Combine(installPath, "NrAutoScale.ini");
        var neuralMaxHeight = 480;
        var nativeResolution = 0;
        var workingScale = 0.0;
        if (File.Exists(existingConfig))
        {
            var ini = IniDocument.Load(existingConfig);
            if (int.TryParse(ini.Get("AutoScale", "NeuralMaxHeight"), out var parsedHeight)) neuralMaxHeight = parsedHeight;
            if (int.TryParse(ini.Get("AutoScale", "NativeResolution"), out var parsedNative)) nativeResolution = parsedNative;
            if (double.TryParse(ini.Get("AutoScale", "WorkingScale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedScale)) workingScale = parsedScale;
            if (string.IsNullOrWhiteSpace(hipDevice)) hipDevice = ini.Get("AutoScale", "HipVisibleDevices") ?? "0";
        }

        var args = new List<string>
        {
            "-LsDir", installPath,
            "-ProxyVersionSource", proxyVersionSource,
            "-NrSource", nrSource,
            "-HipVisibleDevices", hipDevice,
            "-NeuralMaxHeight", neuralMaxHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-WorkingScale", workingScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-NativeResolution", nativeResolution.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-BuiltAutoScaleDllPath", payload.Wrapper,
            "-BridgeExe", payload.Bridge,
            "-NonInteractive"
        };

        var validationArgs = new List<string>(args) { "-ValidateOnly" };
        await RunPowerShellAsync(payload.Setup, validationArgs, cancellationToken);

        var status = GetStatus(installPath);
        if (status.Installed)
            await RunPowerShellAsync(payload.Uninstall, ["-LsDir", installPath], cancellationToken);

        return await RunPowerShellAsync(payload.Setup, args, cancellationToken);
    }

    public async Task<CommandResult> UninstallAsync(string? repoRoot, string installPath, CancellationToken cancellationToken = default)
    {
        var payload = ResolvePayload(repoRoot);
        return await RunPowerShellAsync(payload.Uninstall, ["-LsDir", installPath], cancellationToken);
    }

    private static async Task<CommandResult> RunPowerShellAsync(string script, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(script);
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = ((await stdout) + Environment.NewLine + (await stderr)).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException(output.Length == 0 ? $"Installer exited with code {process.ExitCode}." : output);
        return new CommandResult(process.ExitCode, output);
    }

    private static bool IsLosslessInstall(string path) => Directory.Exists(path) && (File.Exists(Path.Combine(path, "LosslessScaling.exe")) || File.Exists(Path.Combine(path, "Lossless.dll")));

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static LosslessPayload ResolvePayload(string? repoRoot)
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "payload");
        var packagedPayload = new LosslessPayload(
            Path.Combine(packaged, "Setup.ps1"),
            Path.Combine(packaged, "Install-AutoScale.ps1"),
            Path.Combine(packaged, "Uninstall-AutoScale.ps1"),
            Path.Combine(packaged, "Lossless.dll"),
            Path.Combine(packaged, "DlssNrBridge.exe"));
        if (packagedPayload.Exists) return packagedPayload;

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var wrapper = FirstExisting(Path.Combine(repoRoot, "auto-scale", "bin", "Lossless.dll"), Path.Combine(repoRoot, "auto-scale", "build", "Release", "Lossless.dll"));
            var bridge = FirstExisting(Path.Combine(repoRoot, "bridge", "build", "Release", "DlssNrBridge.exe"), Path.Combine(repoRoot, "auto-scale", "bin", "DlssNrBridge.exe"));
            if (wrapper is not null && bridge is not null)
            {
                var sourcePayload = new LosslessPayload(
                    Path.Combine(repoRoot, "auto-scale", "scripts", "Setup.ps1"),
                    Path.Combine(repoRoot, "auto-scale", "scripts", "Install-AutoScale.ps1"),
                    Path.Combine(repoRoot, "auto-scale", "scripts", "Uninstall-AutoScale.ps1"),
                    wrapper,
                    bridge);
                if (sourcePayload.Exists) return sourcePayload;
            }
        }

        throw new InvalidOperationException("The project-owned Lossless Scaling payload is missing. Use a packaged build or build the native bridge and wrapper from source first.");
    }

    private static IEnumerable<string> FindSteamLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyPath in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
        {
            var path = Registry.GetValue(keyPath, "SteamPath", null) as string;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) roots.Add(Path.GetFullPath(path));
        }
        foreach (var root in roots.ToArray())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            var text = File.ReadAllText(vdf);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var value = match.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(value)) roots.Add(Path.GetFullPath(value));
            }
        }
        return roots;
    }
}

internal sealed record LosslessPayload(string Setup, string Installer, string Uninstall, string Wrapper, string Bridge)
{
    public bool Exists => File.Exists(Setup) && File.Exists(Installer) && File.Exists(Uninstall) && File.Exists(Wrapper) && File.Exists(Bridge);
}

public sealed record LosslessStatus(bool Found, bool Installed, bool Running, int? NeuralMaxHeight, string? BridgePath, string? HipDevice, string Message);
public sealed record CommandResult(int ExitCode, string Output);
