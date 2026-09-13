using System.Diagnostics;
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
        var presr = await ReadTailAndHashAsync(game.PreSrLogPath, cancellationToken);
        var opti = await ReadTailAndHashAsync(game.OptiScalerLogPath, cancellationToken);
        var missing = ManagedManifest.ReadRoute(game) == InstallRoute.OptiScalerPreSr
            ? OptiScalerInstallerService.GetMissingRequiredFiles(game) : null;
        var processStarted = GetProcessStartUtc(game);
        var manifestPath = ManagedManifest.FindManifestPath(game);
        DateTime? installedAt = null;
        if (manifestPath is not null)
        {
            try
            {
                var manifest = await OptiScalerInstallerService.ReadManifestAsync(manifestPath, cancellationToken);
                if (manifest?.CreatedUnix > 0) installedAt = DateTimeOffset.FromUnixTimeSeconds(manifest.CreatedUnix).UtcDateTime;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
        }

        // A new proxy launch need not touch amd_presr.log if no SR dispatch is reached.
        // A previous successful pass cannot prove that this launch works.
        var earliest = new[] { processStarted, installedAt, SessionStartUtc(opti) }
            .Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty(DateTime.MinValue).Max();
        var presrCurrent = presr.LastWriteUtc >= earliest;
        var optiCurrent = opti.LastWriteUtc >= (processStarted ?? installedAt ?? DateTime.MinValue);
        var scoped = presrCurrent
            ? ScopePreSrSession(presr.Text, presr.LastWriteUtc, earliest, DateTime.UtcNow, Environment.TickCount64)
            : (Text: string.Empty, Correlated: false);
        var result = ParseCore(scoped.Text, optiCurrent ? opti.Text : string.Empty, scoped.Correlated);
        return result with
        {
            PreSrActive = result.PreSrActive && missing is not { Count: > 0 }
                && (!optiCurrent || opti.SessionHeader is null || result.CostSamples > 0 || scoped.Correlated),
            LoaderObserved = optiCurrent && (result.LoaderObserved || opti.SessionHeader is not null),
            PreSrLogSha256 = presr.Sha256, PreSrLogBytes = presr.Bytes,
            OptiLogSha256 = opti.Sha256, OptiLogBytes = opti.Bytes,
            HistoricalEvidence = (!presrCurrent && presr.Bytes > 0) || (!optiCurrent && opti.Bytes > 0),
            InstallComplete = missing is null ? null : missing.Count == 0,
            MissingFiles = missing ?? [],
            ActivationGuidance = OptiScalerInstallerService.GetActivationGuidance(game)
        };
    }

    public static OptiScalerDiagnostics Parse(string presrLog, string optiLog) => ParseCore(presrLog, optiLog, false);

    private static OptiScalerDiagnostics ParseCore(string presrLog, string optiLog, bool preSrSessionCorrelated)
    {
        presrLog = LatestSession(presrLog, PreSrSession());
        optiLog = LatestSession(optiLog, OptiSession());
        var hasOptiSession = OptiSession().IsMatch(optiLog);
        var adapter = HipAdapter().Matches(presrLog).Cast<Match>().LastOrDefault();
        var initialized = PassInitialized().Matches(presrLog).Cast<Match>().Select(match => match.Value).Distinct().Count();
        int? completed = null;
        var lastCompleted = -1;
        foreach (Match match in PassesCompleted().Matches(presrLog))
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var passes) && passes > 0)
            { completed = passes; lastCompleted = match.Index; }
        var running = Running().Matches(optiLog).Cast<Match>().LastOrDefault();
        var presrFault = LastFaultLine(presrLog);
        var optiFault = LastFaultLine(optiLog);
        var costs = new List<(double Total, double Model)>();
        foreach (Match match in Cost().Matches(optiLog))
            if (match.Index > optiFault.Index
                && double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var total)
                && double.TryParse(match.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var model)
                && double.IsFinite(total) && double.IsFinite(model) && total > 0 && model > 0) costs.Add((total, model));
        var presrStalled = presrFault.Index > lastCompleted;
        var optiStalled = optiFault.Index >= 0 && costs.Count == 0;
        var stalled = presrStalled || optiStalled;
        // Brief successful runs may only write the AMD completion, without a cost sample.
        // Accept that only when its timestamps identify this launch; unscoped input remains conservative.
        var active = !stalled && (costs.Count > 0 || ((!hasOptiSession || preSrSessionCorrelated) && lastCompleted >= 0));
        return new OptiScalerDiagnostics(
            PreSrActive: active,
            HipAdapter: adapter?.Groups[1].Value.Trim(),
            PassesInitialized: initialized,
            PassesCompleted: completed,
            ModelSize: running?.Groups[2].Value,
            TargetSize: running?.Groups[1].Value,
            MeanTotalMs: costs.Count == 0 ? null : costs.Average(cost => cost.Total),
            MeanModelMs: costs.Count == 0 ? null : costs.Average(cost => cost.Model),
            CostSamples: costs.Count,
            LastFault: presrStalled ? presrFault.Line : optiStalled ? optiFault.Line : presrFault.Line ?? optiFault.Line,
            PreSrLogSha256: null, PreSrLogBytes: 0, OptiLogSha256: null, OptiLogBytes: 0)
        {
            LoaderObserved = hasOptiSession,
            UpscalerObserved = UpscalerContext().IsMatch(optiLog),
            HasLogEvidence = presrLog.Length > 0 || optiLog.Length > 0,
            FsrInputsDisabled = optiLog.Contains("Disable FSR 3.0 Inputs", StringComparison.Ordinal) || optiLog.Contains("Disable FSR 2.X Inputs", StringComparison.Ordinal),
            InputHookWarning = optiLog.Contains("WndProc is not subclassed", StringComparison.Ordinal) || optiLog.Contains("subclass lost", StringComparison.Ordinal)
        };
    }

    internal static (string Text, bool Correlated) ScopePreSrSession(string text, DateTime lastWriteUtc, DateTime sessionStartUtc, DateTime observedUtc, long uptimeMs)
    {
        text = LatestSession(text, PreSrSession());
        if (sessionStartUtc == DateTime.MinValue || uptimeMs < 0) return (text, false);
        var startup = PreSrSession().Match(text);
        if (!startup.Success || !long.TryParse(startup.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var startupTick)) return (text, false);
        var bootUtc = observedUtc.AddMilliseconds(-uptimeMs);
        if (lastWriteUtc < bootUtc || sessionStartUtc < bootUtc) return (text, false);
        var stamped = new List<(Match Match, long Tick)>();
        foreach (Match match in TickLine().Matches(text))
        {
            if (!long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tick)) return (text, false);
            stamped.Add((match, tick));
        }
        if (stamped.Count == 0) return (text, false);
        var latest = stamped[^1].Tick;
        if (latest > uptimeMs || latest < 0) return (text, false);
        // Observed AMD builds prefix lines with boot-relative milliseconds. Validate the
        // clock against file metadata before using it, so old-boot logs or another clock
        // format cannot become evidence merely because they contain a numeric prefix.
        var mappedWrite = observedUtc.AddMilliseconds(latest - uptimeMs);
        if (Math.Abs((lastWriteUtc - mappedWrite).TotalSeconds) > 10) return (text, false);
        var cutoff = (sessionStartUtc - bootUtc).TotalMilliseconds;
        if (startupTick < cutoff || stamped.Any(line => line.Tick < cutoff || line.Tick > uptimeMs)) return (text, false);
        // Keep untimed lines: filtering to numeric prefixes would silently discard a
        // later fault. A completion itself still needs the validated clock prefix.
        if (PassesCompleted().Matches(text).Count != StampedCompletion().Matches(text).Count) return (text, false);
        return (text, true);
    }

    private static string LatestSession(string text, Regex marker)
    {
        var last = marker.Matches(text).Cast<Match>().LastOrDefault();
        return last is null ? text : text[last.Index..];
    }

    private static (int Index, string? Line) LastFaultLine(string text)
    {
        var last = Fault().Matches(text).Cast<Match>().LastOrDefault();
        if (last is null) return (-1, null);
        var start = text.LastIndexOf('\n', last.Index);
        var end = text.IndexOf('\n', last.Index);
        return (last.Index, text[(start + 1)..(end < 0 ? text.Length : end)].Trim());
    }

    private sealed record LogSnapshot(string Text, string? Sha256, long Bytes, DateTime LastWriteUtc, string? SessionHeader);

    private static async Task<LogSnapshot> ReadTailAndHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var lastWrite = File.GetLastWriteTimeUtc(path);
            var snapshotLength = stream.Length;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            var tail = new byte[TailBytes];
            var stored = 0;
            long total = 0;
            string? session = null;
            var carry = string.Empty;
            while (total < snapshotLength)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, snapshotLength - total)), cancellationToken);
                if (count == 0) break;
                sha.AppendData(buffer, 0, count);
                total += count;
                if (stored + count > tail.Length)
                {
                    var retained = tail.Length - count;
                    Buffer.BlockCopy(tail, stored - retained, tail, 0, retained);
                    stored = retained;
                }
                Buffer.BlockCopy(buffer, 0, tail, stored, count);
                stored += count;
                var chunk = carry + Encoding.UTF8.GetString(buffer, 0, count);
                var last = OptiSession().Matches(chunk).Cast<Match>().LastOrDefault();
                if (last is not null) session = last.Value;
                carry = chunk[^Math.Min(chunk.Length, 2048)..];
            }
            var text = Encoding.UTF8.GetString(tail, 0, stored);
            if (total > stored)
            {
                var firstLine = text.IndexOf('\n');
                text = firstLine < 0 ? string.Empty : text[(firstLine + 1)..];
            }
            return new(text, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), total, lastWrite, session);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new(string.Empty, null, 0, DateTime.MinValue, null); }
    }

    private static DateTime? SessionStartUtc(LogSnapshot log)
    {
        if (log.SessionHeader is null) return null;
        var match = TimePrefix().Match(log.SessionHeader);
        if (!match.Success || !TimeSpan.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var time)) return null;
        var writtenLocal = log.LastWriteUtc.ToLocalTime();
        var start = writtenLocal.Date + time;
        if (start > writtenLocal) start = start.AddDays(-1);
        return start.ToUniversalTime();
    }

    private static DateTime? GetProcessStartUtc(GameEntry game)
    {
        if (!game.Running) return null;
        Process[] processes = [];
        try
        {
            var path = Path.GetFullPath(game.ExePath);
            processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
            foreach (var process in processes)
                {
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) return process.StartTime.ToUniversalTime();
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
        finally { foreach (var process in processes) process.Dispose(); }
        return null;
    }

    [GeneratedRegex(@"(?m)^[^\r\n]*OptiScaler v[^\r\n]* loaded[^\r\n]*")] private static partial Regex OptiSession();
    [GeneratedRegex(@"(?m)^(?:(\d+) )?HIP runtime:[^\r\n]*")] private static partial Regex PreSrSession();
    [GeneratedRegex(@"\[(\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]")] private static partial Regex TimePrefix();
    [GeneratedRegex(@"(?m)^(\d+) [^\r\n]*")] private static partial Regex TickLine();
    [GeneratedRegex(@"HIP adapter:\s*(.+)", RegexOptions.Multiline)] private static partial Regex HipAdapter();
    [GeneratedRegex(@"Initialized independent AMD pass \d+")] private static partial Regex PassInitialized();
    [GeneratedRegex(@"Completed AMD pre-SR passes=(\d+)")] private static partial Regex PassesCompleted();
    [GeneratedRegex(@"(?m)^\d+ Completed AMD pre-SR passes=\d+")] private static partial Regex StampedCompletion();
    [GeneratedRegex(@"DLSS-NR running [^:]*: target (\d+x\d+), model (\d+x\d+)")] private static partial Regex Running();
    [GeneratedRegex(@"DLSS-NR cost: ([\d.]+) ms total = ([\d.]+) ms model")] private static partial Regex Cost();
    // The pre-SR route always outputs through Dx12Upscaler=ffx, so an ffx context/dispatch is
    // what proves the game actually asked for upscaling. Without it there is nothing to run
    // the neural pass before, however healthy the rest of the loader looks.
    [GeneratedRegex(@"ffxCreateContext_Dx12 context created|ffxDispatch_Dx12")] private static partial Regex UpscalerContext();
    [GeneratedRegex(@"(AMD pre-SR: (?!idle|waiting)|HIP completion timeout|Unsupported AMD pre-SR|hash mismatch|weights\.bin is required|LoadLibrary failed|initialization failed|Cannot load amdhip64_7\.dll|AMD stopped|AMD timeout|DLSS-NR[^\r\n]*(?:failed|error))", RegexOptions.IgnoreCase)] private static partial Regex Fault();
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
    public bool LoaderObserved { get; init; }
    public bool UpscalerObserved { get; init; }
    public bool HasLogEvidence { get; init; }
    public bool FsrInputsDisabled { get; init; }
    public bool InputHookWarning { get; init; }
    public bool HistoricalEvidence { get; init; }
    public bool? InstallComplete { get; init; }
    public IReadOnlyList<string> MissingFiles { get; init; } = [];
    public string ActivationGuidance { get; init; } = "Enable a supported DLSS, FSR or XeSS input and press Del to verify Neural Rendering.";

    public string Summary
    {
        get
        {
            if (InstallComplete == false) return "Pre-SR install incomplete: missing " + string.Join(", ", MissingFiles) + ". Use Repair to restore missing files.";
            if (LastFault is not null && !PreSrActive) return "Pre-SR fault: " + LastFault;
            if (!PreSrActive)
            {
                if (HistoricalEvidence && !HasLogEvidence) return "No current-session pre-SR evidence. Previous launch logs were ignored. " + ActivationGuidance;
                // Distinguish "you never switched upscaling on" from "the neural pass failed".
                // Both used to report the same vague dispatch message.
                if (LoaderObserved && !UpscalerObserved)
                    return "OptiScaler loaded, but no upscaler ran this session: the game is still rendering without DLSS, FSR or XeSS, so the image cannot change. "
                        + (FsrInputsDisabled ? "This game's FSR input hooks are disabled; choose DLSS or XeSS, then check Del." : ActivationGuidance);
                if (LoaderObserved && FsrInputsDisabled) return "OptiScaler loaded; neural dispatch not observed. This game's FSR input hooks are disabled; choose DLSS or XeSS, then check Del.";
                if (LoaderObserved) return "OptiScaler loaded; neural dispatch not observed. " + ActivationGuidance;
                return (HasLogEvidence ? "Pre-SR dispatch not observed. " : "No pre-SR log yet. ") + ActivationGuidance;
            }
            var parts = new List<string> { "Pre-SR dispatch observed" };
            if (ModelSize is not null) parts.Add(ModelSize + " model");
            if (TargetSize is not null) parts.Add(TargetSize + " target");
            if (MeanTotalMs is not null) parts.Add(MeanTotalMs.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            if (PassesCompleted is > 0 || PassesInitialized > 0) parts.Add("passes " + (PassesCompleted ?? PassesInitialized));
            if (LastFault is not null) parts.Add("earlier fault: " + LastFault);
            return string.Join(" · ", parts);
        }
    }
}
