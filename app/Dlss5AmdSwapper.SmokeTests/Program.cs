using System.Security.Cryptography;
using System.Text;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

var failures = new List<string>();

await RunAsync("INI preserves unrelated sections", async () =>
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "runtime.ini");
    await File.WriteAllTextAsync(path, "[Other]\nKeep=1\n\n[DlssNrOnAmd]\nEnabled=1\nLocalStructure=1.0\n");
    var ini = IniDocument.Load(path);
    ini.Set("DlssNrOnAmd", "LocalStructure", "1.4");
    ini.SaveAtomic(path);
    var reread = IniDocument.Load(path);
    Assert(reread.Get("Other", "Keep") == "1", "Unrelated section was changed.");
    Assert(reread.Get("DlssNrOnAmd", "LocalStructure") == "1.4", "Updated setting was not saved.");
});

await RunAsync("Runtime controls persist safely", async () =>
{
    using var temp = new TempDirectory();
    var exe = Path.Combine(temp.Path, "FixtureGame.exe");
    await File.WriteAllBytesAsync(exe, []);
    await File.WriteAllTextAsync(Path.Combine(temp.Path, "dlssnr_on_amd.ini"), "[DlssNrOnAmd]\nEnabled=1\nLocalStructure=1.0\nLocalTone=1.0\nSkinStructure=1.0\n");
    await File.WriteAllBytesAsync(Path.Combine(temp.Path, "version.dll"), [1]);
    var game = new GameEntry { Name = "Fixture", ExePath = exe };
    var service = new RuntimeControlService();
    var off = await service.SetEnabledAsync(game, false);
    Assert(!off.LiveAcknowledged, "A non-running fixture must not report live acknowledgement.");
    await service.AdjustStructureAsync(game, 0.1);
    var ini = IniDocument.Load(game.ConfigPath);
    Assert(!ini.GetBool("DlssNrOnAmd", "Enabled", true), "Enabled did not toggle off.");
    Assert(Math.Abs(ini.GetDouble("DlssNrOnAmd", "LocalStructure", 0) - 1.1) < 0.001, "Structure did not increase by 0.1.");
});

await RunAsync("Runtime log proves rich path", async () =>
{
    using var temp = new TempDirectory();
    var game = new GameEntry { Name = "Fixture", ExePath = Path.Combine(temp.Path, "FixtureGame.exe") };
    var log = string.Join('\n',
        "env: HIP: 1 device(s), driver 60241134, runtime 70100;",
        "env: swapchain 2560x1440 format 28, test",
        "first ffxDispatch type FFX_API_DISPATCH_UPSCALE_GENERATE_REACTIVE_DESCRIPTION",
        "staging ready: colour 1706x960 dxgi 28 test; motion 1706x960 dxgi 16; depth 1706x960 dxgi 40 (inverted 1); exposure yes; residual on",
        "interop: inputs shared (zero-copy), output shared (zero-copy); mode inline",
        "network job 1 done in 8 ms (6.25 ms network on the GPU, 0.50 ms waiting for the capture; history on, zero-copy)");
    await File.WriteAllTextAsync(game.LogPath, log);
    var result = await new RuntimeDiagnosticsService().InspectAsync(game);
    Assert(result.RichPathObserved, "Rich path was not recognized from the fixture log.");
    Assert(result.InputResolution == "1706×960", "FSR input resolution was not parsed.");
    Assert(result.OutputResolution == "2560×1440", "Output resolution was not parsed.");
    Assert(result.ZeroCopySamples == 1, "Zero-copy job was not counted.");
    Assert(result.LogSha256?.Length == 64, "Log SHA-256 was not recorded.");
});

await RunAsync("PE x64 validation works", () =>
{
    var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
    Assert(GameProbeService.ReadPeMachine(processPath) == 0x8664, "Smoke-test executable is not recognized as AMD64.");
    return Task.CompletedTask;
});

