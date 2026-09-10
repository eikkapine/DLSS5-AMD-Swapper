using System.Text.Json;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public static class DiagnosticsReportService
{
    // Allowlist structured fields. Raw log lines, game names, paths, machine identifiers,
    // environment variables, configuration files and local activity are deliberately absent.
    public static string Create(GameEntry game, RuntimeDiagnostics evidence) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        app = "DLSS5 AMD Swapper",
        version = typeof(DiagnosticsReportService).Assembly.GetName().Version?.ToString(),
        capturedUtc = DateTimeOffset.UtcNow,
        route = "Direct Game",
        compatibility = new { game.X64, game.HasFsr, game.HasDx12, game.HasAntiCheat },
        state = new { game.Installed, game.Enabled, game.Running, game.LiveAcknowledged },
        evidence = new
        {
            evidence.RichPathObserved, evidence.EvidenceScope, evidence.BytesHashed,
            evidence.InputResolution, evidence.OutputResolution, evidence.FullOutputResolutionInput,
            evidence.TimedJobs, evidence.MeanNetworkGpuMs, evidence.ZeroCopySamples,
            evidence.FaultLines, evidence.GpuErrorLines, evidence.LogSha256,
            timingMeaning = "Mean GPU time of logged neural network jobs; not game FPS or end-to-end frame time",
            hashMeaning = "SHA-256 of bytes read from the captured log length; an active log may change during capture"
        }
    }, new JsonSerializerOptions { WriteIndented = true });
}
