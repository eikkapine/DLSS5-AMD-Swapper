using System.Text.Json;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

internal static class DiagnosticsReportTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Post-FSR export excludes raw failed-hook log text", () =>
        {
            var game = new GameEntry { Name = "PrivateGame", ExePath = @"X:\PrivateFolder\PrivateGame.exe" };
            var evidence = RuntimeDiagnostics.Empty("Private summary") with
            {
                HookFailures = 1, FailedHooks = [@"X:\PrivateFolder\PrivateLibrary.dll:CreateSwapChain"]
            };
            var report = DiagnosticsReportService.Create(game, evidence);
            using var parsed = JsonDocument.Parse(report);
            if (parsed.RootElement.GetProperty("evidence").GetProperty("HookFailures").GetInt32() != 1
                || report.Contains("Private", StringComparison.Ordinal))
                throw new InvalidOperationException("Post-FSR report lost hook failure count or leaked raw log text.");
            return Task.CompletedTask;
        });
        await run("Pre-SR export uses route evidence and excludes private log text", () =>
        {
            var game = new GameEntry
            {
                Name = "PrivateGame", ExePath = @"X:\PrivateFolder\PrivateGame.exe",
                Route = InstallRoute.OptiScalerPreSr, Installed = true, X64 = true
            };
            var evidence = new OptiScalerDiagnostics(false, "PrivateAdapter", 1, null, "640x360", "1280x720",
                null, null, 0, @"LoadLibrary failed X:\PrivateFolder\PrivateLibrary.dll",
                new string('a', 64), 256, new string('b', 64), 512);
            var report = DiagnosticsReportService.Create(game, evidence);
            using var parsed = JsonDocument.Parse(report);
            var root = parsed.RootElement;
            if (root.GetProperty("route").GetString() != "OptiScaler pre-SR")
                throw new InvalidOperationException("Report used the wrong install route.");
            var fields = root.GetProperty("evidence");
            if (fields.GetProperty("PreSrActive").GetBoolean() || !fields.GetProperty("faultObserved").GetBoolean())
                throw new InvalidOperationException("Report lost the failed pre-SR evidence.");
            if (fields.GetProperty("OptiLogBytes").GetInt64() != 512 || fields.TryGetProperty("RichPathObserved", out _))
                throw new InvalidOperationException("Report mixed logs from different routes.");
            if (report.Contains("Private", StringComparison.Ordinal))
                throw new InvalidOperationException("Report leaked private game, adapter, path or raw fault text.");
            return Task.CompletedTask;
        });
    }
}