await RunAsync("Anti-cheat overrides an otherwise compatible target", async () =>
{
    using var temp = new TempDirectory();
    var sourceExe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
    var gameExe = Path.Combine(temp.Path, "FixtureGame.exe");
    File.Copy(sourceExe, gameExe);
    await using (var stream = File.Open(gameExe, FileMode.Append, FileAccess.Write, FileShare.None))
    {
        var marker = Encoding.ASCII.GetBytes("d3d12.dll");
        await stream.WriteAsync(marker);
    }
    await File.WriteAllBytesAsync(Path.Combine(temp.Path, "ffx_fsr3upscaler_x64.dll"), [1]);
    await File.WriteAllBytesAsync(Path.Combine(temp.Path, "EAAntiCheat.GameServiceLauncher.exe"), [1]);

    var result = new GameProbeService().Probe(gameExe);
    Assert(result.X64, "Fixture executable should be x64.");
    Assert(result.FsrMarkers.Count > 0, "FSR marker was not detected.");
    Assert(result.Dx12Evidence.Count > 0, "DX12 evidence was not detected.");
    Assert(result.AntiCheatMarkers.Count > 0, "EA AntiCheat marker was not detected.");
});

await RunAsync("SHA-256 helper is deterministic", async () =>
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "hash.bin");
    var bytes = Encoding.UTF8.GetBytes("DLSS5 AMD Swapper smoke test");
    await File.WriteAllBytesAsync(path, bytes);
    var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var actual = await DirectGameInstallerService.Sha256Async(path);
    Assert(actual == expected, "SHA-256 mismatch.");
});

await RunAsync("Game executable picker rejects launcher noise", async () =>
{
    using var temp = new TempDirectory();
    var root = Path.Combine(temp.Path, "Example Game");
    var binaries = Path.Combine(root, "Binaries", "Win64");
    Directory.CreateDirectory(binaries);
    var sourceExe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
    var gameExe = Path.Combine(binaries, "ExampleGame-Win64-Shipping.exe");
    File.Copy(sourceExe, gameExe);
    await File.WriteAllBytesAsync(Path.Combine(root, "ExampleGameLauncher.exe"), [1, 2, 3]);
    var picked = new GameProbeService().FindBestExecutable(root, "Example Game", lenient: true);
    Assert(string.Equals(picked, gameExe, StringComparison.OrdinalIgnoreCase), $"Expected the game executable, got {picked ?? "null"}.");
});

if (args.Any(arg => arg.Equals("--scan-games", StringComparison.OrdinalIgnoreCase)))
{
    await RunAsync("Universal installed-game scan", async () =>
    {
        var progress = new Progress<string>(message => Console.WriteLine("SCAN  " + message));
        var games = await new GameDiscoveryService(new GameProbeService()).DiscoverAsync(includeHeuristics: true, progress: progress);
        Assert(games.Count > 0, "No installed games were discovered on this machine.");
        Assert(games.All(game => File.Exists(game.ExePath)), "Discovery returned a missing executable.");
        var duplicateRoots = games.GroupBy(game => Path.GetFullPath(game.RootPath), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToArray();
        Assert(duplicateRoots.Length == 0, "Discovery returned duplicate install roots.");
        Console.WriteLine($"SCAN  discovered {games.Count} game(s): " + string.Join(", ", games.GroupBy(game => game.Store).OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}")));
    });
}

var losslessArg = args.FirstOrDefault(arg => arg.StartsWith("--lossless-root=", StringComparison.OrdinalIgnoreCase));
if (losslessArg is not null)
{
    var root = losslessArg[(losslessArg.IndexOf('=') + 1)..];
    await RunAsync("Installed Lossless Scaling bridge is recognized", () =>
    {
        var status = new LosslessScalingService().GetStatus(root);
        Assert(status.Found, "Lossless Scaling install was not detected.");
        Assert(status.Installed, "Existing managed bridge was not detected.");
        Assert(status.NeuralMaxHeight == 480, $"Expected the current neural cap to be 480, got {status.NeuralMaxHeight?.ToString() ?? "null"}.");
        Assert(!string.IsNullOrWhiteSpace(status.BridgePath) && File.Exists(status.BridgePath), "Configured bridge executable was not resolved.");
        return Task.CompletedTask;
    });
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAILED: {failures.Count} smoke test(s)");
    foreach (var failure in failures) Console.Error.WriteLine(" - " + failure);
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine("All smoke tests passed.");
}

return;

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine("PASS  " + name);
    }
    catch (Exception ex)
    {
        failures.Add(name + ": " + ex.Message);
        Console.WriteLine("FAIL  " + name);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dlss5-amd-swapper-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
