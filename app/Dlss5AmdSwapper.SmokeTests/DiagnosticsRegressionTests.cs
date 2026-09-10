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
            Require(result.Sampled && result.RichPathObserved, "Large-log sample lost startup/recent evidence.");
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
