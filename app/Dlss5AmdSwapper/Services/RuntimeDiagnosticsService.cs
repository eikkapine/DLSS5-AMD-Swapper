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
            // The head belongs to the oldest session in the file. Never stitch it in front of the
            // tail: a stale successful startup must not be combined with a newer session's lines.
            var firstBreak = text.IndexOf('\n');
            text = firstBreak >= 0 ? text[(firstBreak + 1)..] : string.Empty;
        }
        var executableName = Path.GetFileName(game.ExePath);
        var sessionPattern = $@"(?im)^dlssnr_amd (?<version>v[^\s]+).* loaded into {Regex.Escape(executableName)}(?:\s|$).*";
        var sessionMatches = Regex.Matches(text, sessionPattern);
        string? runtimeVersion = null;
        var sessionScoped = sessionMatches.Count > 0;
        var sessionStartOutsideSample = sampled && !sessionScoped;
        if (sessionScoped)
        {
            var latestSession = sessionMatches[^1];
            runtimeVersion = latestSession.Groups["version"].Value;
            text = text[latestSession.Index..];
        }

        var engineInitialized = Regex.IsMatch(text, @"(?im)^engine init ok\s*$");
        var dispatch = Regex.IsMatch(text, @"(?im)^first ffxDispatch type ");
        var presentQueue = Regex.IsMatch(text, @"(?im)^present queue ");
        var gameDevice = Regex.IsMatch(text, @"(?im)^device .*\(from the first presented swapchain\)\s*$");
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

        var hookFailureMatches = sessionScoped ? Regex.Matches(text, @"(?im)^detour of (.+) failed \((-?\d+)\)") : null;
        var hookFailures = hookFailureMatches?.Count ?? 0;
        var failedHooks = hookFailureMatches != null ? hookFailureMatches.Select(m => m.Groups[1].Value.Trim()).ToArray() : Array.Empty<string>();
        var swapchainsCreated = sessionScoped ? Regex.Matches(text, @"(?im)^swapchain .* created on queue").Count : 0;
        var crashpadMatch = sessionScoped ? Regex.Match(text, @"(?im)^.*loaded into crashpad_handler\.exe") : Match.Empty;
        var preCrashpadText = crashpadMatch.Success ? text[..crashpadMatch.Index] : text;
        var hooksInstalled = sessionScoped ? Regex.Matches(preCrashpadText, @"(?im)^hooked ").Count : 0;

        // Without the session header in the sample the startup lines cannot be attributed to the
        // recent jobs, so no verified verdict is possible from the sample alone.
        var rich = !sessionStartOutsideSample && dispatch && stage.Success && jobMatches.Count > 0;
        var startupStalled = sessionScoped && !engineInitialized && (presentQueue || gameDevice);
        var hooksFailed = sessionScoped && hookFailures > 0 && !engineInitialized;
        var summary = sessionStartOutsideSample
            ? jobMatches.Count > 0
                ? "Recent neural jobs observed, but the session start is outside the sampled window; restart the game or inspect the full log for a verified verdict"
                : "Session start is outside the sampled window; no verdict from the recent sample"
            : rich
            ? faults + gpuErrors == 0 ? "Rich AMD Neural Rendering path observed" : "Rich path observed with runtime errors"
            : dispatch ? "FidelityFX hook observed; waiting for complete neural jobs"
            : hooksFailed ? $"AMD runtime {runtimeVersion} loaded, but {hookFailures} render hook(s) failed to install ({string.Join(", ", failedHooks)}); no frame reached the runtime, so Neural Rendering cannot activate in this launch" + (faults > 0 ? "; startup faults were logged" : string.Empty)
            : startupStalled && faults > 0 ? $"AMD runtime {runtimeVersion} reached game rendering but stalled before neural engine initialization; startup faults were logged"
            : startupStalled ? $"AMD runtime {runtimeVersion} reached game rendering but stalled before neural engine initialization"
            : sessionScoped && !engineInitialized ? $"AMD runtime {runtimeVersion} loaded; neural engine has not initialized in the latest session"
            : engineInitialized ? "AMD neural engine initialized; waiting for FidelityFX dispatch"
            : "No rich FidelityFX neural dispatch observed in sampled evidence";
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
            hash)
        {
            Sampled = sampled,
            BytesHashed = total,
            SessionScoped = sessionScoped,
            EngineInitialized = engineInitialized,
            FidelityFxDispatchObserved = dispatch,
            PresentQueueObserved = presentQueue,
            StartupStalled = startupStalled,
            SessionStartOutsideSample = sessionStartOutsideSample,
            HookFailures = hookFailures,
            FailedHooks = failedHooks,
            SwapchainsCreated = swapchainsCreated,
            HooksInstalled = hooksInstalled
        };
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
    public bool SessionScoped { get; init; }
    public bool EngineInitialized { get; init; }
    public bool FidelityFxDispatchObserved { get; init; }
    public bool PresentQueueObserved { get; init; }
    public bool StartupStalled { get; init; }
    public bool SessionStartOutsideSample { get; init; }
    public int HookFailures { get; init; }
    public string[] FailedHooks { get; init; } = Array.Empty<string>();
    public int SwapchainsCreated { get; init; }
    public int HooksInstalled { get; init; }
    public bool HooksFailed => HookFailures > 0;
    public long BytesHashed { get; init; }
    public string EvidenceScope => SessionStartOutsideSample
        ? "Recent 1 MiB sample only; the session header is outside the sample"
        : SessionScoped
        ? Sampled ? "Latest visible runtime session in startup/recent sample; counts and GPU mean cover that session only" : "Latest runtime session in the captured log; counts and GPU mean cover that session only"
        : Sampled ? "Startup and recent log sample (up to 128 KiB + 1 MiB); counts and GPU mean cover sampled lines only" : "Complete captured log; GPU mean covers logged neural jobs only";
    public static RuntimeDiagnostics Empty(string summary) => new(false, summary, null, null, false, null, null, 0, null, 0, 0, 0, null);
}
