using System.Diagnostics;
using System.Text.Json;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

// Runs the same installer/probe/restore services as the GUI, against freshly created
// synthetic game folders only. No desktop input, game launch, settings, or downloads.
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || !args[i].StartsWith("--"))
        throw new ArgumentException("Arguments must be --name value pairs.");
    options.Add(args[i][2..], args[i + 1]);
}
string Required(string key) => Path.GetFullPath(options.GetValueOrDefault(key) ?? throw new ArgumentException($"Missing --{key}."));
var binaries = Required("binaries");
var runRoot = Required("out");
var officialOnly = options.GetValueOrDefault("official-only") == "true";
var requireNeural = options.GetValueOrDefault("require-neural") == "true";
if (Directory.Exists(runRoot) && Directory.EnumerateFileSystemEntries(runRoot).Any())
    throw new InvalidOperationException("The output directory must be new or empty. Existing game folders are never accepted.");
Directory.CreateDirectory(runRoot);
var checks = new List<object>();
var failures = 0;
var serializer = new JsonSerializerOptions { WriteIndented = true };
var probe = new GameProbeService();
var installer = new OptiScalerInstallerService(probe, new DirectGameInstallerService(probe));
OptiScalerPackage? package = null;
LocalWeights? weights = null;

async Task SaveAsync() => await File.WriteAllTextAsync(Path.Combine(runRoot, "summary.json"), JsonSerializer.Serialize(new
{
    createdUtc = DateTimeOffset.UtcNow,
    inputControlUsed = false,
    checks,
    failures,
    limitations = new[] {
        "Hidden-window GPU readback verifies rendering; occluded Present does not prove visible presentation.",
        "The DX12 probe supplies real FFX color/depth/motion/exposure resources with a synthetic pattern. It is not a game quality benchmark.",
        "A loaded OptiScaler proxy or successful FFX dispatch alone does not prove neural inference.",
        "D3D11 and Vulkan are outside the current AMD pre-SR DX12 route. Vulkan is not exercised by this harness."
    }
}, serializer));

