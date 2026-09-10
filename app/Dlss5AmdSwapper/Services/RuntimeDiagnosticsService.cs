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
        // Hash a fixed-length snapshot without allocating the complete (potentially huge) log.
        // Parse only bounded startup/recent evidence; this is not a full-session benchmark.
        const int headLimit = 128 * 1024;
        const int tailLimit = 1024 * 1024;
        var head = new byte[headLimit];
        var tail = new byte[tailLimit];
        var buffer = new byte[64 * 1024];
        long total = 0;
        int tailPosition = 0;
        string hash;
        await using (var stream = File.Open(game.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var snapshotLength = stream.Length;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (total < snapshotLength)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, snapshotLength - total)), cancellationToken);
                if (read == 0) break;
                hasher.AppendData(buffer, 0, read);
                if (total < headLimit) Array.Copy(buffer, 0, head, (int)total, (int)Math.Min(read, headLimit - total));
                int first = Math.Min(read, tailLimit - tailPosition);
                Array.Copy(buffer, 0, tail, tailPosition, first);
                if (read > first) Array.Copy(buffer, first, tail, 0, read - first);
                tailPosition = (tailPosition + read) % tailLimit;
                total += read;
            }
            hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        var tailCount = (int)Math.Min(total, tailLimit);
        var orderedTail = new byte[tailCount];
        var start = total > tailLimit ? tailPosition : 0;
        var firstPart = Math.Min(tailCount, tailLimit - start);
        Array.Copy(tail, start, orderedTail, 0, firstPart);
        if (tailCount > firstPart) Array.Copy(tail, 0, orderedTail, firstPart, tailCount - firstPart);
        var text = System.Text.Encoding.UTF8.GetString(orderedTail);
        var sampled = total > tailLimit;
        if (sampled)
        {
            // Discard incomplete boundary lines and avoid overlap between head and tail.
            var prefix = System.Text.Encoding.UTF8.GetString(head, 0, (int)Math.Min(headLimit, total - tailLimit));
            var lastBreak = prefix.LastIndexOf('\n');
            prefix = lastBreak >= 0 ? prefix[..(lastBreak + 1)] : string.Empty;
            var firstBreak = text.IndexOf('\n');
            text = prefix + "\n" + (firstBreak >= 0 ? text[(firstBreak + 1)..] : string.Empty);
        }
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
        var summary = rich
            ? faults + gpuErrors == 0 ? "Rich AMD Neural Rendering path observed" : "Rich path observed with runtime errors"
            : dispatch ? "FidelityFX hook observed; waiting for complete neural jobs" : "No rich FidelityFX neural dispatch observed in sampled evidence";
        if (sampled) summary += " · startup/recent log sample";

        return new RuntimeDiagnostics(
            rich, summary, inputResolution, outputResolution, fullOutputInput,
            interop.Success ? $"{interop.Groups[1].Value} in / {interop.Groups[2].Value} out · {interop.Groups[3].Value.Trim()}" : null,
            hip.Success ? $"{hip.Groups[1].Value} device(s) · runtime {hip.Groups[3].Value}" : null,
            jobMatches.Count,
            gpuTimes.Count > 0 ? gpuTimes.Average() : null,
            zeroCopySamples,
            faults,
            gpuErrors,
            hash) { Sampled = sampled, BytesHashed = total };
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
    public bool Sampled { get; init; }
    public long BytesHashed { get; init; }
    public string EvidenceScope => Sampled ? "Startup and recent log sample (up to 128 KiB + 1 MiB); counts and GPU mean cover sampled lines only" : "Complete captured log; GPU mean covers logged neural jobs only";
    public static RuntimeDiagnostics Empty(string summary) => new(false, summary, null, null, false, null, null, 0, null, 0, 0, 0, null);
}
