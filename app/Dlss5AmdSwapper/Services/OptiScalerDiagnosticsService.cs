using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed partial class OptiScalerDiagnosticsService
{
    private const int TailBytes = 1024 * 1024;

    public async Task<OptiScalerDiagnostics> InspectAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        var (presrText, presrHash, presrBytes) = await ReadTailAndHashAsync(game.PreSrLogPath, cancellationToken);
        var (optiText, optiHash, optiBytes) = await ReadTailAndHashAsync(game.OptiScalerLogPath, cancellationToken);
        return Parse(presrText, optiText) with { PreSrLogSha256 = presrHash, PreSrLogBytes = presrBytes, OptiLogSha256 = optiHash, OptiLogBytes = optiBytes };
    }

    public static OptiScalerDiagnostics Parse(string presrLog, string optiLog)
    {
        var adapter = HipAdapter().Match(presrLog);
        var initialized = PassInitialized().Matches(presrLog).Count;
        int? completed = null;
        foreach (Match match in PassesCompleted().Matches(presrLog)) completed = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var running = Running().Matches(optiLog).Cast<Match>().LastOrDefault();
        var costs = Cost().Matches(optiLog).Cast<Match>()
            .Select(match => (Total: double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), Model: double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToArray();
        string? lastFault = null;
        foreach (var line in presrLog.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && Fault().IsMatch(trimmed)) lastFault = trimmed;
        }
        return new OptiScalerDiagnostics(
            PreSrActive: completed is not null || optiLog.Contains("DLSS-NR running", StringComparison.Ordinal),
            HipAdapter: adapter.Success ? adapter.Groups[1].Value.Trim() : null,
            PassesInitialized: initialized,
            PassesCompleted: completed,
            ModelSize: running?.Groups[2].Value,
            TargetSize: running?.Groups[1].Value,
            MeanTotalMs: costs.Length == 0 ? null : costs.Average(cost => cost.Total),
            MeanModelMs: costs.Length == 0 ? null : costs.Average(cost => cost.Model),
            CostSamples: costs.Length,
            LastFault: lastFault,
            PreSrLogSha256: null, PreSrLogBytes: 0, OptiLogSha256: null, OptiLogBytes: 0);
    }

    private static async Task<(string Text, string? Sha256, long Bytes)> ReadTailAndHashAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return (string.Empty, null, 0);
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        var currentLength = stream.Length;
        if (currentLength < length) length = currentLength;
        var tailStart = Math.Max(0, length - TailBytes);
        stream.Position = tailStart;
        var buffer = new byte[length - tailStart];
        int bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            var readCount = await stream.ReadAsync(buffer.AsMemory(bytesRead), cancellationToken);
            if (readCount == 0) break;
            bytesRead += readCount;
        }
        return (Encoding.UTF8.GetString(buffer, 0, bytesRead), Convert.ToHexString(hash).ToLowerInvariant(), length);
    }

    [GeneratedRegex(@"HIP adapter:\s*(.+)", RegexOptions.Multiline)] private static partial Regex HipAdapter();
    [GeneratedRegex(@"Initialized independent AMD pass \d+")] private static partial Regex PassInitialized();
    [GeneratedRegex(@"Completed AMD pre-SR passes=(\d+)")] private static partial Regex PassesCompleted();
    [GeneratedRegex(@"DLSS-NR running [^:]*: target (\d+x\d+), model (\d+x\d+)")] private static partial Regex Running();
    [GeneratedRegex(@"DLSS-NR cost: ([\d.]+) ms total = ([\d.]+) ms model")] private static partial Regex Cost();
    [GeneratedRegex(@"(AMD pre-SR: (?!idle)|HIP completion timeout|Unsupported AMD pre-SR|hash mismatch|weights\.bin is required|LoadLibrary failed|initialization failed|Cannot load amdhip64_7\.dll|AMD stopped|AMD timeout)")] private static partial Regex Fault();
}

public sealed record OptiScalerDiagnostics(
    bool PreSrActive,
    string? HipAdapter,
    int PassesInitialized,
    int? PassesCompleted,
    string? ModelSize,
    string? TargetSize,
    double? MeanTotalMs,
    double? MeanModelMs,
    int CostSamples,
    string? LastFault,
    string? PreSrLogSha256,
    long PreSrLogBytes,
    string? OptiLogSha256,
    long OptiLogBytes)
{
    public string Summary
    {
        get
        {
            if (LastFault is not null && !PreSrActive) return "Pre-SR fault: " + LastFault;
            if (!PreSrActive) return PreSrLogBytes == 0 && OptiLogBytes == 0 ? "No pre-SR log yet. Launch the game with FSR enabled." : "Pre-SR not active yet.";
            var parts = new List<string> { "Pre-SR active" };
            if (ModelSize is not null) parts.Add(ModelSize + " model");
            if (TargetSize is not null) parts.Add(TargetSize + " target");
            if (MeanTotalMs is not null) parts.Add(MeanTotalMs.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            parts.Add("passes " + (PassesCompleted ?? PassesInitialized));
            if (LastFault is not null) parts.Add("last fault: " + LastFault);
            return string.Join(" · ", parts);
        }
    }
}
