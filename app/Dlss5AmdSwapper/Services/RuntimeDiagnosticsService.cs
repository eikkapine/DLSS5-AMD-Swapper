using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class RuntimeDiagnosticsService
{
    public async Task<RuntimeDiagnostics> InspectAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(game.LogPath)) return RuntimeDiagnostics.Empty(game.Running ? "Waiting for runtime log" : "Launch the game to collect runtime evidence");
        byte[] raw;
        await using (var stream = File.Open(game.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            raw = new byte[stream.Length];
            var offset = 0;
            while (offset < raw.Length)
            {
                var read = await stream.ReadAsync(raw.AsMemory(offset), cancellationToken);
                if (read == 0) break;
                offset += read;
            }
            if (offset != raw.Length) Array.Resize(ref raw, offset);
        }

        var text = System.Text.Encoding.UTF8.GetString(raw);
        var dispatch = Regex.IsMatch(text, @"(?im)^first ffxDispatch type ");
        var faults = Regex.Matches(text, @"(?im)^FAULT:").Count;
        var gpuErrors = Regex.Matches(text, @"(?im)^job \d+ GPU errors:").Count;
        var jobMatches = Regex.Matches(text, @"network job (\d+) done in (\d+) ms \(([\d.]+) ms network on the GPU, ([\d.]+) ms waiting for the capture; history (on|off), (zero-copy|copied)\)", RegexOptions.IgnoreCase);
        var gpuTimes = new List<double>();
        var zeroCopySamples = 0;
        foreach (Match match in jobMatches)
        {
            if (double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gpu)) gpuTimes.Add(gpu);
            if (match.Groups[6].Value.Equals("zero-copy", StringComparison.OrdinalIgnoreCase)) zeroCopySamples++;
        }

        var stage = Regex.Match(text, @"staging ready: colour (\d+)x(\d+) dxgi \d+ .*?; motion (\d+)x(\d+) dxgi \d+; depth (\d+)x(\d+) dxgi \d+ \(inverted (\d+)\); exposure (yes|no); residual (on|off)", RegexOptions.IgnoreCase);
        var swap = Regex.Match(text, @"env: swapchain (\d+)x(\d+) format \d+,", RegexOptions.IgnoreCase);
        var interop = Regex.Match(text, @"interop: inputs shared \((zero-copy|copied)\), output shared \((zero-copy|copied)\); mode ([^\r\n]+)", RegexOptions.IgnoreCase);
        var hip = Regex.Match(text, @"env: HIP: (\d+) device\(s\), driver (\d+), runtime (\d+);", RegexOptions.IgnoreCase);

        string? inputResolution = null;
        string? outputResolution = null;
        var fullOutputInput = false;
        if (stage.Success)
            inputResolution = $"{stage.Groups[1].Value}×{stage.Groups[2].Value}";
        if (swap.Success)
            outputResolution = $"{swap.Groups[1].Value}×{swap.Groups[2].Value}";
        if (stage.Success && swap.Success)
            fullOutputInput = stage.Groups[1].Value == swap.Groups[1].Value && stage.Groups[2].Value == swap.Groups[2].Value;

        var rich = dispatch && stage.Success && jobMatches.Count > 0;
        var hash = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        var summary = rich
            ? faults + gpuErrors == 0 ? "Rich AMD Neural Rendering path observed" : "Rich path observed with runtime errors"
            : dispatch ? "FidelityFX hook observed; waiting for complete neural jobs" : "No rich FidelityFX neural dispatch observed yet";

        return new RuntimeDiagnostics(
            rich, summary, inputResolution, outputResolution, fullOutputInput,
            interop.Success ? $"{interop.Groups[1].Value} in / {interop.Groups[2].Value} out · {interop.Groups[3].Value.Trim()}" : null,
            hip.Success ? $"{hip.Groups[1].Value} device(s) · runtime {hip.Groups[3].Value}" : null,
            jobMatches.Count,
            gpuTimes.Count > 0 ? gpuTimes.Average() : null,
            zeroCopySamples,
            faults,
            gpuErrors,
            hash);
    }
}

public sealed record RuntimeDiagnostics(
    bool RichPathObserved,
    string Summary,
    string? InputResolution,
    string? OutputResolution,
    bool FullOutputResolutionInput,
    string? Interop,
    string? HipRuntime,
    int TimedJobs,
    double? MeanNetworkGpuMs,
    int ZeroCopySamples,
    int FaultLines,
    int GpuErrorLines,
    string? LogSha256)
{
    public static RuntimeDiagnostics Empty(string summary) => new(false, summary, null, null, false, null, null, 0, null, 0, 0, 0, null);
}
