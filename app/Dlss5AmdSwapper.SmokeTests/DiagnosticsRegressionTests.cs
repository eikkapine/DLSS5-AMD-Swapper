using System.Security.Cryptography;
using System.Text;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

internal static class DiagnosticsRegressionTests
{
    public static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "swapper-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var game = new GameEntry { ExePath = Path.Combine(folder, "SecretGame.exe"), Name = "PrivateGame" };
            var startup = "first ffxDispatch type TEST\nstaging ready: colour 640x480 dxgi 28 test; motion 640x480 dxgi 16; depth 640x480 dxgi 40 (inverted 1); exposure yes; residual on\nenv: swapchain 1920x1080 format 28,\n";
            var job = "network job 1 done in 8 ms (6.25 ms network on the GPU, 0.50 ms waiting for the capture; history on, zero-copy)\n";
            var content = Encoding.UTF8.GetBytes(startup + new string('x', 2 * 1024 * 1024) + "\n" + job);
            await File.WriteAllBytesAsync(game.LogPath, content);
            var result = await new RuntimeDiagnosticsService().InspectAsync(game);
            // Startup lines that scrolled out of the 1 MiB tail may belong to an older session, so a
            // sampled log without its session header must not be reported as verified.
            Require(result.Sampled && result.SessionStartOutsideSample && !result.RichPathObserved, "Sampled log without a session header claimed a verified verdict.");
            Require(result.Summary.Contains("outside the sampled window", StringComparison.Ordinal), "Sampled log summary did not explain the missing session header.");
            Require(result.TimedJobs == 1 && result.MeanNetworkGpuMs == 6.25, "Recent timing sample was incorrect.");
            Require(result.BytesHashed == content.Length && result.LogSha256 == Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), "Streaming hash differs from exact source bytes.");
            var report = DiagnosticsReportService.Create(game, result with { Interop = folder, HipRuntime = "PRIVATE", Summary = "PrivateGame" });
            Require(!report.Contains("PrivateGame") && !report.Contains(folder) && !report.Contains("PRIVATE") && !report.Contains("SecretGame"), "Untrusted/free-form diagnostic data leaked into report.");
            for (var length = 1024 * 1024 - 5; length <= 1024 * 1024 + 5; length++)
            {
                await File.WriteAllTextAsync(game.LogPath, startup + new string('x', length - startup.Length - job.Length - 1) + "\n" + job);
                var boundary = await new RuntimeDiagnosticsService().InspectAsync(game);
                Require(boundary.TimedJobs == 1, "Head/tail boundary duplicated a timing sample.");
            }
            var noHeaderLog = startup + job;
            await File.WriteAllTextAsync(game.LogPath, noHeaderLog);
            var noHeaderResult = await new RuntimeDiagnosticsService().InspectAsync(game);
            Require(!noHeaderResult.Sampled, "Small log should not be sampled.");
            Require(!noHeaderResult.SessionScoped, "Log without a session header must not be session scoped.");
            Require(!noHeaderResult.RichPathObserved, "Log without a session header claimed a rich verdict.");
            Require(noHeaderResult.Summary.Contains("No runtime session for this executable was found in the log", StringComparison.Ordinal), "Summary did not report missing runtime session header.");
            var oldSession = string.Join('\n',
                "dlssnr_amd v0.2.17 (build old) loaded into SecretGame.exe as winmm.dll",
                "engine init ok",
                "first ffxDispatch type TEST",
                "staging ready: colour 640x480 dxgi 28 test; motion 640x480 dxgi 16; depth 640x480 dxgi 40 (inverted 1); exposure yes; residual on",
                job.TrimEnd());
            var latestSession = string.Join('\n',
                "dlssnr_amd v0.2.18 (build new) loaded into SecretGame.exe as winmm.dll",
                "hooked ID3D12CommandQueue::ExecuteCommandLists",
                "swapchain 1 created on queue 2",
                "device 3 (from the first presented swapchain)",
                "present queue 2 (first direct queue on our device)");
            await File.WriteAllTextAsync(game.LogPath, oldSession + "\n" + latestSession + "\n");
            var current = await new RuntimeDiagnosticsService().InspectAsync(game);
            Require(current.SessionScoped && !current.RichPathObserved && current.TimedJobs == 0, "Old neural jobs leaked into the latest-session verdict.");
            Require(current.StartupStalled && current.PresentQueueObserved && !current.EngineInitialized, "Latest-session D3D12 startup stall was not classified.");
            Require(current.Summary.Contains("v0.2.18", StringComparison.Ordinal) && current.Summary.Contains("stalled before neural engine", StringComparison.Ordinal), "Latest-session startup failure was not surfaced clearly.");

            var faultedSession = latestSession + "\nFAULT: exception 0xc0000005 at 00000001, last job -1\n";
            await File.WriteAllTextAsync(game.LogPath, faultedSession);
            var faulted = await new RuntimeDiagnosticsService().InspectAsync(game);
            Require(faulted.StartupStalled && faulted.FaultLines == 1 && faulted.Summary.Contains("startup faults", StringComparison.Ordinal), "Pre-engine startup faults were not associated with the stalled latest session.");

            var hookFailureFixture = string.Join('\n',
                "dlssnr_amd v0.2.17 (build 976a3fa0) loaded into SecretGame.exe as winmm.dll from X:\\Games\\SecretGame\\bin64\\; log X:\\Games\\SecretGame\\bin64\\dlssnr_on_amd.log; settings X:\\Games\\SecretGame\\bin64\\dlssnr_on_amd.ini",
                "detour of ID3D12CommandQueue::ExecuteCommandLists failed (5)",
                "hooked IDXGIFactory2::CreateSwapChainForHwnd",
                "detour of IDXGIFactory::CreateSwapChain failed (5)",
                "detour of IDXGISwapChain::Present failed (5)",
                "hooked IDXGISwapChain1::Present1",
                "dlssnr_amd v0.2.17 (build 976a3fa0) loaded into crashpad_handler.exe as winmm.dll from X:\\Games\\SecretGame\\bin64\\; log X:\\Games\\SecretGame\\bin64\\dlssnr_on_amd.log; settings X:\\Games\\SecretGame\\bin64\\dlssnr_on_amd.ini",
                "hooked ID3D12CommandQueue::ExecuteCommandLists",
                "hooked IDXGIFactory2::CreateSwapChainForHwnd",
                "hooked IDXGIFactory::CreateSwapChain",
                "hooked IDXGISwapChain::Present",
                "hooked IDXGISwapChain1::Present1",
                "swapchain 0000000071031300 created on queue 0000000070A75760 (device 0000000070776F40)",
                "swapchain 000000010F3E1170 created on queue 00000000DD119140 (device 0000000070776F40)");
            await File.WriteAllTextAsync(game.LogPath, hookFailureFixture);
            var hookDiag = await new RuntimeDiagnosticsService().InspectAsync(game);
            Require(hookDiag.SessionScoped, "Failed-hook session was not scoped.");
            Require(hookDiag.HookFailures == 3, "Hook failures count was not 3.");
            Require(hookDiag.FailedHooks.Contains("IDXGISwapChain::Present"), "Failed hooks did not contain IDXGISwapChain::Present.");
            Require(hookDiag.SwapchainsCreated == 2, "Swapchains created count was not 2.");
            Require(hookDiag.HooksInstalled == 2, "Hooks installed count was not 2 (before crashpad).");
            Require(!hookDiag.RichPathObserved, "Rich path should not be observed.");
            Require(!hookDiag.EngineInitialized, "Engine should not be initialized.");
            Require(!hookDiag.StartupStalled, "StartupStalled should be false when hooks failed before device/present queue.");
            Require(hookDiag.HooksFailed, "HooksFailed should be true.");
            Require(hookDiag.Summary.Contains("3 render hook(s) failed", StringComparison.Ordinal), "Summary did not describe render hook failures.");

            var hookReport = DiagnosticsReportService.Create(game, hookDiag);
            Require(hookReport.Contains("hookFailures", StringComparison.OrdinalIgnoreCase), "Diagnostics report missing hookFailures.");
            Require(!hookReport.Contains("SecretGame") && !hookReport.Contains("X:\\Games"), "Diagnostics report leaked SecretGame or X:\\Games.");
            var history = new ActivityHistoryService(folder);
            for (var i = 0; i < 205; i++) history.Record("Action " + i, new string('z', 1000));
            var restored = new ActivityHistoryService(folder).Read();
            Require(restored.Count == 200 && restored[0].Action == "Action 204" && restored[0].Detail.Length == 600, "History persistence/bounds failed.");
            history.Clear();
            Require(new ActivityHistoryService(folder).Read().Count == 0, "Cleared history reappeared after reload.");
            Console.WriteLine("PASS bounded diagnostics, privacy allowlist and local activity persistence");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