void Record(string name, string status, object evidence)
{
    checks.Add(new { name, status, evidence });
    Console.WriteLine($"{status.ToUpperInvariant()}: {name}");
    if (status == "fail" || (status == "unverified" && requireNeural)) ++failures;
}
async Task CheckAsync(string name, Func<Task<object>> action)
{
    try { Record(name, "pass", await action()); }
    catch (Exception error) { Record(name, "fail", error.ToString()); }
    await SaveAsync();
}
GameEntry CreateGame(string name, string binary)
{
    var folder = Path.Combine(runRoot, name);
    Directory.CreateDirectory(folder);
    var exe = Path.Combine(folder, Path.GetFileName(binary));
    File.Copy(binary, exe);
    // Opt out only these synthetic apps from Special K global injection. Otherwise
    // it can restart a probe into an untracked process and invalidate GPU evidence.
    File.WriteAllText(Path.Combine(folder, "SpecialK.deny." + Path.GetFileNameWithoutExtension(exe)), "");
    File.WriteAllText(Path.Combine(folder, "SpecialK.deny." + Path.GetFileName(exe)), "");
    return new GameEntry { ExePath = exe, Name = name };
}
async Task<Dictionary<string, string>> RunAsync(GameEntry game, string outputName, params string[] arguments)
{
    var output = Path.Combine(game.DirectoryPath, outputName);
    Directory.CreateDirectory(output);
    // Each process gets fresh logs; prior copies remain under that run's output.
    string[] runtimeLogs = ["OptiScaler.log", "amd_presr.log", "dlssnr_on_amd.log", "ffx-debug.log", "probe-error.txt"];
    foreach (var name in runtimeLogs)
        if (File.Exists(Path.Combine(game.DirectoryPath, name))) File.Delete(Path.Combine(game.DirectoryPath, name));
    var info = new ProcessStartInfo(game.ExePath)
    {
        WorkingDirectory = game.DirectoryPath, UseShellExecute = false,
        CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    if (Path.GetFileName(game.ExePath).Contains("d3d11")) info.ArgumentList.Add(output);
    else { info.ArgumentList.Add("--out"); info.ArgumentList.Add(output); }
    using var process = Process.Start(info) ?? throw new IOException("Probe did not start.");
    using var processJob = ProcessJob.Track(process);
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var timedOut = false;
    try { await process.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException)
    {
        timedOut = true;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
    await File.WriteAllTextAsync(Path.Combine(output, "stdout.txt"), await stdout);
    await File.WriteAllTextAsync(Path.Combine(output, "stderr.txt"), await stderr);
    foreach (var name in runtimeLogs)
        if (File.Exists(Path.Combine(game.DirectoryPath, name))) File.Copy(Path.Combine(game.DirectoryPath, name), Path.Combine(output, name));
    if (timedOut) throw new TimeoutException($"Probe exceeded 60 seconds and its process tree was stopped. Logs: {output}");
    if (process.ExitCode != 0) throw new IOException($"Probe exit code {process.ExitCode} (0x{unchecked((uint)process.ExitCode):X8}); logs: {output}");
    var reportPath = Path.Combine(output, "report.txt");
    var report = new Dictionary<string, string>(StringComparer.Ordinal);
    var specialKBootstrap = false;
    foreach (var line in await File.ReadAllLinesAsync(reportPath))
    {
        if (line.StartsWith("module=", StringComparison.Ordinal) && line.Contains("SpecialK", StringComparison.OrdinalIgnoreCase))
            specialKBootstrap = true;
        if (line.StartsWith("module=", StringComparison.Ordinal) && line.Contains(@"Special K\Drivers\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Special K initialized its driver wrappers despite the per-exe deny marker; rendering evidence is contaminated.");
        var split = line.IndexOf('=');
        if (split > 0) report[line[..split]] = line[(split + 1)..];
    }
    // The global bootstrap can remain mapped after honoring a deny marker. Merely
    // seeing its module does not mean hooks/driver wrappers initialized.
    report["specialk_bootstrap_mapped"] = specialKBootstrap.ToString();
    if (!long.TryParse(report.GetValueOrDefault("frames_rendered"), out var frames) || frames < 12)
        throw new IOException("Probe did not render the required minimum frames.");
    if (report.GetValueOrDefault("present_failure_count") != "0" || report.GetValueOrDefault("device_removed_reason") != "0x0")
        throw new IOException("Probe reported a presentation or device failure.");
    return report;
}
string[] FfxArguments() => ["--fsr", "--frames", "24", "--seconds", "30", "--width", "640", "--height", "360"];
void AddFfxRuntime(GameEntry game)
{
    foreach (var name in new[] { "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll" })
        File.Copy(Path.Combine(package!.DependencyFolder!, name), Path.Combine(game.DirectoryPath, name));
}
async Task<Dictionary<string, string>> SnapshotAsync(string folder)
{
    var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        snapshot[Path.GetRelativePath(folder, path)] = await DirectGameInstallerService.Sha256Async(path);
    return snapshot;
}
void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task<object> CompareFramesAsync(string left, string right, bool expectEqual)
{
    var a = await File.ReadAllBytesAsync(left);
    var b = await File.ReadAllBytesAsync(right);
    Require(a.Length == b.Length && a.Length > 100, "Frame payload sizes differ or are empty.");
    var headerEnd = 0;
    for (var lines = 0; lines < 3; ++lines)
    {
        headerEnd = Array.IndexOf(a, (byte)'\n', headerEnd) + 1;
        Require(headerEnd > 0, "Invalid PPM header.");
    }
    Require(a.AsSpan(0, headerEnd).SequenceEqual(b.AsSpan(0, headerEnd)), "Frame headers differ.");
    long changedPixels = 0, absoluteError = 0;
    for (var index = headerEnd; index < a.Length; index += 3)
    {
        var changed = false;
        for (var channel = 0; channel < 3; ++channel)
        {
            var delta = Math.Abs(a[index + channel] - b[index + channel]);
            absoluteError += delta;
            changed |= delta != 0;
        }
        if (changed) ++changedPixels;
    }
    Require(expectEqual ? changedPixels == 0 : changedPixels > 0,
        expectEqual ? "Repeated disabled frames are not deterministic." : "Neural ON and OFF did not change GPU output.");
    return new { left, right, changedPixels, meanAbsoluteChannelError = (double)absoluteError / (a.Length - headerEnd),
        leftSha256 = await DirectGameInstallerService.Sha256Async(left), rightSha256 = await DirectGameInstallerService.Sha256Async(right) };
}

var d3d11 = CreateGame("d3d11-baseline", Path.Combine(binaries, "swapper_d3d11_probe.exe"));
if (!officialOnly) await CheckAsync("D3D11 shader rendering and GPU readback", async () => await RunAsync(d3d11, "render"));
var d3d12Binary = Path.Combine(binaries, "fsr-probe", "dlssnr_fsr_probe.exe");
var d3d12 = CreateGame("d3d12-baseline", d3d12Binary);
if (!officialOnly) await CheckAsync("D3D12 rendering and GPU readback", async () => await RunAsync(d3d12, "render", "--frames", "24", "--seconds", "15"));

if (!options.ContainsKey("package") || !options.ContainsKey("weights"))
{
    Record("Actual OptiScaler installer and neural runtime", "skip", "Supply --package and --weights for integration testing; no runtime payload is bundled.");
}
else
{
    await CheckAsync("Validate real OptiScaler package and generated weights", async () =>
    {
        package = OptiScalerPackageService.Validate(Required("package"));
        var path = Required("weights");
        Require(OptiScalerPackageService.IsRealWeightsFile(path), "Weights are missing or an LFS pointer.");
        weights = new LocalWeights(path, new FileInfo(path).Length, await DirectGameInstallerService.Sha256Async(path));
        return new { package.ForkVersion, package.Sha256SumsVerified, weights.Size, weights.Sha256 };
    });
    if (package is not null && weights is not null && !officialOnly)
    {
        await CheckAsync("D3D11 target is rejected without modifying its files", async () =>
        {
            var before = await SnapshotAsync(d3d11.DirectoryPath);
            string? rejection = null;
            try { await installer.InstallAsync(d3d11, package, weights, OptiScalerPreset.Balanced, false); }
            catch (InvalidOperationException error) { rejection = error.Message; }
            Require(rejection is not null && (rejection.Contains("FSR") || rejection.Contains("DirectX 12")), "Expected a runtime compatibility rejection.");
            var after = await SnapshotAsync(d3d11.DirectoryPath);
            Require(before.Count == after.Count && before.All(item => after.GetValueOrDefault(item.Key) == item.Value), "Rejected install changed files.");
            return new { rejection, filesUnchanged = true };
        });

        var ffx = CreateGame("d3d12-ffx-baseline", d3d12Binary);
        AddFfxRuntime(ffx);
        await CheckAsync("D3D12 real FFX upscale dispatch and GPU readback", async () =>
        {
            var result = await RunAsync(ffx, "render", FfxArguments());
            Require(result.GetValueOrDefault("ffx_dispatch_ok_count") == "24" && result.GetValueOrDefault("ffx_dispatch_fail_count") == "0", "Expected 24 successful FFX dispatches.");
            return result;
        });

        var managed = CreateGame("d3d12-optiscaler", d3d12Binary);
        AddFfxRuntime(managed);
        var originals = await SnapshotAsync(managed.DirectoryPath);
        var installed = false;
        await CheckAsync("Install using the application's OptiScalerInstallerService", async () =>
        {
            await probe.ProbeAsync(managed);
            var result = await installer.InstallAsync(managed, package, weights, OptiScalerPreset.Balanced, false);
            installed = result.Success;
            Require(installed && File.Exists(managed.ManifestPath), "Managed install manifest was not created.");
            return result;
        });
        if (installed)
        {
            await CheckAsync("Update the managed installation", async () => await installer.InstallAsync(managed, package, weights, OptiScalerPreset.Balanced, true));
            await CheckAsync("Installed OptiScaler proxy renders real FFX input", async () =>
            {
                var result = await RunAsync(managed, "render", FfxArguments());
                var raw = await File.ReadAllTextAsync(Path.Combine(managed.DirectoryPath, "render", "report.txt"));
                Require(raw.Contains(Path.Combine(managed.DirectoryPath, "dxgi.dll"), StringComparison.OrdinalIgnoreCase), "The installed proxy did not load.");
                Require(result.GetValueOrDefault("ffx_dispatch_ok_count") == "24" && result.GetValueOrDefault("ffx_dispatch_fail_count") == "0", "Installed FFX dispatch failed.");
                return result;
            });
            var diagnostics = await new OptiScalerDiagnosticsService().InspectAsync(managed);
            await File.WriteAllTextAsync(Path.Combine(managed.DirectoryPath, "diagnostics.json"), JsonSerializer.Serialize(diagnostics, serializer));
            // Do not count a loaded proxy, a timing line, or a changed frame alone as inference.
            var completed = diagnostics.PassesCompleted.GetValueOrDefault() > 0 && diagnostics.PreSrActive;
            Record("Neural pre-SR evaluation", completed ? "pass" : "unverified", diagnostics);
            var control = new OptiScalerControlService();
            await CheckAsync("Saved OFF disables neural dispatch on the next launch", async () =>
            {
                await control.SetEnabledAsync(managed, false);
                var result = await RunAsync(managed, "disabled-render", FfxArguments());
                var disabledDiagnostics = await new OptiScalerDiagnosticsService().InspectAsync(managed);
                Require(!disabledDiagnostics.PreSrActive, "Neural dispatch remained active after disabling it.");
                return new { render = result, diagnostics = disabledDiagnostics };
            });
            await CheckAsync("Repeated disabled FFX output is deterministic", async () =>
            {
                await RunAsync(managed, "disabled-repeat", FfxArguments());
                return await CompareFramesAsync(Path.Combine(managed.DirectoryPath, "disabled-render", "pre_present_frame023_buffer1.ppm"),
                    Path.Combine(managed.DirectoryPath, "disabled-repeat", "pre_present_frame023_buffer1.ppm"), true);
            });
            await CheckAsync("Neural ON changes GPU output versus OFF with the same OptiScaler provider", async () =>
                await CompareFramesAsync(Path.Combine(managed.DirectoryPath, "render", "pre_present_frame023_buffer1.ppm"),
                    Path.Combine(managed.DirectoryPath, "disabled-render", "pre_present_frame023_buffer1.ppm"), false));
            await CheckAsync("Saved two-pass setting completes two independent AMD passes", async () =>
            {
                await control.SetEnabledAsync(managed, true);
                await control.SetPassesAsync(managed, 2);
                var result = await RunAsync(managed, "two-pass-render", FfxArguments());
                var twoPass = await new OptiScalerDiagnosticsService().InspectAsync(managed);
                Require(twoPass.PreSrActive && twoPass.PassesCompleted == 2 && twoPass.PassesInitialized >= 2, "Expected current evidence of two independent completed AMD passes.");
                return new { render = result, diagnostics = twoPass };
            });
            await CheckAsync("Restore managed binaries and preserve original FFX files", async () =>
            {
                var result = await installer.RemoveAsync(managed);
                Require(!File.Exists(Path.Combine(managed.DirectoryPath, "dxgi.dll")), "Restore left the active proxy behind.");
                foreach (var name in OptiScalerPackageService.PassNames.Append(OptiScalerPackageService.WeightsName))
                    Require(!File.Exists(Path.Combine(managed.DirectoryPath, name)), $"Restore left managed runtime {name} behind.");
                foreach (var item in originals)
                    Require(await DirectGameInstallerService.Sha256Async(Path.Combine(managed.DirectoryPath, item.Key)) == item.Value, $"Original file changed: {item.Key}");
                return result;
            });
            await CheckAsync("D3D12 FFX renders again after restore", async () => await RunAsync(managed, "restored-render", FfxArguments()));
        }
    }
}
if (options.ContainsKey("setup") && options.ContainsKey("nr") && package is not null)
{
    var official = CreateGame("d3d12-official-runtime", d3d12Binary);
    AddFfxRuntime(official);
    File.WriteAllText(Path.Combine(official.DirectoryPath, "SpecialK.deny.dlssnr_on_amd_setup"), "");
    File.WriteAllText(Path.Combine(official.DirectoryPath, "SpecialK.deny.dlssnr_on_amd_setup.exe"), "");
    var originals = await SnapshotAsync(official.DirectoryPath);
    var directInstaller = new DirectGameInstallerService(probe);
    var installed = false;
    await CheckAsync("Official installer validates upstream hash and installs the synthetic DX12 game", async () =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await directInstaller.InstallAsync(official, Required("setup"), Required("nr"), false, deadline.Token);
        installed = result.Success;
        Require(installed, "Official installer reported no successful install.");
        return result;
    });
    if (installed)
    {
        // This tiny probe reaches DXGI much faster than a game. Give the official
        // runtime's asynchronous hook bootstrap time to finish before creating it.
        await CheckAsync("Official AMD proxy renders FFX frames and exits cleanly", async () =>
            await RunAsync(official, "render", "--fsr", "--proxy", "--startup-delay-ms", "10000", "--frames", "180", "--seconds", "30"));
        var diagnostics = await new RuntimeDiagnosticsService().InspectAsync(official);
        Record("Official AMD neural evaluation", diagnostics.RichPathObserved ? "pass" : "unverified", diagnostics);
        await CheckAsync("Official installer updates its managed synthetic installation", async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            return await directInstaller.InstallAsync(official, Required("setup"), Required("nr"), true, deadline.Token);
        });
        await CheckAsync("Official restore removes installed proxies and retains original files", async () =>
        {
            var result = await directInstaller.RemoveAsync(official, true);
            foreach (var proxyName in OptiScalerInstallerService.ProxyNames)
                Require(!File.Exists(Path.Combine(official.DirectoryPath, proxyName)), $"Official restore left {proxyName}.");
            foreach (var item in originals)
                Require(await DirectGameInstallerService.Sha256Async(Path.Combine(official.DirectoryPath, item.Key)) == item.Value, $"Official restore changed {item.Key}.");
            return result;
        });
    }
}
else Record("Official AMD installer", "skip", "Supply --setup and --nr plus the package's FFX runtime for official installer integration checks.");
Record("Vulkan route", "skip", "The selected AMD pre-SR implementation requires D3D12. No Vulkan SDK was available for an independent Vulkan render probe.");
await SaveAsync();
Console.WriteLine($"Report: {Path.Combine(runRoot, "summary.json")}");
return failures == 0 ? 0 : 1;
