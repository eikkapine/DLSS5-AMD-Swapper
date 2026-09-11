# OptiScaler Pre-SR Route Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a second direct-game backend that installs a user-supplied OptiScaler AMD pre-SR multipass package reversibly, expose Lossless Scaling layer controls, and ship `v0.3.0-pre.1` after a live test.

**Architecture:** New `OptiScaler*` services sit beside the existing `DirectGameInstallerService`, share `GameProbeService`, `IniDocument`, `FileState`/`Sha256Async`, and write the same `.dlss5-amd-swapper.json` manifest file with `route: "amd-optiscaler-presr"` (schema 3). A static `ManagedManifest` reader tells every consumer which route owns a game folder. The Python helper mirrors the same manifest JSON. UI adds a route/preset dialog, settings fields, a passes combo, and LS sliders/hotkeys.

**Tech Stack:** .NET 8 WPF (C# 12), custom smoke-test runner in `app/Dlss5AmdSwapper.SmokeTests`, Python 3.14 stdlib + `unittest`, PowerShell 5.1 build scripts.

Spec: `docs/superpowers/specs/2026-09-11-optiscaler-presr-route-design.md`.

## Global Constraints

- Nothing from the reference package (OptiScaler fork, `dlssnr_amd_pass*.dll`, weights, FidelityFX/XeSS DLLs, `dlss-enabler-headless.dll`, package INI/scripts) is ever committed, packaged, or downloaded by this project.
- Proxy names: `dxgi.dll` (default), `version.dll`, `winmm.dll`, `dbghelp.dll`, `wininet.dll`, `winhttp.dll`.
- Manifest file name stays `.dlss5-amd-swapper.json`; pre-SR route string is exactly `amd-optiscaler-presr`; post-FSR stays `amd-fsr-direct`; schema 3 for pre-SR, schema 2 for post-FSR.
- Fork detection: VersionInfo `ProductName == "OptiScaler"` and `ProductVersion` contains `amd-presr` (case-insensitive).
- Pass DLL marker: file bytes contain ASCII `dlssnr_amd`; PE machine `0x8664`.
- Weights are valid only when size > 1,048,576 bytes and the file does not start with `version https://git-lfs`.
- INI keys and values are exactly those in the spec section "Presets and OptiScaler.ini".
- Never write a numeric FPS claim in `.md`/`.txt` (publication gate rejects `\d+\s*fps`). Write "frame rate" or use ms.
- All new smoke tests run through the existing `RunAsync(name, test)` runner in `app/Dlss5AmdSwapper.SmokeTests/Program.cs`.
- Commit after every task with the trailer `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

Run smoke tests:

```powershell
dotnet run --project .\app\Dlss5AmdSwapper.SmokeTests\Dlss5AmdSwapper.SmokeTests.csproj -c Release
```

Build app only (fast compile check):

```powershell
dotnet build .\app\Dlss5AmdSwapper\Dlss5AmdSwapper.csproj -c Release
```

Python tests:

```powershell
py -m unittest discover -s .\direct-game\tests -v
```

## File map

| File | Responsibility |
| --- | --- |
| `app/Dlss5AmdSwapper/Models/InstallRoute.cs` | `InstallRoute` and `OptiScalerPreset` enums + string mapping |
| `app/Dlss5AmdSwapper/Models/OptiScalerPackage.cs` | `OptiScalerPackage`, `LocalWeights`, `PeVersion` records |
| `app/Dlss5AmdSwapper/Services/ManagedManifest.cs` | Reads `route` from a manifest file |
| `app/Dlss5AmdSwapper/Services/OptiScalerPackageService.cs` | Validate, discover, extract zip, find local weights |
| `app/Dlss5AmdSwapper/Services/OptiScalerIniWriter.cs` | Build preset INI text |
| `app/Dlss5AmdSwapper/Services/IniDocument.cs` | + `FromText`, `ToText` |
| `app/Dlss5AmdSwapper/Services/OptiScalerInstallerService.cs` | Install / update / remove, manifest schema 3, rollback |
| `app/Dlss5AmdSwapper/Services/OptiScalerDiagnosticsService.cs` | Parse `amd_presr.log` + `OptiScaler.log` |
| `app/Dlss5AmdSwapper/Services/OptiScalerControlService.cs` | Write `[DlssNr]` keys (passes, enabled, layers) |
| `app/Dlss5AmdSwapper/Services/RuntimeControlService.cs` | Route-aware `Refresh`; INI-path layer API for LS |
| `app/Dlss5AmdSwapper/Services/DirectGameInstallerService.cs` | Route-gate `HasManagedInstall`/`RemoveAsync` |
| `app/Dlss5AmdSwapper/Services/HotkeyService.cs` | Two hotkey sets |
| `app/Dlss5AmdSwapper/Models/GameEntry.cs` | `Route`, `Passes`, new paths/labels |
| `app/Dlss5AmdSwapper/Models/AppSettings.cs` | Package/weights/preset settings |
| `app/Dlss5AmdSwapper/SetupDialog.xaml(.cs)` | Route + preset picker |
| `app/Dlss5AmdSwapper/MainWindow.*` | Wiring, LS sliders, settings fields |
| `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs` | All new C# tests |
| `direct-game/amd_dlss5.py` | CLI mirror |
| `direct-game/tests/test_amd_dlss5.py` | Python tests |
| `tools/Check-Publication.py`, `app/Build-Package.ps1`, `.gitignore` | Publication gate |
| `docs/optiscaler-presr.md`, `docs/licensing.md`, `README.md`, `docs/install.md`, `direct-game/README.md`, `docs/development.md`, `docs/releases/v0.3.0-pre.1.md`, `RELEASE.json`, `VERSION` | Docs and release |

---

### Task 1: Route enums, package model, manifest route reader

**Files:**
- Create: `app/Dlss5AmdSwapper/Models/InstallRoute.cs`
- Create: `app/Dlss5AmdSwapper/Models/OptiScalerPackage.cs`
- Create: `app/Dlss5AmdSwapper/Services/ManagedManifest.cs`
- Create: `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs`
- Modify: `app/Dlss5AmdSwapper.SmokeTests/Program.cs:8` (add `await OptiScalerTests.RunAsync(RunAsync);` after `DiagnosticsRegressionTests`)

**Interfaces:**
- Produces: `enum InstallRoute { None, PostFsrRuntime, OptiScalerPreSr }`, `enum OptiScalerPreset { Quality, Performance }`, `static class InstallRoutes { const string PostFsr = "amd-fsr-direct"; const string OptiScalerPreSr = "amd-optiscaler-presr"; InstallRoute Parse(string?); string ToManifestString(InstallRoute) }`
- Produces: `record PeVersion(string? ProductName, string? ProductVersion)`, `record LocalWeights(string Path, long Size, string Sha256)`, `record OptiScalerPackage(string Root, string OptiScalerDllPath, IReadOnlyList<string> PassDllPaths, string? IniPath, string? DependencyFolder, string? EnablerDllPath, string? WeightsPath, string? Sha256SumsPath, string ForkVersion, IReadOnlyDictionary<string, FileState> Files, bool Sha256SumsVerified, string Layout)` with `string Summary` property.
- Produces: `static class ManagedManifest { InstallRoute ReadRoute(string manifestPath); InstallRoute ReadRoute(GameEntry game); string? FindManifestPath(GameEntry game) }`

- [ ] **Step 1: Write the failing tests**

Create `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs`:

```csharp
using System.Text;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

internal static class OptiScalerTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Manifest route reader distinguishes routes", async () =>
        {
            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            Check(ManagedManifest.ReadRoute(game) == InstallRoute.None, "Missing manifest must be None.");
            await File.WriteAllTextAsync(game.ManifestPath, "{\"route\":\"amd-optiscaler-presr\"}");
            Check(ManagedManifest.ReadRoute(game) == InstallRoute.OptiScalerPreSr, "Pre-SR route not read.");
            await File.WriteAllTextAsync(game.ManifestPath, "{\"schema_version\":2,\"route\":\"amd-fsr-direct\"}");
            Check(ManagedManifest.ReadRoute(game) == InstallRoute.PostFsrRuntime, "Post-FSR route not read.");
            await File.WriteAllTextAsync(game.ManifestPath, "{\"schema_version\":1}");
            Check(ManagedManifest.ReadRoute(game) == InstallRoute.PostFsrRuntime, "Legacy manifest without route must be post-FSR.");
            await File.WriteAllTextAsync(game.ManifestPath, "not json");
            Check(ManagedManifest.ReadRoute(game) == InstallRoute.None, "Corrupt manifest must be None.");
        });

        await run("Route strings round-trip", () =>
        {
            Check(InstallRoutes.Parse("amd-optiscaler-presr") == InstallRoute.OptiScalerPreSr, "parse presr");
            Check(InstallRoutes.Parse("amd-fsr-direct") == InstallRoute.PostFsrRuntime, "parse post-fsr");
            Check(InstallRoutes.Parse(null) == InstallRoute.PostFsrRuntime, "null means legacy post-FSR");
            Check(InstallRoutes.Parse("other") == InstallRoute.None, "unknown is None");
            Check(InstallRoutes.ToManifestString(InstallRoute.OptiScalerPreSr) == "amd-optiscaler-presr", "to string");
            return Task.CompletedTask;
        });
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed class OptiTemp : IDisposable
    {
        public OptiTemp()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dlss5-amd-swapper-opti", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    // Copies the running test executable (a real x64 PE) and appends a marker so
    // PE and marker checks pass without shipping any third-party binary.
    internal static string WritePe(string path, string? appendMarker = null)
    {
        File.Copy(Environment.ProcessPath ?? throw new InvalidOperationException("No process path."), path, true);
        if (appendMarker is not null)
        {
            using var stream = File.Open(path, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(Encoding.ASCII.GetBytes(appendMarker));
        }
        return path;
    }
}
```

In `Program.cs`, after `await DiagnosticsRegressionTests.RunAsync();` add:

```csharp
await OptiScalerTests.RunAsync(RunAsync);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project .\app\Dlss5AmdSwapper.SmokeTests\Dlss5AmdSwapper.SmokeTests.csproj -c Release`
Expected: build error `The name 'ManagedManifest' does not exist` (compile failure counts as failing).

- [ ] **Step 3: Write the models and reader**

`app/Dlss5AmdSwapper/Models/InstallRoute.cs`:

```csharp
namespace Dlss5AmdSwapper.Models;

public enum InstallRoute { None, PostFsrRuntime, OptiScalerPreSr }

public enum OptiScalerPreset { Quality, Performance }

public static class InstallRoutes
{
    public const string PostFsr = "amd-fsr-direct";
    public const string OptiScalerPreSr = "amd-optiscaler-presr";

    // A manifest without a route field predates route support and belongs to the post-FSR installer.
    public static InstallRoute Parse(string? route) => route switch
    {
        null or "" => InstallRoute.PostFsrRuntime,
        PostFsr => InstallRoute.PostFsrRuntime,
        OptiScalerPreSr => InstallRoute.OptiScalerPreSr,
        _ => InstallRoute.None
    };

    public static string ToManifestString(InstallRoute route) => route switch
    {
        InstallRoute.PostFsrRuntime => PostFsr,
        InstallRoute.OptiScalerPreSr => OptiScalerPreSr,
        _ => string.Empty
    };

    public static string Label(InstallRoute route) => route switch
    {
        InstallRoute.PostFsrRuntime => "Post-FSR runtime",
        InstallRoute.OptiScalerPreSr => "OptiScaler pre-SR",
        _ => "Not installed"
    };
}
```

`app/Dlss5AmdSwapper/Models/OptiScalerPackage.cs`:

```csharp
using Dlss5AmdSwapper.Services;

namespace Dlss5AmdSwapper.Models;

public sealed record PeVersion(string? ProductName, string? ProductVersion);

public sealed record LocalWeights(string Path, long Size, string Sha256);

public sealed record OptiScalerPackage(
    string Root,
    string OptiScalerDllPath,
    IReadOnlyList<string> PassDllPaths,
    string? IniPath,
    string? DependencyFolder,
    string? EnablerDllPath,
    string? WeightsPath,
    string? Sha256SumsPath,
    string ForkVersion,
    IReadOnlyDictionary<string, FileState> Files,
    bool Sha256SumsVerified,
    string Layout)
{
    public const string LayoutPackage = "package";
    public const string LayoutVodkaman = "vodkaman";

    public string Summary =>
        $"{ForkVersion} · {Layout} layout · {PassDllPaths.Count} pass DLL(s)" +
        (Sha256SumsPath is null ? " · no SHA256SUMS" : Sha256SumsVerified ? " · SHA256SUMS verified" : " · SHA256SUMS mismatch") +
        (EnablerDllPath is null ? string.Empty : " · enabler present");
}
```

`app/Dlss5AmdSwapper/Services/ManagedManifest.cs`:

```csharp
using System.Text.Json;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public static class ManagedManifest
{
    public static string? FindManifestPath(GameEntry game)
    {
        if (File.Exists(game.ManifestPath)) return game.ManifestPath;
        if (File.Exists(game.LegacyManifestPath)) return game.LegacyManifestPath;
        return null;
    }

    public static InstallRoute ReadRoute(GameEntry game)
    {
        var path = FindManifestPath(game);
        return path is null ? InstallRoute.None : ReadRoute(path);
    }

    public static InstallRoute ReadRoute(string manifestPath)
    {
        try
        {
            using var stream = File.Open(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return InstallRoute.None;
            var route = document.RootElement.TryGetProperty("route", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return InstallRoutes.Parse(route);
        }
        catch (JsonException) { return InstallRoute.None; }
        catch (IOException) { return InstallRoute.None; }
        catch (UnauthorizedAccessException) { return InstallRoute.None; }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project .\app\Dlss5AmdSwapper.SmokeTests\Dlss5AmdSwapper.SmokeTests.csproj -c Release`
Expected: `PASS  Manifest route reader distinguishes routes`, `PASS  Route strings round-trip`, and `All smoke tests passed.`

- [ ] **Step 5: Commit**

```bash
git add app/Dlss5AmdSwapper/Models/InstallRoute.cs app/Dlss5AmdSwapper/Models/OptiScalerPackage.cs app/Dlss5AmdSwapper/Services/ManagedManifest.cs app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs app/Dlss5AmdSwapper.SmokeTests/Program.cs
git commit -m "Add install route enums, OptiScaler package model and manifest route reader

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Package validation

**Files:**
- Create: `app/Dlss5AmdSwapper/Services/OptiScalerPackageService.cs`
- Modify: `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs`

**Interfaces:**
- Consumes: `OptiScalerPackage`, `PeVersion`, `FileState`, `DirectGameInstallerService.Sha256Async`, `GameProbeService.ReadPeMachine`.
- Produces: `OptiScalerPackageService.Validate(string root, Func<string, PeVersion>? versionReader = null) : OptiScalerPackage` (throws `InvalidOperationException` with one actionable sentence), `static bool IsRealWeightsFile(string path)`, `static bool ContainsAsciiMarker(string path, string marker)`, `static PeVersion ReadPeVersion(string path)`.

- [ ] **Step 1: Write the failing tests**

Append inside `RunAsync` in `OptiScalerTests.cs` (before the closing brace of the method):

```csharp
        await run("Package validation accepts the package layout and verifies SHA256SUMS", async () =>
        {
            using var temp = new OptiTemp();
            var root = MakePackage(temp.Path, layout: "package", withSums: true);
            var package = OptiScalerPackageService.Validate(root, FakeFork);
            Check(package.Layout == "package", "layout");
            Check(package.PassDllPaths.Count == 3, "three pass DLLs expected");
            Check(package.Sha256SumsVerified, "SHA256SUMS should verify");
            Check(package.WeightsPath is null, "LFS pointer must not count as weights");
            Check(package.DependencyFolder is not null && Directory.Exists(package.DependencyFolder), "dependency folder");
            Check(package.EnablerDllPath is null, "no enabler in fixture");
            Check(package.Files.ContainsKey("dlssnr_amd_pass1.dll"), "files recorded");
            Check(package.ForkVersion.Contains("amd-presr"), "fork version recorded");
        });

        await run("Package validation accepts the Vodkaman layout", () =>
        {
            using var temp = new OptiTemp();
            WritePe(Path.Combine(temp.Path, "dxgi.dll"));
            WritePe(Path.Combine(temp.Path, "dlssnr_amd_pass1.dll"), "dlssnr_amd");
            var package = OptiScalerPackageService.Validate(temp.Path, FakeFork);
            Check(package.Layout == "vodkaman", "layout");
            Check(Path.GetFileName(package.OptiScalerDllPath) == "dxgi.dll", "dxgi.dll is the fork binary");
            Check(package.PassDllPaths.Count == 1, "single pass DLL");
            Check(package.IniPath is null && package.DependencyFolder is null, "optional parts absent");
            return Task.CompletedTask;
        });

        await run("Package validation rejects a non-fork OptiScaler build", () =>
        {
            using var temp = new OptiTemp();
            MakePackage(temp.Path, layout: "package", withSums: false);
            try { OptiScalerPackageService.Validate(temp.Path, _ => new PeVersion("OptiScaler", "10.0.0-dev (792f2f1)")); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("amd-presr"), "reason must name amd-presr"); }
            return Task.CompletedTask;
        });

        await run("Package validation rejects a SHA256SUMS mismatch and a pass DLL without marker", async () =>
        {
            using var temp = new OptiTemp();
            var root = MakePackage(temp.Path, layout: "package", withSums: true);
            await File.AppendAllTextAsync(Path.Combine(root, "OptiScaler.ini"), "\n; tampered\n");
            try { OptiScalerPackageService.Validate(root, FakeFork); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("OptiScaler.ini"), "mismatch must name the file"); }

            using var temp2 = new OptiTemp();
            WritePe(Path.Combine(temp2.Path, "OptiScaler.dll"));
            WritePe(Path.Combine(temp2.Path, "dlssnr_amd_pass1.dll"));
            try { OptiScalerPackageService.Validate(temp2.Path, FakeFork); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("dlssnr_amd_pass1.dll"), "marker failure must name pass 1"); }
        });

        await run("Weights detection rejects LFS pointers and small files", async () =>
        {
            using var temp = new OptiTemp();
            var pointer = Path.Combine(temp.Path, "dlssnr_on_amd_weights.bin");
            await File.WriteAllTextAsync(pointer, "version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 1\n");
            Check(!OptiScalerPackageService.IsRealWeightsFile(pointer), "LFS pointer accepted");
            await File.WriteAllBytesAsync(pointer, new byte[1024 * 1024 + 1]);
            Check(OptiScalerPackageService.IsRealWeightsFile(pointer), "large binary rejected");
        });
```

Add these helpers to the class (after `WritePe`):

```csharp
    internal static readonly Func<string, PeVersion> FakeFork = _ => new PeVersion("OptiScaler", "10.0.0-dev (amd-presr-multipass-local) (20260907_075847)");

    internal static string MakePackage(string parent, string layout, bool withSums)
    {
        var root = Path.Combine(parent, "OptiScaler-AMD-PreSR-Multipass-v1.2");
        Directory.CreateDirectory(Path.Combine(root, "OptiScaler"));
        WritePe(Path.Combine(root, "OptiScaler.dll"));
        for (var i = 1; i <= 3; i++) WritePe(Path.Combine(root, $"dlssnr_amd_pass{i}.dll"), "dlssnr_amd");
        File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Upscalers]\nDx12Upscaler=auto\n\n[DlssNr]\nEnabled=auto\n");
        File.WriteAllText(Path.Combine(root, "dlssnr_on_amd_weights.bin"), "version https://git-lfs.github.com/spec/v1\noid sha256:6bf8\nsize 147689451\n");
        File.WriteAllBytes(Path.Combine(root, "OptiScaler", "amd_fidelityfx_upscaler_dx12.dll"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "OptiScaler", "libxess.dll"), [4, 5, 6]);
        if (withSums)
        {
            var lines = new List<string>();
            foreach (var relative in new[] { "OptiScaler.dll", "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll", "OptiScaler.ini", "OptiScaler\\amd_fidelityfx_upscaler_dx12.dll", "OptiScaler\\libxess.dll", "MISSING_OPTIONAL.txt" })
            {
                var full = Path.Combine(root, relative);
                var hash = File.Exists(full) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full))) : new string('0', 64);
                lines.Add($"{hash} *{relative}");
            }
            File.WriteAllLines(Path.Combine(root, "SHA256SUMS.txt"), lines);
        }
        return root;
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run the smoke tests. Expected: compile error `OptiScalerPackageService` missing.

- [ ] **Step 3: Implement validation**

`app/Dlss5AmdSwapper/Services/OptiScalerPackageService.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class OptiScalerPackageService
{
    public const string ForkMarker = "amd-presr";
    public const string PassMarker = "dlssnr_amd";
    public const string WeightsName = "dlssnr_on_amd_weights.bin";
    public const string EnablerName = "dlss-enabler-headless.dll";
    public const string DependencyFolderName = "OptiScaler";
    public const string RequiredUpscalerDependency = "amd_fidelityfx_upscaler_dx12.dll";
    public static readonly string[] PassNames = ["dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll"];

    public static OptiScalerPackage Validate(string root, Func<string, PeVersion>? versionReader = null)
    {
        versionReader ??= ReadPeVersion;
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new InvalidOperationException("The OptiScaler package folder does not exist.");

        var packageDll = Path.Combine(root, "OptiScaler.dll");
        var vodkamanDll = Path.Combine(root, "dxgi.dll");
        string fork;
        string layout;
        if (File.Exists(packageDll)) { fork = packageDll; layout = OptiScalerPackage.LayoutPackage; }
        else if (File.Exists(vodkamanDll)) { fork = vodkamanDll; layout = OptiScalerPackage.LayoutVodkaman; }
        else throw new InvalidOperationException("No OptiScaler.dll or dxgi.dll was found in the package folder.");

        if (GameProbeService.ReadPeMachine(fork) != 0x8664)
            throw new InvalidOperationException($"{Path.GetFileName(fork)} is not a 64-bit Windows PE file.");
        var version = versionReader(fork);
        if (!string.Equals(version.ProductName, "OptiScaler", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{Path.GetFileName(fork)} does not identify itself as OptiScaler.");
        if (version.ProductVersion is null || !version.ProductVersion.Contains(ForkMarker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This OptiScaler build is not the AMD pre-SR fork; the pre-SR route needs a build whose version contains amd-presr.");

        var passes = new List<string>();
        foreach (var name in PassNames)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path))
            {
                if (passes.Count == 0) throw new InvalidOperationException("dlssnr_amd_pass1.dll was not found in the package folder.");
                continue;
            }
            if (GameProbeService.ReadPeMachine(path) != 0x8664) throw new InvalidOperationException($"{name} is not a 64-bit Windows PE file.");
            if (!ContainsAsciiMarker(path, PassMarker)) throw new InvalidOperationException($"{name} does not look like a DLSS-NR-on-AMD runtime (marker {PassMarker} missing).");
            passes.Add(path);
        }

        var ini = Path.Combine(root, "OptiScaler.ini");
        var dependencies = Path.Combine(root, DependencyFolderName);
        var enabler = Path.Combine(root, EnablerName);
        var weights = Path.Combine(root, WeightsName);
        var sums = Path.Combine(root, "SHA256SUMS.txt");

        var files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        void Record(string path)
        {
            var relative = Path.GetRelativePath(root, path);
            files[relative] = new FileState(new FileInfo(path).Length, DirectGameInstallerService.Sha256Async(path).GetAwaiter().GetResult());
        }
        Record(fork);
        foreach (var pass in passes) Record(pass);
        if (File.Exists(ini)) Record(ini);
        if (File.Exists(enabler)) Record(enabler);
        if (Directory.Exists(dependencies))
            foreach (var file in Directory.EnumerateFiles(dependencies, "*", SearchOption.AllDirectories)) Record(file);

        var sumsVerified = false;
        if (File.Exists(sums))
        {
            foreach (var (relative, expected) in ParseSha256Sums(sums))
            {
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"SHA256SUMS.txt lists a path outside the package: {relative}");
                if (!File.Exists(full)) continue; // optional file absent; the required ones were checked above
                var actual = files.TryGetValue(Path.GetRelativePath(root, full), out var state) ? state.Sha256 : DirectGameInstallerService.Sha256Async(full).GetAwaiter().GetResult();
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"SHA256SUMS.txt does not match {relative}. Re-download the package before installing.");
            }
            sumsVerified = true;
        }

        return new OptiScalerPackage(
            root, fork, passes,
            File.Exists(ini) ? ini : null,
            Directory.Exists(dependencies) ? dependencies : null,
            File.Exists(enabler) ? enabler : null,
            IsRealWeightsFile(weights) ? weights : null,
            File.Exists(sums) ? sums : null,
            version.ProductVersion,
            files, sumsVerified, layout);
    }

    public static bool IsRealWeightsFile(string path)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length <= 1024 * 1024) return false;
        using var stream = File.OpenRead(path);
        var head = new byte[32];
        var read = stream.Read(head, 0, head.Length);
        return !Encoding.ASCII.GetString(head, 0, read).StartsWith("version https://git-lfs", StringComparison.Ordinal);
    }

    public static bool ContainsAsciiMarker(string path, string marker)
    {
        var needle = Encoding.ASCII.GetBytes(marker);
        using var stream = File.OpenRead(path);
        var buffer = new byte[4 * 1024 * 1024 + needle.Length];
        var carry = 0;
        while (true)
        {
            var read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read == 0) return false;
            var length = carry + read;
            if (buffer.AsSpan(0, length).IndexOf(needle) >= 0) return true;
            carry = Math.Min(needle.Length - 1, length);
            Array.Copy(buffer, length - carry, buffer, 0, carry);
        }
    }

    public static PeVersion ReadPeVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new PeVersion(info.ProductName, info.ProductVersion);
    }

    public static IEnumerable<(string Relative, string Sha256)> ParseSha256Sums(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length < 66) continue;
            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var rest = line[64..].TrimStart(' ', '*');
            if (rest.Length == 0) continue;
            yield return (rest.Replace('/', '\\'), hash);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run the smoke tests. Expected: five new `PASS` lines and `All smoke tests passed.`

- [ ] **Step 5: Commit**

```bash
git add app/Dlss5AmdSwapper/Services/OptiScalerPackageService.cs app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs
git commit -m "Validate user-supplied OptiScaler pre-SR packages

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Package discovery, zip extraction, local weights

**Files:**
- Modify: `app/Dlss5AmdSwapper/Services/OptiScalerPackageService.cs`
- Modify: `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs`

**Interfaces:**
- Produces: `IReadOnlyList<string> DiscoverCandidates(string? configuredPath, IEnumerable<string>? searchRoots = null)` (folders or `.zip` files, most specific first), `string EnsureExtracted(string zipPath, string? cacheRoot = null)` (returns folder), `static LocalWeights? FindLocalWeights(string? configuredPath, IEnumerable<string> gameDirectories, string? losslessInstallPath)` (throws when two candidates disagree).

- [ ] **Step 1: Write the failing tests**

Append inside `RunAsync`:

```csharp
        await run("Discovery finds package folders, zips and Vodkaman folders under search roots", () =>
        {
            using var temp = new OptiTemp();
            var downloads = Path.Combine(temp.Path, "Downloads");
            MakePackage(downloads, layout: "package", withSums: false);
            var nested = Path.Combine(downloads, "Arquivos necessarios");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(nested, "OptiScaler-AMD-PreSR-Multipass-v1.3.zip"), [0x50, 0x4B]);
            var vodka = Path.Combine(downloads, "DLSS-NR-UE5-Opti-DLL");
            Directory.CreateDirectory(vodka);
            File.WriteAllBytes(Path.Combine(vodka, "dxgi.dll"), [1]);
            File.WriteAllBytes(Path.Combine(vodka, "dlssnr_amd_pass1.dll"), [1]);
            var configured = Path.Combine(temp.Path, "Configured");
            Directory.CreateDirectory(configured);
            var found = new OptiScalerPackageService().DiscoverCandidates(configured, [downloads]);
            Check(found.Count == 4, $"expected 4 candidates, got {found.Count}");
            Check(found[0] == configured, "configured path first");
            Check(found.Any(path => path.EndsWith("OptiScaler-AMD-PreSR-Multipass-v1.2", StringComparison.Ordinal)), "package folder");
            Check(found.Any(path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)), "zip");
            Check(found.Any(path => path.EndsWith("DLSS-NR-UE5-Opti-DLL", StringComparison.Ordinal)), "vodkaman folder");
            return Task.CompletedTask;
        });

        await run("Zip extraction is cached by hash and rejects escaping entries", async () =>
        {
            using var temp = new OptiTemp();
            var zip = Path.Combine(temp.Path, "OptiScaler-AMD-PreSR-Multipass-v1.2.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("OptiScaler-AMD-PreSR-Multipass-v1.2/OptiScaler.ini");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("[DlssNr]\nEnabled=auto\n");
            }
            var cache = Path.Combine(temp.Path, "cache");
            var service = new OptiScalerPackageService();
            var first = service.EnsureExtracted(zip, cache);
            var second = service.EnsureExtracted(zip, cache);
            Check(first == second, "extraction must be cached");
            Check(File.Exists(Path.Combine(first, "OptiScaler.ini")), "inner folder must be unwrapped");

            var evil = Path.Combine(temp.Path, "evil.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
                archive.CreateEntry("../escape.txt");
            try { service.EnsureExtracted(evil, cache); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("outside"), "zip-slip must be refused"); }
        });

        await run("Local weights come from generated copies and must agree", async () =>
        {
            using var temp = new OptiTemp();
            var ls = Path.Combine(temp.Path, "Lossless Scaling");
            var runtime = Path.Combine(ls, "nr-bridge", "runtime");
            var game = Path.Combine(temp.Path, "Game");
            Directory.CreateDirectory(runtime); Directory.CreateDirectory(game);
            var payload = new byte[1024 * 1024 + 7];
            payload[3] = 9;
            await File.WriteAllBytesAsync(Path.Combine(runtime, "dlssnr_on_amd_weights.bin"), payload);
            await File.WriteAllBytesAsync(Path.Combine(game, "dlssnr_on_amd_weights.bin"), payload);
            var weights = OptiScalerPackageService.FindLocalWeights(null, [game], ls);
            Check(weights is not null && weights.Size == payload.Length, "weights not found");
            Check(weights!.Path == Path.Combine(runtime, "dlssnr_on_amd_weights.bin"), "bridge runtime copy must win");
            payload[5] = 1;
            await File.WriteAllBytesAsync(Path.Combine(game, "dlssnr_on_amd_weights.bin"), payload);
            try { OptiScalerPackageService.FindLocalWeights(null, [game], ls); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("differ"), "disagreeing copies must fail"); }
            Check(OptiScalerPackageService.FindLocalWeights(null, [], Path.Combine(temp.Path, "nowhere")) is null, "no candidates must be null");
        });
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: compile errors for `DiscoverCandidates`, `EnsureExtracted`, `FindLocalWeights`.

- [ ] **Step 3: Implement discovery, extraction and weights lookup**

Add to `OptiScalerPackageService` (and `using System.IO.Compression;` at the top):

```csharp
    public const string PackageFolderPrefix = "OptiScaler-AMD-PreSR-Multipass";

    public IReadOnlyList<string> DiscoverCandidates(string? configuredPath, IEnumerable<string>? searchRoots = null)
    {
        var results = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath) && (Directory.Exists(configuredPath) || File.Exists(configuredPath)))
            results.Add(Path.GetFullPath(configuredPath));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = searchRoots?.ToArray() ?? [Path.Combine(profile, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)];
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var folder in EnumerateFolders(root, 2))
            {
                var name = Path.GetFileName(folder);
                if (name.StartsWith(PackageFolderPrefix, StringComparison.OrdinalIgnoreCase)
                    || (File.Exists(Path.Combine(folder, "dxgi.dll")) && File.Exists(Path.Combine(folder, PassNames[0]))))
                    results.Add(folder);
                foreach (var zip in Directory.EnumerateFiles(folder, PackageFolderPrefix + "*.zip"))
                    results.Add(zip);
            }
            foreach (var zip in Directory.EnumerateFiles(root, PackageFolderPrefix + "*.zip"))
                results.Add(zip);
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EnumerateFolders(string root, int depth)
    {
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(root); }
        catch (UnauthorizedAccessException) { yield break; }
        catch (IOException) { yield break; }
        foreach (var child in children)
        {
            yield return child;
            if (depth > 1)
                foreach (var grandchild in EnumerateFolders(child, depth - 1)) yield return grandchild;
        }
    }

    public string EnsureExtracted(string zipPath, string? cacheRoot = null)
    {
        cacheRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5 AMD Swapper", "optiscaler-packages");
        var hash = DirectGameInstallerService.Sha256Async(zipPath).GetAwaiter().GetResult();
        var target = Path.Combine(cacheRoot, hash[..8]);
        var marker = Path.Combine(target, ".extracted");
        if (File.Exists(marker)) return UnwrapSingleFolder(target);

        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        var prefix = Path.GetFullPath(target).TrimEnd('\\') + "\\";
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(target, entry.FullName.Replace('/', '\\')));
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The zip contains an entry outside its folder: {entry.FullName}");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        }
        File.WriteAllText(marker, zipPath);
        return UnwrapSingleFolder(target);
    }

    // A zip usually wraps everything in one top-level folder; validate that folder, not the cache root.
    private static string UnwrapSingleFolder(string folder)
    {
        var files = Directory.EnumerateFiles(folder).Where(file => Path.GetFileName(file) != ".extracted").Any();
        var directories = Directory.GetDirectories(folder);
        return !files && directories.Length == 1 ? directories[0] : folder;
    }

    public static LocalWeights? FindLocalWeights(string? configuredPath, IEnumerable<string> gameDirectories, string? losslessInstallPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath);
        if (!string.IsNullOrWhiteSpace(losslessInstallPath))
        {
            candidates.Add(Path.Combine(losslessInstallPath, "nr-bridge", "runtime", WeightsName));
            candidates.Add(Path.Combine(losslessInstallPath, WeightsName));
        }
        foreach (var directory in gameDirectories) candidates.Add(Path.Combine(directory, WeightsName));

        LocalWeights? chosen = null;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(IsRealWeightsFile))
        {
            var state = new LocalWeights(Path.GetFullPath(candidate), new FileInfo(candidate).Length, DirectGameInstallerService.Sha256Async(candidate).GetAwaiter().GetResult());
            if (chosen is null) { chosen = state; continue; }
            if (!chosen.Sha256.Equals(state.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Two local weights files differ: {chosen.Path} and {state.Path}. Point Settings at the one you trust.");
        }
        return chosen;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Expected: three new `PASS` lines and `All smoke tests passed.`

- [ ] **Step 5: Commit**

```bash
git add app/Dlss5AmdSwapper/Services/OptiScalerPackageService.cs app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs
git commit -m "Discover OptiScaler packages and local generated weights

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: IniDocument text helpers and preset INI writer

**Files:**
- Modify: `app/Dlss5AmdSwapper/Services/IniDocument.cs`
- Create: `app/Dlss5AmdSwapper/Services/OptiScalerIniWriter.cs`
- Modify: `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs`

**Interfaces:**
- Produces: `IniDocument.FromText(string text)`, `IniDocument.ToText()` (LF-joined, single trailing newline).
- Produces: `static string OptiScalerIniWriter.Build(string? baseIniText, OptiScalerPreset preset, bool enablerAvailable)`; `static bool OptiScalerIniWriter.RequiresEnabler(OptiScalerPreset preset)` (true for Performance).

- [ ] **Step 1: Write the failing test**

Append inside `RunAsync` in `OptiScalerTests.cs`:

```csharp
        await run("Preset INI writer patches a package INI in place and writes a minimal INI otherwise", () =>
        {
            var baseIni = "; comment stays\n[Upscalers]\nDx12Upscaler=auto\n\n[DlssNr]\nEnabled=auto\nPasses=auto\n\n[Log]\nLogToFile=auto\n";
            var quality = IniDocument.FromText(OptiScalerIniWriter.Build(baseIni, OptiScalerPreset.Quality, enablerAvailable: false));
            Check(quality.Get("Upscalers", "Dx12Upscaler") == "ffx", "Dx12Upscaler");
            Check(quality.Get("DlssNr", "Enabled") == "true" && quality.Get("DlssNr", "RunBeforeSR") == "true" && quality.Get("DlssNr", "Passes") == "1", "DlssNr core keys");
            Check(quality.Get("DlssNr", "LocalTone") == "0" && quality.Get("DlssNr", "LocalStructure") == "1" && quality.Get("DlssNr", "SkinStructure") == "1" && quality.Get("DlssNr", "ApplyAfterRR") == "false", "DlssNr layer keys");
            Check(quality.Get("Log", "LogToFile") == "true" && quality.Get("Log", "LogLevel") == "2", "Log keys");
            Check(quality.Get("FrameGen", "Enabled") is null && quality.Get("UpscaleRatio", "UpscaleRatioOverrideEnabled") is null, "Quality must not touch FG or ratio");
            Check(OptiScalerIniWriter.Build(baseIni, OptiScalerPreset.Quality, false).StartsWith("; comment stays", StringComparison.Ordinal), "comments preserved");

            var perf = IniDocument.FromText(OptiScalerIniWriter.Build(null, OptiScalerPreset.Performance, enablerAvailable: true));
            Check(perf.Get("UpscaleRatio", "UpscaleRatioOverrideEnabled") == "true" && perf.Get("UpscaleRatio", "UpscaleRatioOverrideValue") == "3.0", "ratio override");
            Check(perf.Get("FrameGen", "Enabled") == "true" && perf.Get("FrameGen", "FGInput") == "nvngxfg" && perf.Get("FrameGen", "FGNvngxReplacement") == "combo", "FG combo");
            Check(perf.Get("DLSSG", "InterpolationCount") == "2", "3x interpolation");
            var perfNoEnabler = IniDocument.FromText(OptiScalerIniWriter.Build(null, OptiScalerPreset.Performance, enablerAvailable: false));
            Check(perfNoEnabler.Get("FrameGen", "FGNvngxReplacement") == "ffx", "ffx without enabler");
            Check(perfNoEnabler.Get("DlssNr", "RunBeforeSR") == "true", "minimal INI still carries DlssNr");
            return Task.CompletedTask;
        });
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: compile errors for `IniDocument.FromText` / `OptiScalerIniWriter`.

- [ ] **Step 3: Implement**

Add to `IniDocument` (after `Load`):

```csharp
    public static IniDocument FromText(string text) =>
        new(text.Replace("\r\n", "\n").Split('\n').ToList());

    public string ToText() => string.Join('\n', _lines).TrimEnd('\n') + "\n";
```

Create `app/Dlss5AmdSwapper/Services/OptiScalerIniWriter.cs`:

```csharp
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public static class OptiScalerIniWriter
{
    private const string MinimalHeader = "; Written by DLSS5 AMD Swapper. Unlisted OptiScaler keys keep their defaults.\n; Open the in-game OptiScaler menu (Insert) to change anything else.\n";

    public static bool RequiresEnabler(OptiScalerPreset preset) => preset == OptiScalerPreset.Performance;

    public static string Build(string? baseIniText, OptiScalerPreset preset, bool enablerAvailable)
    {
        var ini = IniDocument.FromText(string.IsNullOrWhiteSpace(baseIniText) ? MinimalHeader : baseIniText);
        ini.Set("Upscalers", "Dx12Upscaler", "ffx");
        ini.Set("DlssNr", "Enabled", "true");
        ini.Set("DlssNr", "RunBeforeSR", "true");
        ini.Set("DlssNr", "Passes", "1");
        ini.Set("DlssNr", "LocalTone", "0");
        ini.Set("DlssNr", "LocalStructure", "1");
        ini.Set("DlssNr", "SkinStructure", "1");
        ini.Set("DlssNr", "ApplyAfterRR", "false");
        ini.Set("Log", "LogToFile", "true");
        ini.Set("Log", "LogLevel", "2");
        if (preset == OptiScalerPreset.Performance)
        {
            ini.Set("UpscaleRatio", "UpscaleRatioOverrideEnabled", "true");
            ini.Set("UpscaleRatio", "UpscaleRatioOverrideValue", "3.0");
            ini.Set("FrameGen", "Enabled", "true");
            ini.Set("FrameGen", "FGInput", "nvngxfg");
            ini.Set("FrameGen", "FGNvngxReplacement", enablerAvailable ? "combo" : "ffx");
            ini.Set("DLSSG", "InterpolationCount", "2");
        }
        return ini.ToText();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Expected: `PASS  Preset INI writer ...` and `All smoke tests passed.`

- [ ] **Step 5: Commit**

```bash
git add app/Dlss5AmdSwapper/Services/IniDocument.cs app/Dlss5AmdSwapper/Services/OptiScalerIniWriter.cs app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs
git commit -m "Write OptiScaler preset INIs from a package base or a minimal template

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Installer (install/update with snapshot, manifest schema 3, rollback)

**Files:**
- Create: `app/Dlss5AmdSwapper/Services/OptiScalerInstallerService.cs`
- Modify: `app/Dlss5AmdSwapper/Services/DirectGameInstallerService.cs` (route gate)
- Modify: `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs`

**Interfaces:**
- Consumes: `OptiScalerPackage`, `LocalWeights`, `OptiScalerPreset`, `OptiScalerIniWriter.Build`, `GameProbeService.Probe(exePath, ct)`, `DirectGameInstallerService.RemoveAsync(game, removeModel, ct)` / `Sha256Async`, `FileState`, `RemoveResult`, `ManagedManifest`.
- Produces: `OptiScalerInstallerService(GameProbeService probe, DirectGameInstallerService postFsr)`; `Task<OptiScalerInstallResult> InstallAsync(GameEntry game, OptiScalerPackage package, LocalWeights weights, OptiScalerPreset preset, bool update, string proxyName = "dxgi.dll", CancellationToken ct = default)`; `public sealed class OptiScalerManifest` (+ `PackageState`, `WeightsState`, `OptiCompatibility`, `PreviousRouteState`) with the JSON names from the spec; `record OptiScalerInstallResult(bool Success, string ProxyName, OptiScalerPreset Preset, IReadOnlyList<string> Written, RemoveResult? PreviousRouteRemoval)`; `static string[] ProxyNames`, `static string[] RootManagedNames`.
- Produces in `DirectGameInstallerService`: `HasManagedInstall(game)` true only when route is post-FSR; `RemoveAsync`/`InstallAsync` refuse manifests owned by the pre-SR route.

- [ ] **Step 1: Write the failing tests**

Append inside `RunAsync`. A shared fixture builder first (add after `MakePackage` in the class):

```csharp
    internal sealed record InstallFixture(OptiTemp Temp, OptiScalerPackage Package, string GameDir, GameEntry Game, LocalWeights Weights, OptiScalerInstallerService Installer, DirectGameInstallerService Legacy);

    internal static async Task<InstallFixture> MakeInstallFixtureAsync(bool preexistingLibxess = false)
    {
        var temp = new OptiTemp();
        var root = MakePackage(temp.Path, layout: "package", withSums: false);
        var package = OptiScalerPackageService.Validate(root, FakeFork);
        var gameDir = Path.Combine(temp.Path, "Game");
        Directory.CreateDirectory(gameDir);
        var exe = WritePe(Path.Combine(gameDir, "FixtureGame.exe"), "d3d12.dll");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll"), [1]);
        if (preexistingLibxess)
        {
            Directory.CreateDirectory(Path.Combine(gameDir, "OptiScaler"));
            File.Copy(Path.Combine(root, "OptiScaler", "libxess.dll"), Path.Combine(gameDir, "OptiScaler", "libxess.dll"));
        }
        var weightsPath = Path.Combine(temp.Path, "weights.bin");
        await File.WriteAllBytesAsync(weightsPath, new byte[1024 * 1024 + 3]);
        var weights = new LocalWeights(weightsPath, new FileInfo(weightsPath).Length, await DirectGameInstallerService.Sha256Async(weightsPath));
        var probe = new GameProbeService();
        var legacy = new DirectGameInstallerService(probe);
        return new InstallFixture(temp, package, gameDir, new GameEntry { Name = "Fixture", ExePath = exe }, weights, new OptiScalerInstallerService(probe, legacy), legacy);
    }
```

Tests:

```csharp
        await run("Pre-SR install copies package, patches INI, records manifest and skips identical dependencies", async () =>
        {
            var f = await MakeInstallFixtureAsync(preexistingLibxess: true);
            using var _ = f.Temp;
            var result = await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, update: false);
            Check(result.Success && result.ProxyName == "dxgi.dll", "install result");
            foreach (var name in new[] { "dxgi.dll", "OptiScaler.ini", "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll", "dlssnr_on_amd_weights.bin", "OptiScaler\\amd_fidelityfx_upscaler_dx12.dll" })
                Check(File.Exists(Path.Combine(f.GameDir, name)), $"missing {name}");
            Check(IniDocument.Load(Path.Combine(f.GameDir, "OptiScaler.ini")).Get("DlssNr", "RunBeforeSR") == "true", "INI patched");
            Check(ManagedManifest.ReadRoute(f.Game) == InstallRoute.OptiScalerPreSr, "manifest route");
            var manifestText = await File.ReadAllTextAsync(f.Game.ManifestPath);
            Check(manifestText.Contains("\"schema_version\": 3") && manifestText.Contains("\"preset\": \"quality\""), "manifest content");
            Check(manifestText.Contains("OptiScaler\\\\libxess.dll") && manifestText.Contains("\"preexisting_dependencies\""), "preexisting dependency recorded");
            Check(f.Game.Status == "Installed" && !f.Game.Busy, "status");

            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, update: false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("Update"), "second install must ask for Update"); }

            var updated = await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Performance, update: true);
            Check(updated.Preset == OptiScalerPreset.Performance, "update preset");
            Check(IniDocument.Load(Path.Combine(f.GameDir, "OptiScaler.ini")).Get("UpscaleRatio", "UpscaleRatioOverrideValue") == "3.0", "update rewrote INI");
        });

        await run("Pre-SR install refuses unmanaged proxies, anti-cheat and running games", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var _ = f.Temp;
            await File.WriteAllBytesAsync(Path.Combine(f.GameDir, "winmm.dll"), [1]);
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("winmm.dll"), "unmanaged proxy must be named"); }
            File.Delete(Path.Combine(f.GameDir, "winmm.dll"));

            await File.WriteAllBytesAsync(Path.Combine(f.GameDir, "EasyAntiCheat.dll"), [1]);
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("Anti-cheat"), "anti-cheat must block"); }
            File.Delete(Path.Combine(f.GameDir, "EasyAntiCheat.dll"));

            f.Game.Running = true;
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("Close the game"), "running game must block"); }
            Check(!File.Exists(Path.Combine(f.GameDir, "dxgi.dll")) && !File.Exists(f.Game.ManifestPath), "refusals must leave the folder untouched");
        });

        await run("Pre-SR install rolls back exactly when a write fails", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var _ = f.Temp;
            // A directory at the INI path makes the INI write fail after the binaries were copied.
            Directory.CreateDirectory(Path.Combine(f.GameDir, "OptiScaler.ini"));
            try { await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false); throw new InvalidOperationException("accepted"); }
            catch (InvalidOperationException error) when (error.Message == "accepted") { throw; }
            catch (Exception) { }
            Check(!File.Exists(Path.Combine(f.GameDir, "dxgi.dll")) && !File.Exists(Path.Combine(f.GameDir, "dlssnr_amd_pass1.dll")) && !File.Exists(Path.Combine(f.GameDir, "dlssnr_on_amd_weights.bin")), "copied files must be rolled back");
            Check(!File.Exists(Path.Combine(f.GameDir, "OptiScaler", "libxess.dll")), "dependency copies must be rolled back");
            Check(!File.Exists(f.Game.ManifestPath), "manifest must not remain");
            Check(!f.Game.Busy, "busy flag cleared");
        });

        await run("Pre-SR install removes a managed post-FSR route first and records it", async () =>
        {
            var f = await MakeInstallFixtureAsync();
            using var _ = f.Temp;
            var oldProxy = Path.Combine(f.GameDir, "winmm.dll");
            var oldIni = Path.Combine(f.GameDir, "dlssnr_on_amd.ini");
            await File.WriteAllTextAsync(oldProxy, "old proxy");
            await File.WriteAllTextAsync(oldIni, "[DlssNrOnAmd]\nEnabled=1\n");
            await File.WriteAllTextAsync(f.Game.ManifestPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                schema_version = 2, route = "amd-fsr-direct",
                before = new Dictionary<string, FileState>(),
                after = new Dictionary<string, FileState>
                {
                    ["winmm.dll"] = new(new FileInfo(oldProxy).Length, await DirectGameInstallerService.Sha256Async(oldProxy)),
                    ["dlssnr_on_amd.ini"] = new(new FileInfo(oldIni).Length, await DirectGameInstallerService.Sha256Async(oldIni))
                },
                installed_proxy_names = new[] { "winmm.dll" }
            }));
            var result = await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false);
            Check(result.PreviousRouteRemoval is not null && result.PreviousRouteRemoval.Removed.Contains("winmm.dll"), "old proxy removed");
            Check(!File.Exists(oldProxy) && !File.Exists(oldIni) && File.Exists(Path.Combine(f.GameDir, "dxgi.dll")), "old route gone, new route present");
            Check((await File.ReadAllTextAsync(f.Game.ManifestPath)).Contains("\"previous_route\""), "previous route recorded");
            Check(!f.Legacy.HasManagedInstall(f.Game), "legacy installer must not claim a pre-SR game");
            try { await f.Legacy.RemoveAsync(f.Game, false); throw new Exception("accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("pre-SR"), "legacy remove must refuse other routes"); }
        });
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: compile errors for `OptiScalerInstallerService`.

- [ ] **Step 3: Route-gate the post-FSR installer**

In `DirectGameInstallerService.cs` replace

```csharp
    public bool HasManagedInstall(GameEntry game) => FindManifest(game) is not null;
```

with

```csharp
    public bool HasManagedInstall(GameEntry game) => ManagedManifest.ReadRoute(game) == InstallRoute.PostFsrRuntime;
```

In `RemoveAsync`, right after `var manifestPath = FindManifest(game) ?? throw new InvalidOperationException("No managed direct-game install was found.");` add:

```csharp
        if (ManagedManifest.ReadRoute(manifestPath) != InstallRoute.PostFsrRuntime)
            throw new InvalidOperationException("This game is managed by the OptiScaler pre-SR route. Use its Restore.");
```

In `InstallAsync`, right after `var existingManifest = FindManifest(game);` add:

```csharp
            if (existingManifest is not null && ManagedManifest.ReadRoute(existingManifest) != InstallRoute.PostFsrRuntime)
                throw new InvalidOperationException("This game is managed by the OptiScaler pre-SR route. Restore it before installing the post-FSR runtime.");
```

- [ ] **Step 4: Implement the installer**

Create `app/Dlss5AmdSwapper/Services/OptiScalerInstallerService.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class OptiScalerInstallerService(GameProbeService probe, DirectGameInstallerService postFsr)
{
    public static readonly string[] ProxyNames = ["dxgi.dll", "version.dll", "winmm.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll"];
    public static readonly string[] RootManagedNames = ["OptiScaler.ini", "OptiScaler.log", "amd_presr.log", "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll", "dlssnr_on_amd_weights.bin"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveFolders = new(StringComparer.OrdinalIgnoreCase);

    public async Task<OptiScalerInstallResult> InstallAsync(GameEntry game, OptiScalerPackage package, LocalWeights weights, OptiScalerPreset preset, bool update, string proxyName = "dxgi.dll", CancellationToken cancellationToken = default)
    {
        if (!ProxyNames.Contains(proxyName, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unsupported proxy name.", nameof(proxyName));
        var folder = Path.GetFullPath(game.DirectoryPath);
        if (game.Busy || !ActiveFolders.TryAdd(folder, 0)) throw new InvalidOperationException("An operation is already running for this game folder.");
        game.Busy = true;
        game.Status = update ? "Updating" : "Installing";
        try
        {
            if (game.Running) throw new InvalidOperationException("Close the game before installing or updating Neural Rendering.");
            var compatibility = await Task.Run(() => probe.Probe(game.ExePath, cancellationToken), cancellationToken);
            if (!compatibility.X64) throw new InvalidOperationException("Direct-game AMD support requires a 64-bit game executable.");
            if (compatibility.AntiCheatMarkers.Count > 0) throw new InvalidOperationException("Anti-cheat markers were found. Direct-game installation is blocked for this target.");
            if (compatibility.FsrMarkers.Count == 0) throw new InvalidOperationException("No supported FSR runtime marker was found near this game.");
            if (compatibility.Dx12Evidence.Count == 0) throw new InvalidOperationException("No DirectX 12 evidence was found near this game.");
            if (!OptiScalerPackageService.IsRealWeightsFile(weights.Path)) throw new InvalidOperationException("The selected weights file is not a generated dlssnr_on_amd_weights.bin.");

            var packageUpscaler = package.DependencyFolder is null ? null : Path.Combine(package.DependencyFolder, OptiScalerPackageService.RequiredUpscalerDependency);
            if (!(packageUpscaler is not null && File.Exists(packageUpscaler)) && !File.Exists(Path.Combine(folder, OptiScalerPackageService.RequiredUpscalerDependency)))
                throw new InvalidOperationException($"{OptiScalerPackageService.RequiredUpscalerDependency} is missing from both the package and the game folder; Dx12Upscaler=ffx cannot work.");

            var existingRoute = ManagedManifest.ReadRoute(game);
            RemoveResult? previousRemoval = null;
            OptiScalerManifest? previousManifest = null;
            if (existingRoute == InstallRoute.PostFsrRuntime)
            {
                if (update) throw new InvalidOperationException("This game has the post-FSR route installed; set it up as a new pre-SR install instead of updating.");
                game.Busy = false; // the post-FSR installer owns the busy flag while it runs
                try { previousRemoval = await postFsr.RemoveAsync(game, false, cancellationToken); }
                finally { game.Busy = true; game.Status = "Installing"; }
                if (ManagedManifest.ReadRoute(game) != InstallRoute.None)
                    throw new InvalidOperationException("The post-FSR route left changed files behind. Review them, then set up again.");
            }
            else if (existingRoute == InstallRoute.OptiScalerPreSr)
            {
                if (!update) throw new InvalidOperationException("This game already has the pre-SR route. Use Update instead.");
                previousManifest = await ReadManifestAsync(game.ManifestPath, cancellationToken) ?? throw new InvalidOperationException("The managed install manifest could not be read.");
            }
            else if (update) throw new InvalidOperationException("Update requires an existing managed install.");

            var dependencyRelatives = package.DependencyFolder is null ? [] : Directory.EnumerateFiles(package.DependencyFolder, "*", SearchOption.AllDirectories)
                .Select(file => Path.Combine(OptiScalerPackageService.DependencyFolderName, Path.GetRelativePath(package.DependencyFolder, file))).ToArray();
            var enablerRelative = Path.Combine(OptiScalerPackageService.DependencyFolderName, OptiScalerPackageService.EnablerName);
            var managed = ProxyNames.Concat(RootManagedNames).Concat(dependencyRelatives).Append(enablerRelative).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var before = await SnapshotAsync(folder, managed, cancellationToken);

            if (!update)
            {
                var unmanaged = ProxyNames.Where(before.ContainsKey).ToArray();
                if (unmanaged.Length > 0) throw new InvalidOperationException("A proxy DLL already exists and is not managed by this app: " + string.Join(", ", unmanaged));
            }
            else if (previousManifest is not null && !previousManifest.InstalledProxyNames.Contains(proxyName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Update must keep the proxy name recorded in the manifest.");

            var backupRoot = Path.Combine(Path.GetTempPath(), "dlss5-amd-swapper", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupRoot);
            foreach (var relative in before.Keys)
            {
                var target = Path.Combine(backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(folder, relative), target, true);
            }
            var previousManifestBytes = File.Exists(game.ManifestPath) ? await File.ReadAllBytesAsync(game.ManifestPath, cancellationToken) : null;

            var written = new List<string>();
            var preexisting = new List<string>();
            var keepBackup = false;
            try
            {
                async Task CopyVerifiedAsync(string source, string relative)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.Combine(folder, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, true);
                    if (!string.Equals(await DirectGameInstallerService.Sha256Async(source, cancellationToken), await DirectGameInstallerService.Sha256Async(destination, cancellationToken), StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Copy verification failed for {relative}.");
                    written.Add(relative);
                }

                await CopyVerifiedAsync(package.OptiScalerDllPath, proxyName);
                for (var index = 0; index < OptiScalerPackageService.PassNames.Length; index++)
                    await CopyVerifiedAsync(index < package.PassDllPaths.Count ? package.PassDllPaths[index] : package.PassDllPaths[0], OptiScalerPackageService.PassNames[index]);
                await CopyVerifiedAsync(weights.Path, OptiScalerPackageService.WeightsName);

                foreach (var relative in dependencyRelatives)
                {
                    var source = Path.Combine(package.Root, relative);
                    if (before.TryGetValue(relative, out var existing)
                        && existing.Sha256.Equals(await DirectGameInstallerService.Sha256Async(source, cancellationToken), StringComparison.OrdinalIgnoreCase))
                    { preexisting.Add(relative); continue; }
                    await CopyVerifiedAsync(source, relative);
                }
                var enablerAvailable = package.EnablerDllPath is not null;
                if (OptiScalerIniWriter.RequiresEnabler(preset) && enablerAvailable)
                    await CopyVerifiedAsync(package.EnablerDllPath!, enablerRelative);

                var baseIni = package.IniPath is null ? null : await File.ReadAllTextAsync(package.IniPath, cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(folder, "OptiScaler.ini"), OptiScalerIniWriter.Build(baseIni, preset, enablerAvailable), new UTF8Encoding(false), cancellationToken);
                written.Add("OptiScaler.ini");

                var after = await SnapshotAsync(folder, managed, cancellationToken);
                var manifest = new OptiScalerManifest
                {
                    SchemaVersion = 3,
                    CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Route = InstallRoutes.OptiScalerPreSr,
                    GameExe = Path.GetFileName(game.ExePath),
                    ProxyName = proxyName,
                    Preset = preset.ToString().ToLowerInvariant(),
                    Package = new PackageState { Root = package.Root, Layout = package.Layout, ForkVersion = package.ForkVersion, Sha256SumsVerified = package.Sha256SumsVerified, Files = new Dictionary<string, FileState>(package.Files, StringComparer.OrdinalIgnoreCase) },
                    Weights = new WeightsState { Source = weights.Path, Size = weights.Size, Sha256 = weights.Sha256 },
                    Compatibility = new OptiCompatibility { X64 = compatibility.X64, FsrMarkers = compatibility.FsrMarkers.ToArray(), Dx12Evidence = compatibility.Dx12Evidence.ToArray(), AntiCheatMarkers = compatibility.AntiCheatMarkers.ToArray() },
                    PreviousRoute = previousRemoval is null ? previousManifest?.PreviousRoute : new PreviousRouteState { Route = InstallRoutes.PostFsr, Removed = previousRemoval.Removed.ToArray(), Preserved = previousRemoval.Preserved.ToArray() },
                    Before = previousManifest?.Before ?? before,
                    After = after,
                    PreexistingDependencies = (previousManifest?.PreexistingDependencies ?? []).Concat(preexisting).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    InstalledProxyNames = [proxyName]
                };
                await File.WriteAllTextAsync(game.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
                if (File.Exists(game.LegacyManifestPath)) File.Delete(game.LegacyManifestPath);
                game.Status = update ? "Updated" : "Installed";
                return new OptiScalerInstallResult(true, proxyName, preset, written, previousRemoval);
            }
            catch
            {
                try { RestoreSnapshot(folder, managed, before, backupRoot); }
                catch (Exception restoreError)
                {
                    keepBackup = true;
                    throw new IOException($"Rollback could not finish. Recovery files were retained at {backupRoot}.", restoreError);
                }
                if (previousManifestBytes is not null) await File.WriteAllBytesAsync(game.ManifestPath, previousManifestBytes, CancellationToken.None);
                else if (File.Exists(game.ManifestPath)) File.Delete(game.ManifestPath);
                game.Status = "Install failed";
                throw;
            }
            finally
            {
                if (!keepBackup) { try { Directory.Delete(backupRoot, true); } catch { } }
            }
        }
        finally
        {
            game.Busy = false;
            ActiveFolders.TryRemove(folder, out _);
        }
    }

    private static async Task<Dictionary<string, FileState>> SnapshotAsync(string folder, IEnumerable<string> relatives, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in relatives)
        {
            var path = Path.Combine(folder, relative);
            if (!File.Exists(path)) continue;
            result[relative] = new FileState(new FileInfo(path).Length, await DirectGameInstallerService.Sha256Async(path, cancellationToken));
        }
        return result;
    }

    private static void RestoreSnapshot(string folder, IEnumerable<string> relatives, Dictionary<string, FileState> before, string backupRoot)
    {
        foreach (var relative in relatives)
        {
            var target = Path.Combine(folder, relative);
            if (before.ContainsKey(relative))
            {
                var backup = Path.Combine(backupRoot, relative);
                if (File.Exists(backup)) File.Copy(backup, target, true);
            }
            else if (File.Exists(target)) File.Delete(target);
        }
        var dependencyFolder = Path.Combine(folder, OptiScalerPackageService.DependencyFolderName);
        if (Directory.Exists(dependencyFolder) && !Directory.EnumerateFileSystemEntries(dependencyFolder, "*", SearchOption.AllDirectories).Any()) Directory.Delete(dependencyFolder, true);
    }

    internal static async Task<OptiScalerManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<OptiScalerManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException) { return null; }
    }

    public sealed class OptiScalerManifest
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("created_unix")] public long CreatedUnix { get; set; }
        [JsonPropertyName("route")] public string Route { get; set; } = string.Empty;
        [JsonPropertyName("game_exe")] public string GameExe { get; set; } = string.Empty;
        [JsonPropertyName("proxy_name")] public string ProxyName { get; set; } = "dxgi.dll";
        [JsonPropertyName("preset")] public string Preset { get; set; } = "quality";
        [JsonPropertyName("package")] public PackageState? Package { get; set; }
        [JsonPropertyName("weights")] public WeightsState? Weights { get; set; }
        [JsonPropertyName("compatibility")] public OptiCompatibility? Compatibility { get; set; }
        [JsonPropertyName("previous_route")] public PreviousRouteState? PreviousRoute { get; set; }
        [JsonPropertyName("before")] public Dictionary<string, FileState> Before { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("after")] public Dictionary<string, FileState> After { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("preexisting_dependencies")] public string[] PreexistingDependencies { get; set; } = [];
        [JsonPropertyName("installed_proxy_names")] public string[] InstalledProxyNames { get; set; } = [];
    }

    public sealed class PackageState
    {
        [JsonPropertyName("root")] public string Root { get; set; } = string.Empty;
        [JsonPropertyName("layout")] public string Layout { get; set; } = string.Empty;
        [JsonPropertyName("fork_version")] public string ForkVersion { get; set; } = string.Empty;
        [JsonPropertyName("sha256sums_verified")] public bool Sha256SumsVerified { get; set; }
        [JsonPropertyName("files")] public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class WeightsState
    {
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    }

    public sealed class OptiCompatibility
    {
        [JsonPropertyName("x64")] public bool X64 { get; set; }
        [JsonPropertyName("fsr_markers")] public string[] FsrMarkers { get; set; } = [];
        [JsonPropertyName("dx12_evidence")] public string[] Dx12Evidence { get; set; } = [];
        [JsonPropertyName("anti_cheat_markers")] public string[] AntiCheatMarkers { get; set; } = [];
    }

    public sealed class PreviousRouteState
    {
        [JsonPropertyName("route")] public string Route { get; set; } = string.Empty;
        [JsonPropertyName("removed")] public string[] Removed { get; set; } = [];
        [JsonPropertyName("preserved")] public string[] Preserved { get; set; } = [];
    }
}

public sealed record OptiScalerInstallResult(bool Success, string ProxyName, OptiScalerPreset Preset, IReadOnlyList<string> Written, RemoveResult? PreviousRouteRemoval);
```

- [ ] **Step 5: Run tests to verify they pass**

Expected: four new `PASS` lines; the existing post-FSR tests still pass (their fixture manifests have no `route`, which `InstallRoutes.Parse(null)` maps to post-FSR).

- [ ] **Step 6: Commit**

```bash
git add app/Dlss5AmdSwapper/Services/OptiScalerInstallerService.cs app/Dlss5AmdSwapper/Services/DirectGameInstallerService.cs app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs
git commit -m "Install the OptiScaler pre-SR route reversibly with a schema 3 manifest

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: Restore for the pre-SR route

**Files:**
- Modify: `app/Dlss5AmdSwapper/Services/OptiScalerInstallerService.cs`
- Modify: `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs`

**Interfaces:**
- Produces: `Task<RemoveResult> RemoveAsync(GameEntry game, CancellationToken ct = default)`, `bool HasManagedInstall(GameEntry game)` on `OptiScalerInstallerService`.

- [ ] **Step 1: Write the failing test**

Append inside `RunAsync`:

```csharp
        await run("Pre-SR restore removes only unchanged managed files and keeps pre-existing dependencies", async () =>
        {
            var f = await MakeInstallFixtureAsync(preexistingLibxess: true);
            using var _ = f.Temp;
            await f.Installer.InstallAsync(f.Game, f.Package, f.Weights, OptiScalerPreset.Quality, false);
            Check(f.Installer.HasManagedInstall(f.Game), "managed after install");
            await File.AppendAllTextAsync(Path.Combine(f.GameDir, "OptiScaler.ini"), "\n; user edit\n");

            var result = await f.Installer.RemoveAsync(f.Game);
            Check(!File.Exists(Path.Combine(f.GameDir, "dxgi.dll")) && !File.Exists(Path.Combine(f.GameDir, "dlssnr_amd_pass3.dll")) && !File.Exists(Path.Combine(f.GameDir, "dlssnr_on_amd_weights.bin")), "managed files removed");
            Check(!File.Exists(Path.Combine(f.GameDir, "OptiScaler", "amd_fidelityfx_upscaler_dx12.dll")), "copied dependency removed");
            Check(File.Exists(Path.Combine(f.GameDir, "OptiScaler", "libxess.dll")), "pre-existing dependency kept");
            Check(File.Exists(Path.Combine(f.GameDir, "OptiScaler.ini")) && result.Preserved.Contains("OptiScaler.ini"), "edited INI preserved");
            Check(result.ManifestRetained && File.Exists(f.Game.ManifestPath), "manifest retained while a created file remains");
            File.Delete(Path.Combine(f.GameDir, "OptiScaler.ini"));
            var second = await f.Installer.RemoveAsync(f.Game);
            Check(!second.ManifestRetained && !File.Exists(f.Game.ManifestPath), "manifest deleted once nothing created remains");
            Check(!f.Installer.HasManagedInstall(f.Game), "not managed after restore");
        });
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: compile error for `RemoveAsync`/`HasManagedInstall`.

- [ ] **Step 3: Implement remove**

Add to `OptiScalerInstallerService` (before `SnapshotAsync`):

```csharp
    public bool HasManagedInstall(GameEntry game) => ManagedManifest.ReadRoute(game) == InstallRoute.OptiScalerPreSr;

    public async Task<RemoveResult> RemoveAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        var folder = Path.GetFullPath(game.DirectoryPath);
        if (game.Busy || !ActiveFolders.TryAdd(folder, 0)) throw new InvalidOperationException("An operation is already running for this game folder.");
        game.Busy = true;
        try
        {
            if (game.Running) throw new InvalidOperationException("Close the game before restoring its files.");
            if (ManagedManifest.ReadRoute(game) != InstallRoute.OptiScalerPreSr) throw new InvalidOperationException("No managed pre-SR install was found.");
            var manifest = await ReadManifestAsync(game.ManifestPath, cancellationToken) ?? throw new InvalidOperationException("The managed install manifest could not be read.");
            var removed = new List<string>();
            var preserved = new List<string>();
            var preexisting = new HashSet<string>(manifest.PreexistingDependencies, StringComparer.OrdinalIgnoreCase);

            foreach (var relative in manifest.After.Keys.Concat(manifest.InstalledProxyNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            {
                var path = Path.Combine(folder, relative);
                if (manifest.Before.ContainsKey(relative) || preexisting.Contains(relative)) { preserved.Add(relative); continue; }
                if (!File.Exists(path)) continue;
                var current = new FileState(new FileInfo(path).Length, await DirectGameInstallerService.Sha256Async(path, cancellationToken));
                if (manifest.After.TryGetValue(relative, out var expected) && expected == current) { File.Delete(path); removed.Add(relative); }
                else preserved.Add(relative);
            }

            var dependencyFolder = Path.Combine(folder, OptiScalerPackageService.DependencyFolderName);
            if (Directory.Exists(dependencyFolder) && !Directory.EnumerateFileSystemEntries(dependencyFolder, "*", SearchOption.AllDirectories).Any()) Directory.Delete(dependencyFolder, true);

            var remaining = manifest.After.Keys
                .Where(relative => !manifest.Before.ContainsKey(relative) && !preexisting.Contains(relative) && File.Exists(Path.Combine(folder, relative)))
                .ToArray();
            if (remaining.Length == 0) File.Delete(game.ManifestPath);
            game.Status = remaining.Length == 0 ? "Restored" : "Some changed files were preserved";
            return new RemoveResult(removed, preserved.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), remaining, remaining.Length != 0);
        }
        finally
        {
            game.Busy = false;
            ActiveFolders.TryRemove(folder, out _);
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Expected: `PASS  Pre-SR restore ...` and all green.

- [ ] **Step 5: Commit**

```bash
git add app/Dlss5AmdSwapper/Services/OptiScalerInstallerService.cs app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs
git commit -m "Restore pre-SR installs with hash-checked removal

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: Pre-SR diagnostics parser

**Files:**
- Create: `app/Dlss5AmdSwapper/Services/OptiScalerDiagnosticsService.cs`
- Modify: `app/Dlss5AmdSwapper/Models/GameEntry.cs` (add log paths)
- Modify: `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs`

**Interfaces:**
- Produces on `GameEntry`: `string OptiScalerIniPath => Path.Combine(DirectoryPath, "OptiScaler.ini")`, `string PreSrLogPath => Path.Combine(DirectoryPath, "amd_presr.log")`, `string OptiScalerLogPath => Path.Combine(DirectoryPath, "OptiScaler.log")`.
- Produces: `record OptiScalerDiagnostics(bool PreSrActive, string? HipAdapter, int PassesInitialized, int? PassesCompleted, string? ModelSize, string? TargetSize, double? MeanTotalMs, double? MeanModelMs, int CostSamples, string? LastFault, string? PreSrLogSha256, long PreSrLogBytes, string? OptiLogSha256, long OptiLogBytes)` with `string Summary`.
- Produces: `static OptiScalerDiagnostics Parse(string presrLog, string optiLog)`, `Task<OptiScalerDiagnostics> InspectAsync(GameEntry game, CancellationToken ct = default)` (reads the last 1 MiB of each log, hashes the whole file).

- [ ] **Step 1: Write the failing test**

Append inside `RunAsync`:

```csharp
        await run("Pre-SR diagnostics parse pass counts, resolution, cost and faults", () =>
        {
            var presr = string.Join('\n',
                "HIP runtime: 70260201",
                "HIP adapter: AMD Radeon RX 9070 XT",
                "Initialized independent AMD pass 1",
                "Initialized independent AMD pass 2",
                "AMD pre-SR: waiting for a DirectX 12 SR frame",
                "Completed AMD pre-SR passes=2",
                "HIP completion timeout pass 2");
            var opti = string.Join('\n',
                "[info] DlssNr_Dx12::Dispatch DLSS-NR running before SR: target 3840x2160, model 1280x720, guides 1280x720 (preset 0, intensity 1, style 0, build epoch 3)",
                "[info] DlssNr_Dx12::Dispatch DLSS-NR cost: 12.40 ms total = 10.10 ms model + 2.30 ms ours (19% ours)",
                "[info] DlssNr_Dx12::Dispatch DLSS-NR cost: 11.60 ms total = 9.90 ms model + 1.70 ms ours (15% ours)");
            var result = OptiScalerDiagnosticsService.Parse(presr, opti);
            Check(result.PreSrActive, "active");
            Check(result.HipAdapter == "AMD Radeon RX 9070 XT", "adapter");
            Check(result.PassesInitialized == 2 && result.PassesCompleted == 2, "passes");
            Check(result.ModelSize == "1280x720" && result.TargetSize == "3840x2160", "sizes");
            Check(result.CostSamples == 2 && Math.Abs(result.MeanTotalMs!.Value - 12.0) < 0.001 && Math.Abs(result.MeanModelMs!.Value - 10.0) < 0.001, "cost");
            Check(result.LastFault == "HIP completion timeout pass 2", "last fault");
            Check(result.Summary.Contains("1280x720") && result.Summary.Contains("12.0 ms"), "summary");
            var idle = OptiScalerDiagnosticsService.Parse("AMD pre-SR: idle\n", string.Empty);
            Check(!idle.PreSrActive && idle.LastFault is null, "idle is not a fault");
            var missing = OptiScalerDiagnosticsService.Parse("dlssnr_on_amd_weights.bin is required\n", string.Empty);
            Check(missing.LastFault == "dlssnr_on_amd_weights.bin is required", "weights fault");
            return Task.CompletedTask;
        });

        await run("Pre-SR diagnostics hash the whole log while sampling the tail", async () =>
        {
            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            var content = "Initialized independent AMD pass 1\n" + new string('x', 2 * 1024 * 1024) + "\nCompleted AMD pre-SR passes=1\n";
            await File.WriteAllTextAsync(game.PreSrLogPath, content);
            var result = await new OptiScalerDiagnosticsService().InspectAsync(game);
            Check(result.PreSrActive && result.PassesCompleted == 1, "tail parsed");
            Check(result.PreSrLogBytes == new FileInfo(game.PreSrLogPath).Length && result.PreSrLogSha256?.Length == 64, "hash covers full file");
            Check(result.OptiLogSha256 is null && result.OptiLogBytes == 0, "absent OptiScaler.log tolerated");
        });
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: compile errors for `OptiScalerDiagnosticsService` and `PreSrLogPath`.

- [ ] **Step 3: Implement**

Add to `GameEntry` after `LegacyManifestPath`:

```csharp
    public string OptiScalerIniPath => Path.Combine(DirectoryPath, "OptiScaler.ini");
    public string PreSrLogPath => Path.Combine(DirectoryPath, "amd_presr.log");
    public string OptiScalerLogPath => Path.Combine(DirectoryPath, "OptiScaler.log");
```

Create `app/Dlss5AmdSwapper/Services/OptiScalerDiagnosticsService.cs`:

```csharp
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
            PreSrActive: completed is not null || running is not null,
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
        var tailStart = Math.Max(0, length - TailBytes);
        stream.Position = tailStart;
        var buffer = new byte[length - tailStart];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return (Encoding.UTF8.GetString(buffer), Convert.ToHexString(hash).ToLowerInvariant(), length);
    }

    [GeneratedRegex(@"^HIP adapter:\s*(.+)$", RegexOptions.Multiline)] private static partial Regex HipAdapter();
    [GeneratedRegex(@"Initialized independent AMD pass \d+")] private static partial Regex PassInitialized();
    [GeneratedRegex(@"Completed AMD pre-SR passes=(\d+)")] private static partial Regex PassesCompleted();
    [GeneratedRegex(@"DLSS-NR running [^:]*: target (\d+x\d+), model (\d+x\d+)")] private static partial Regex Running();
    [GeneratedRegex(@"DLSS-NR cost: ([\d.]+) ms total = ([\d.]+) ms model")] private static partial Regex Cost();
    [GeneratedRegex(@"^(AMD pre-SR: (?!idle)|HIP completion timeout|Unsupported AMD pre-SR|.*hash mismatch|dlssnr_on_amd_weights\.bin is required|.*LoadLibrary failed|AMD engine initialization failed|Cannot load amdhip64_7\.dll|AMD stopped|AMD timeout)")] private static partial Regex Fault();
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
```

- [ ] **Step 4: Run tests to verify they pass**

Expected: two new `PASS` lines and all green.

- [ ] **Step 5: Commit**

```bash
git add app/Dlss5AmdSwapper/Services/OptiScalerDiagnosticsService.cs app/Dlss5AmdSwapper/Models/GameEntry.cs app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs
git commit -m "Parse pre-SR and OptiScaler logs into bounded diagnostics

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 8: Route-aware runtime refresh, pre-SR controls, LS layer API

**Files:**
- Modify: `app/Dlss5AmdSwapper/Models/GameEntry.cs` (`Route`, `Passes`, labels)
- Modify: `app/Dlss5AmdSwapper/Services/RuntimeControlService.cs`
- Create: `app/Dlss5AmdSwapper/Services/OptiScalerControlService.cs`
- Modify: `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs`

**Interfaces:**
- Produces on `GameEntry`: `InstallRoute Route` (notifying), `int Passes` (notifying, default 1), `string RouteLabel => InstallRoutes.Label(Route)`, `bool IsPreSr => Route == InstallRoute.OptiScalerPreSr`, `bool IsPostFsr => Route == InstallRoute.PostFsrRuntime`.
- Produces on `RuntimeControlService`: `Refresh(game, runningOverride)` now sets `Route`; for pre-SR games it reads `[DlssNr]` from `OptiScalerIniPath` (Enabled bool, LocalStructure/LocalTone/SkinStructure clamped 0–2, Passes 1–3) and sets `Installed = proxy present && OptiScaler.ini present`; existing post-FSR behaviour unchanged. New INI-path layer API: `record LayerState(double Structure, double Skin, double Tone, bool SkinFollowsStructure)`, `LayerState ReadLayers(string iniPath)`, `Task<RuntimeChangeResult> SetLayerAsync(string iniPath, string key, double value, CancellationToken ct = default)` (key ∈ `LocalStructure|SkinStructure|LocalTone`, value clamped 0–2, or `-1` allowed for `SkinStructure`), `Task<RuntimeChangeResult> AdjustLayerAsync(string iniPath, string key, double delta, CancellationToken ct = default)`.
- Produces: `OptiScalerControlService` with `Task<RuntimeChangeResult> SetEnabledAsync(GameEntry, bool)`, `SetPassesAsync(GameEntry, int)`, `SetStructureAsync/SetSkinAsync/SetToneAsync(GameEntry, double)` writing `[DlssNr]` in `OptiScalerIniPath` through `IniDocument.SaveAtomic`; message always `"Saved for the next launch"` when not running, `"Saved. OptiScaler reads OptiScaler.ini at startup; use the Insert menu for live changes."` when running.

- [ ] **Step 1: Write the failing tests**

Append inside `RunAsync`:

```csharp
        await run("Runtime refresh reads route and pre-SR controls from OptiScaler.ini", async () =>
        {
            using var temp = new OptiTemp();
            var game = new GameEntry { ExePath = Path.Combine(temp.Path, "Game.exe") };
            await File.WriteAllTextAsync(game.ManifestPath, "{\"route\":\"amd-optiscaler-presr\",\"installed_proxy_names\":[\"dxgi.dll\"]}");
            await File.WriteAllBytesAsync(Path.Combine(temp.Path, "dxgi.dll"), [1]);
            await File.WriteAllTextAsync(game.OptiScalerIniPath, "[DlssNr]\nEnabled=true\nPasses=2\nLocalStructure=1.5\nSkinStructure=0.5\nLocalTone=0\n");
            new RuntimeControlService().Refresh(game, false);
            Check(game.Route == InstallRoute.OptiScalerPreSr && game.IsPreSr, "route");
            Check(game.Installed && game.Enabled && game.Passes == 2, "installed/enabled/passes");
            Check(Math.Abs(game.LocalStructure - 1.5) < 0.001 && Math.Abs(game.SkinStructure - 0.5) < 0.001 && game.LocalTone == 0, "layers");
            Check(game.RouteLabel == "OptiScaler pre-SR", "label");

            var control = new OptiScalerControlService();
            await control.SetPassesAsync(game, 3);
            await control.SetEnabledAsync(game, false);
            await control.SetSkinAsync(game, 2.0);
            var ini = IniDocument.Load(game.OptiScalerIniPath);
            Check(ini.Get("DlssNr", "Passes") == "3" && ini.Get("DlssNr", "Enabled") == "false" && ini.Get("DlssNr", "SkinStructure") == "2.0", "control writes");
            try { await control.SetPassesAsync(game, 4); throw new Exception("accepted"); }
            catch (ArgumentOutOfRangeException) { }
        });

        await run("Layer API edits an arbitrary runtime INI and supports skin follow", async () =>
        {
            using var temp = new OptiTemp();
            var ini = Path.Combine(temp.Path, "dlssnr_on_amd.ini");
            await File.WriteAllTextAsync(ini, "[DlssNrOnAmd]\nEnabled=1\nLocalStructure=1\nLocalTone=0\nSkinStructure=-1\n");
            var service = new RuntimeControlService();
            var state = service.ReadLayers(ini);
            Check(state.SkinFollowsStructure && state.Structure == 1 && state.Tone == 0, "read");
            await service.SetLayerAsync(ini, "SkinStructure", 1.2);
            await service.AdjustLayerAsync(ini, "LocalTone", 0.3);
            state = service.ReadLayers(ini);
            Check(!state.SkinFollowsStructure && Math.Abs(state.Skin - 1.2) < 0.001 && Math.Abs(state.Tone - 0.3) < 0.001, "write");
            await service.SetLayerAsync(ini, "SkinStructure", -1);
            Check(service.ReadLayers(ini).SkinFollowsStructure, "follow restored");
            try { await service.SetLayerAsync(ini, "Enabled", 1); throw new Exception("accepted"); }
            catch (ArgumentException) { }
        });
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: compile errors (`Route`, `Passes`, `ReadLayers`, `OptiScalerControlService`).

- [ ] **Step 3: Implement GameEntry additions**

In `GameEntry` add fields and properties:

```csharp
    private InstallRoute _route;
    private int _passes = 1;
```

```csharp
    public InstallRoute Route { get => _route; set { if (Set(ref _route, value)) { OnPropertyChanged(nameof(RouteLabel)); OnPropertyChanged(nameof(IsPreSr)); OnPropertyChanged(nameof(IsPostFsr)); } } }
    public int Passes { get => _passes; set => Set(ref _passes, value); }
    public string RouteLabel => InstallRoutes.Label(Route);
    public bool IsPreSr => Route == InstallRoute.OptiScalerPreSr;
    public bool IsPostFsr => Route == InstallRoute.PostFsrRuntime;
```

Change `Set<T>` to return `bool`:

```csharp
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        if (name == nameof(Installed)) OnPropertyChanged(nameof(InstallLabel));
        return true;
    }
```

- [ ] **Step 4: Implement RuntimeControlService changes**

Replace `Refresh` in `RuntimeControlService`:

```csharp
    public void Refresh(GameEntry game, bool? runningOverride = null)
    {
        lock (ConfigWriteLock)
        {
            game.LiveAcknowledged = false;
            game.Route = ManagedManifest.ReadRoute(game);
            game.Running = runningOverride ?? IsRunning(game.ExePath);
            if (game.Route == InstallRoute.OptiScalerPreSr)
            {
                game.Installed = File.Exists(game.OptiScalerIniPath) && HasProxy(game.DirectoryPath);
                if (game.Installed)
                {
                    var ini = IniDocument.Load(game.OptiScalerIniPath);
                    game.Enabled = ini.GetBool(OptiScalerControlService.Section, "Enabled", true);
                    game.LocalStructure = Math.Clamp(ini.GetDouble(OptiScalerControlService.Section, "LocalStructure", 1.0), 0.0, 2.0);
                    game.LocalTone = Math.Clamp(ini.GetDouble(OptiScalerControlService.Section, "LocalTone", 0.0), 0.0, 2.0);
                    game.SkinStructure = Math.Clamp(ini.GetDouble(OptiScalerControlService.Section, "SkinStructure", 1.0), 0.0, 2.0);
                    game.Passes = int.TryParse(ini.Get(OptiScalerControlService.Section, "Passes"), out var passes) ? Math.Clamp(passes, 1, 3) : 1;
                }
                else ResetControls(game);
                game.RuntimeStatus = !game.Installed ? "Not installed" : game.Running ? "Game running - Insert opens the OptiScaler menu" : "Ready for next launch";
                return;
            }

            game.Installed = File.Exists(game.ConfigPath) && HasProxy(game.DirectoryPath);
            if (File.Exists(game.ConfigPath))
            {
                var ini = IniDocument.Load(game.ConfigPath);
                game.Enabled = ini.GetBool(Section, "Enabled", true);
                game.LocalStructure = Math.Clamp(ini.GetDouble(Section, "LocalStructure", 1.0), 0.0, 2.0);
                game.LocalTone = Math.Clamp(ini.GetDouble(Section, "LocalTone", 1.0), 0.0, 2.0);
                game.SkinStructure = Math.Clamp(ini.GetDouble(Section, "SkinStructure", 1.0), 0.0, 2.0);
            }
            else ResetControls(game);
            game.Passes = 1;
            game.RuntimeStatus = !game.Installed ? "Not installed" : game.Running ? "Game running - End opens AMD live controls" : "Ready for next launch";
        }
    }

    private static void ResetControls(GameEntry game)
    {
        game.Enabled = false;
        game.LocalStructure = game.LocalTone = game.SkinStructure = 1.0;
        game.Passes = 1;
    }
```

Add the layer API to `RuntimeControlService`:

```csharp
    private static readonly string[] LayerKeys = ["LocalStructure", "SkinStructure", "LocalTone"];

    public LayerState ReadLayers(string iniPath)
    {
        lock (ConfigWriteLock)
        {
            var ini = IniDocument.Load(iniPath);
            var skin = ini.GetDouble(Section, "SkinStructure", 1.0);
            return new LayerState(
                Math.Clamp(ini.GetDouble(Section, "LocalStructure", 1.0), 0.0, 2.0),
                skin < 0 ? 1.0 : Math.Clamp(skin, 0.0, 2.0),
                Math.Clamp(ini.GetDouble(Section, "LocalTone", 0.0), 0.0, 2.0),
                skin < 0);
        }
    }

    public Task<RuntimeChangeResult> SetLayerAsync(string iniPath, string key, double value, CancellationToken cancellationToken = default)
    {
        if (!LayerKeys.Contains(key, StringComparer.Ordinal)) throw new ArgumentException("Unknown layer key.", nameof(key));
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var stored = key == "SkinStructure" && value < 0 ? -1.0 : Math.Clamp(value, 0.0, 2.0);
        return ChangeIniAsync(iniPath, ini => ini.Set(Section, key, stored.ToString("0.0", CultureInfo.InvariantCulture)), cancellationToken);
    }

    public Task<RuntimeChangeResult> AdjustLayerAsync(string iniPath, string key, double delta, CancellationToken cancellationToken = default)
    {
        if (!LayerKeys.Contains(key, StringComparer.Ordinal)) throw new ArgumentException("Unknown layer key.", nameof(key));
        if (!double.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta));
        return ChangeIniAsync(iniPath, ini =>
        {
            var current = ini.GetDouble(Section, key, key == "LocalTone" ? 0.0 : 1.0);
            if (current < 0) current = ini.GetDouble(Section, "LocalStructure", 1.0);
            var next = Math.Clamp(Math.Round(current + delta, 1, MidpointRounding.AwayFromZero), 0.0, 2.0);
            ini.Set(Section, key, next.ToString("0.0", CultureInfo.InvariantCulture));
        }, cancellationToken);
    }

    private Task<RuntimeChangeResult> ChangeIniAsync(string iniPath, Action<IniDocument> change, CancellationToken cancellationToken)
    {
        lock (ConfigWriteLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(iniPath)) throw new InvalidOperationException("The runtime config was not found: " + Path.GetFileName(iniPath));
            var ini = IniDocument.Load(iniPath);
            change(ini);
            ini.SaveAtomic(iniPath);
            return Task.FromResult(new RuntimeChangeResult(false, "Saved. The runtime reloads this file while it runs."));
        }
    }
```

Add after `RuntimeChangeResult`:

```csharp
public sealed record LayerState(double Structure, double Skin, double Tone, bool SkinFollowsStructure);
```

- [ ] **Step 5: Implement OptiScalerControlService**

Create `app/Dlss5AmdSwapper/Services/OptiScalerControlService.cs`:

```csharp
using System.Globalization;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public sealed class OptiScalerControlService
{
    public const string Section = "DlssNr";
    private static readonly object WriteLock = new();
    private readonly RuntimeControlService _runtime = new();

    public Task<RuntimeChangeResult> SetEnabledAsync(GameEntry game, bool enabled, CancellationToken cancellationToken = default) =>
        ChangeAsync(game, ini => ini.Set(Section, "Enabled", enabled ? "true" : "false"), cancellationToken);

    public Task<RuntimeChangeResult> SetPassesAsync(GameEntry game, int passes, CancellationToken cancellationToken = default)
    {
        if (passes is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(passes), "Passes must be 1, 2 or 3.");
        return ChangeAsync(game, ini => ini.Set(Section, "Passes", passes.ToString(CultureInfo.InvariantCulture)), cancellationToken);
    }

    public Task<RuntimeChangeResult> SetStructureAsync(GameEntry game, double value, CancellationToken cancellationToken = default) => SetScalarAsync(game, "LocalStructure", value, cancellationToken);
    public Task<RuntimeChangeResult> SetSkinAsync(GameEntry game, double value, CancellationToken cancellationToken = default) => SetScalarAsync(game, "SkinStructure", value, cancellationToken);
    public Task<RuntimeChangeResult> SetToneAsync(GameEntry game, double value, CancellationToken cancellationToken = default) => SetScalarAsync(game, "LocalTone", value, cancellationToken);

    private Task<RuntimeChangeResult> SetScalarAsync(GameEntry game, string key, double value, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return ChangeAsync(game, ini => ini.Set(Section, key, Math.Clamp(value, 0.0, 2.0).ToString("0.0", CultureInfo.InvariantCulture)), cancellationToken);
    }

    private Task<RuntimeChangeResult> ChangeAsync(GameEntry game, Action<IniDocument> change, CancellationToken cancellationToken)
    {
        lock (WriteLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (game.Busy) throw new InvalidOperationException("Wait for the current game operation to finish before changing settings.");
            if (!File.Exists(game.OptiScalerIniPath)) throw new InvalidOperationException("OptiScaler.ini is not installed for this game.");
            var ini = IniDocument.Load(game.OptiScalerIniPath);
            change(ini);
            ini.SaveAtomic(game.OptiScalerIniPath);
            _runtime.Refresh(game);
            return Task.FromResult(new RuntimeChangeResult(false, game.Running
                ? "Saved. OptiScaler reads OptiScaler.ini at startup; use the Insert menu for live changes."
                : "Saved for the next launch"));
        }
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Expected: two new `PASS` lines; every earlier `RuntimeControlRegressionTests` test still passes.

- [ ] **Step 7: Commit**

```bash
git add app/Dlss5AmdSwapper/Models/GameEntry.cs app/Dlss5AmdSwapper/Services/RuntimeControlService.cs app/Dlss5AmdSwapper/Services/OptiScalerControlService.cs app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs
git commit -m "Make runtime refresh route-aware and add pre-SR and layer controls

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 9: Hotkey sets for Lossless Scaling layers

**Files:**
- Modify: `app/Dlss5AmdSwapper/Services/HotkeyService.cs`
- Modify: `app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs`

**Interfaces:**
- Produces: `enum SwapperHotkey { Toggle = 1, Decrease = 2, Increase = 3, CycleLayer = 4, LayerDecrease = 5, LayerIncrease = 6 }`, `enum HotkeySet { DirectGame, LosslessLayers }`, `HotkeyService.Attach(IntPtr handle, HotkeySet set)`, `HotkeySet Set { get; }`, `static string Describe(HotkeySet set)` (`"F6/F7/F8"` or `"F9/F10/F11"`), `static (SwapperHotkey Key, uint VirtualKey)[] Bindings(HotkeySet set)`.

- [ ] **Step 1: Write the failing test**

Append inside `RunAsync`:

```csharp
        await run("Hotkey sets map to distinct virtual keys", () =>
        {
            var direct = HotkeyService.Bindings(HotkeySet.DirectGame);
            var layers = HotkeyService.Bindings(HotkeySet.LosslessLayers);
            Check(direct.Select(b => b.VirtualKey).SequenceEqual([0x75u, 0x76u, 0x77u]), "F6-F8");
            Check(layers.Select(b => b.VirtualKey).SequenceEqual([0x78u, 0x79u, 0x7Au]), "F9-F11");
            Check(layers.Select(b => b.Key).SequenceEqual([SwapperHotkey.CycleLayer, SwapperHotkey.LayerDecrease, SwapperHotkey.LayerIncrease]), "layer keys");
            Check(HotkeyService.Describe(HotkeySet.LosslessLayers) == "F9/F10/F11", "describe");
            return Task.CompletedTask;
        });
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: compile errors for `HotkeySet` / `Bindings`.

- [ ] **Step 3: Implement**

Replace the enum and `Attach`/`Register` in `HotkeyService.cs`:

```csharp
public enum SwapperHotkey { Toggle = 1, Decrease = 2, Increase = 3, CycleLayer = 4, LayerDecrease = 5, LayerIncrease = 6 }

public enum HotkeySet { DirectGame, LosslessLayers }
```

```csharp
    public HotkeySet Set { get; private set; }

    public static (SwapperHotkey Key, uint VirtualKey)[] Bindings(HotkeySet set) => set switch
    {
        HotkeySet.LosslessLayers => [(SwapperHotkey.CycleLayer, 0x78), (SwapperHotkey.LayerDecrease, 0x79), (SwapperHotkey.LayerIncrease, 0x7A)],
        _ => [(SwapperHotkey.Toggle, 0x75), (SwapperHotkey.Decrease, 0x76), (SwapperHotkey.Increase, 0x77)]
    };

    public static string Describe(HotkeySet set) => set == HotkeySet.LosslessLayers ? "F9/F10/F11" : "F6/F7/F8";

    public void Attach(IntPtr handle, HotkeySet set = HotkeySet.DirectGame)
    {
        _handle = handle;
        Set = set;
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Could not attach to the app window.");
        _source.AddHook(Hook);
        foreach (var (key, virtualKey) in Bindings(set)) Register(key, virtualKey);
    }

    private void Register(SwapperHotkey hotkey, uint key)
    {
        if (!RegisterHotKey(_handle, (int)hotkey, ModControl | ModAlt | ModNoRepeat, key))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not register Ctrl+Alt+F{key - 0x70 + 1}.");
        _registered.Add((int)hotkey);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Expected: `PASS  Hotkey sets map to distinct virtual keys`.

- [ ] **Step 5: Commit**

```bash
git add app/Dlss5AmdSwapper/Services/HotkeyService.cs app/Dlss5AmdSwapper.SmokeTests/OptiScalerTests.cs
git commit -m "Add a second hotkey set for Lossless Scaling layer control

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 10: Manager UI — settings, setup dialog, game panel, LS layer sliders and hotkeys

WPF has no automated tests here; the gate is `dotnet build` clean, the smoke suite green, and a manual run checklist at the end of the task.

**Files:**
- Modify: `app/Dlss5AmdSwapper/Models/AppSettings.cs`
- Create: `app/Dlss5AmdSwapper/SetupDialog.xaml`, `app/Dlss5AmdSwapper/SetupDialog.xaml.cs`
- Modify: `app/Dlss5AmdSwapper/MainWindow.xaml`
- Modify: `app/Dlss5AmdSwapper/MainWindow.xaml.cs`
- Create: `app/Dlss5AmdSwapper/MainWindow.OptiScaler.cs` (all new pre-SR + LS-layer code-behind lives here to keep `MainWindow.xaml.cs` from growing)

**Interfaces:**
- Consumes: everything from Tasks 1–9.
- Produces: settings properties `OptiScalerPackagePath`, `LocalWeightsPath`, `OptiScalerDefaultPreset` (string `"Quality"`/`"Performance"`); `SetupDialog` returning `(InstallRoute Route, OptiScalerPreset Preset)?`; LS layer view properties `LosslessStructure`, `LosslessSkin`, `LosslessTone`, `LosslessSkinFollows`, `LosslessLayerStatus`.

- [ ] **Step 1: Settings model**

Add to `AppSettings`:

```csharp
    public string OptiScalerPackagePath { get; set; } = string.Empty;
    public string LocalWeightsPath { get; set; } = string.Empty;
    public string OptiScalerDefaultPreset { get; set; } = "Quality";
```

- [ ] **Step 2: Setup dialog**

`app/Dlss5AmdSwapper/SetupDialog.xaml`:

```xml
<Window x:Class="Dlss5AmdSwapper.SetupDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Set up Neural Rendering" Width="560" SizeToContent="Height" WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize" Background="{DynamicResource BgBrush}" Foreground="{DynamicResource TextBrush}" FontSize="12">
    <StackPanel Margin="24">
        <TextBlock Text="{Binding GameName}" FontSize="20" FontWeight="SemiBold"/>
        <TextBlock Text="Choose how Neural Rendering reaches this game." Foreground="{DynamicResource MutedBrush}" Margin="0,4,0,16"/>

        <TextBlock Text="ROUTE" Foreground="{DynamicResource MutedBrush}" FontSize="10" FontWeight="Bold"/>
        <RadioButton x:Name="PreSrRadio" GroupName="Route" Margin="0,8,0,0" Foreground="{DynamicResource TextBrush}" IsEnabled="{Binding PreSrAvailable}">
            <StackPanel><TextBlock Text="OptiScaler pre-SR (render-resolution neural pass)" FontWeight="SemiBold"/><TextBlock Text="{Binding PackageSummary}" Foreground="{DynamicResource MutedBrush}" TextWrapping="Wrap" FontSize="10"/><TextBlock Text="{Binding WeightsSummary}" Foreground="{DynamicResource MutedBrush}" TextWrapping="Wrap" FontSize="10"/></StackPanel>
        </RadioButton>
        <RadioButton x:Name="PostFsrRadio" GroupName="Route" Margin="0,10,0,0" Foreground="{DynamicResource TextBrush}">
            <StackPanel><TextBlock Text="Post-FSR runtime (official DLSS-NR-on-AMD setup)" FontWeight="SemiBold"/><TextBlock Text="Runs after FSR at the game's FSR input resolution." Foreground="{DynamicResource MutedBrush}" FontSize="10"/></StackPanel>
        </RadioButton>

        <TextBlock Text="PRE-SR PRESET" Foreground="{DynamicResource MutedBrush}" FontSize="10" FontWeight="Bold" Margin="0,18,0,0"/>
        <RadioButton x:Name="QualityRadio" GroupName="Preset" Margin="0,8,0,0" Foreground="{DynamicResource TextBrush}" IsEnabled="{Binding ElementName=PreSrRadio, Path=IsChecked}">
            <StackPanel><TextBlock Text="Quality" FontWeight="SemiBold"/><TextBlock Text="One pass, pre-SR on, frame generation off, the game's own FSR ratio." Foreground="{DynamicResource MutedBrush}" FontSize="10"/></StackPanel>
        </RadioButton>
        <RadioButton x:Name="PerformanceRadio" GroupName="Preset" Margin="0,8,0,0" Foreground="{DynamicResource TextBrush}" IsEnabled="{Binding ElementName=PreSrRadio, Path=IsChecked}">
            <StackPanel><TextBlock Text="Performance" FontWeight="SemiBold"/><TextBlock Text="Forces a 3.0x upscale ratio and 3x frame generation through OptiScaler. Adds latency; the FG path needs the game's own DLSS Frame Generation option." Foreground="{DynamicResource MutedBrush}" TextWrapping="Wrap" FontSize="10"/></StackPanel>
        </RadioButton>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,22,0,0">
            <Button Content="Cancel" MinWidth="90" Click="Cancel_Click" Margin="0,0,10,0"/>
            <Button Content="Set up" MinWidth="110" Style="{StaticResource PrimaryButton}" Click="Confirm_Click"/>
        </StackPanel>
    </StackPanel>
</Window>
```

`app/Dlss5AmdSwapper/SetupDialog.xaml.cs`:

```csharp
using System.Windows;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper;

public partial class SetupDialog : Window
{
    public string GameName { get; }
    public string PackageSummary { get; }
    public string WeightsSummary { get; }
    public bool PreSrAvailable { get; }
    public InstallRoute Route { get; private set; } = InstallRoute.None;
    public OptiScalerPreset Preset { get; private set; } = OptiScalerPreset.Quality;

    public SetupDialog(string gameName, string packageSummary, string weightsSummary, bool preSrAvailable, OptiScalerPreset defaultPreset)
    {
        GameName = gameName;
        PackageSummary = packageSummary;
        WeightsSummary = weightsSummary;
        PreSrAvailable = preSrAvailable;
        InitializeComponent();
        DataContext = this;
        (preSrAvailable ? PreSrRadio : PostFsrRadio).IsChecked = true;
        (defaultPreset == OptiScalerPreset.Performance ? PerformanceRadio : QualityRadio).IsChecked = true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Route = PreSrRadio.IsChecked == true ? InstallRoute.OptiScalerPreSr : InstallRoute.PostFsrRuntime;
        Preset = PerformanceRadio.IsChecked == true ? OptiScalerPreset.Performance : OptiScalerPreset.Quality;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
```

- [ ] **Step 3: New code-behind partial**

Create `app/Dlss5AmdSwapper/MainWindow.OptiScaler.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

namespace Dlss5AmdSwapper;

public partial class MainWindow
{
    private readonly OptiScalerPackageService _optiPackages = new();
    private readonly OptiScalerControlService _optiControl = new();
    private readonly OptiScalerDiagnosticsService _optiDiagnostics = new();
    private OptiScalerInstallerService? _optiInstaller;
    private OptiScalerPackage? _optiPackage;
    private LocalWeights? _localWeights;
    private string _optiPackageStatus = "OptiScaler package: not checked";
    private string _losslessLayerStatus = "Bridge runtime config not found";
    private string _losslessLayerTarget = "LocalStructure";
    private LayerState _losslessLayers = new(1, 1, 0, true);

    private OptiScalerInstallerService OptiInstaller => _optiInstaller ??= new OptiScalerInstallerService(_probe, _installer);

    public string OptiScalerPackagePath { get => _settings.OptiScalerPackagePath; set { _settings.OptiScalerPackagePath = value; OnPropertyChanged(); SaveSettings(); } }
    public string LocalWeightsPath { get => _settings.LocalWeightsPath; set { _settings.LocalWeightsPath = value; OnPropertyChanged(); SaveSettings(); } }
    public string[] PresetNames { get; } = ["Quality", "Performance"];
    public string OptiScalerDefaultPreset { get => _settings.OptiScalerDefaultPreset; set { if (value is null) return; _settings.OptiScalerDefaultPreset = value; OnPropertyChanged(); SaveSettings(); } }
    public string OptiScalerPackageStatus { get => _optiPackageStatus; private set => Set(ref _optiPackageStatus, value); }
    public int[] PassOptions { get; } = [1, 2, 3];

    public double LosslessStructure => _losslessLayers.Structure;
    public double LosslessSkin => _losslessLayers.Skin;
    public double LosslessTone => _losslessLayers.Tone;
    public bool LosslessSkinFollows => _losslessLayers.SkinFollowsStructure;
    public string LosslessLayerStatus { get => _losslessLayerStatus; private set => Set(ref _losslessLayerStatus, value); }
    private string? LosslessRuntimeIni => string.IsNullOrWhiteSpace(LosslessInstallPath) ? null : Path.Combine(LosslessInstallPath, "nr-bridge", "runtime", "dlssnr_on_amd.ini");
    private OptiScalerPreset DefaultPreset => OptiScalerDefaultPreset == "Performance" ? OptiScalerPreset.Performance : OptiScalerPreset.Quality;

    private async Task<bool> ResolveOptiScalerSourcesAsync(bool showToast)
    {
        _optiPackage = null;
        _localWeights = null;
        try
        {
            var candidates = await Task.Run(() => _optiPackages.DiscoverCandidates(OptiScalerPackagePath));
            string? failure = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    var root = candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? await Task.Run(() => _optiPackages.EnsureExtracted(candidate)) : candidate;
                    _optiPackage = await Task.Run(() => OptiScalerPackageService.Validate(root));
                    if (!string.Equals(OptiScalerPackagePath, candidate, StringComparison.OrdinalIgnoreCase)) OptiScalerPackagePath = candidate;
                    break;
                }
                catch (InvalidOperationException error) { failure ??= $"{Path.GetFileName(candidate)}: {error.Message}"; }
            }
            _localWeights = await Task.Run(() => OptiScalerPackageService.FindLocalWeights(LocalWeightsPath, Games.Select(game => game.DirectoryPath), LosslessInstallPath));
            if (_localWeights is not null && !string.Equals(LocalWeightsPath, _localWeights.Path, StringComparison.OrdinalIgnoreCase)) LocalWeightsPath = _localWeights.Path;
            OptiScalerPackageStatus = _optiPackage is null
                ? "OptiScaler package: " + (failure ?? "none found. Put the OptiScaler-AMD-PreSR-Multipass folder or zip in Downloads, or choose it below.")
                : $"OptiScaler package: {_optiPackage.Summary}" + (_localWeights is null ? " · weights: none found (run the post-FSR route once so the runtime generates them)" : " · weights ready");
            if (showToast) ShowToast(OptiScalerPackageStatus, _optiPackage is not null && _localWeights is not null);
            return _optiPackage is not null && _localWeights is not null;
        }
        catch (Exception ex)
        {
            OptiScalerPackageStatus = "OptiScaler package: " + ex.Message;
            if (showToast) ShowError(ex);
            return false;
        }
    }

    private async void RefreshOptiScalerSources_Click(object sender, RoutedEventArgs e) => await ResolveOptiScalerSourcesAsync(true);

    private void BrowseOptiScalerPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the OptiScaler AMD pre-SR package folder" };
        if (dialog.ShowDialog(this) == true) OptiScalerPackagePath = dialog.FolderName;
    }

    private void BrowseLocalWeights_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Select a generated dlssnr_on_amd_weights.bin", "dlssnr_on_amd_weights.bin|dlssnr_on_amd_weights.bin|All files (*.*)|*.*");
        if (path is not null) LocalWeightsPath = path;
    }

    // Called by InstallSelected_Click when the selected game has no managed route yet.
    private async Task SetUpNewRouteAsync(GameEntry target)
    {
        var preSrReady = await ResolveOptiScalerSourcesAsync(false);
        var dialog = new SetupDialog(target.Name,
            _optiPackage?.Summary ?? OptiScalerPackageStatus,
            _localWeights is null ? "Weights: none found" : $"Weights: {Path.GetFileName(Path.GetDirectoryName(_localWeights.Path))} copy, {_localWeights.Size / (1024 * 1024)} MB",
            preSrReady, DefaultPreset) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.Route == InstallRoute.PostFsrRuntime)
        {
            await InstallPostFsrAsync(target, update: false);
            return;
        }
        ShowToast("Installing OptiScaler pre-SR…");
        var result = await OptiInstaller.InstallAsync(target, _optiPackage!, _localWeights!, dialog.Preset, update: false);
        _runtime.Refresh(target);
        if (ReferenceEquals(SelectedGame, target)) await RefreshDiagnosticsAsync(false);
        RecordActivity("Game installed (pre-SR)", $"{target.Name} · {result.Preset} · {result.Written.Count} files");
        ShowToast($"Installed pre-SR for {target.Name} · {result.Preset} preset · launch the game with FSR enabled", true);
    }

    private async Task UpdatePreSrAsync(GameEntry target)
    {
        if (!await ResolveOptiScalerSourcesAsync(false)) throw new InvalidOperationException(OptiScalerPackageStatus);
        var preset = Enum.TryParse<OptiScalerPreset>(IniDocument.Load(target.OptiScalerIniPath).Get("UpscaleRatio", "UpscaleRatioOverrideEnabled") == "true" ? "Performance" : "Quality", out var current) ? current : OptiScalerPreset.Quality;
        ShowToast("Updating OptiScaler pre-SR…");
        var result = await OptiInstaller.InstallAsync(target, _optiPackage!, _localWeights!, preset, update: true);
        _runtime.Refresh(target);
        RecordActivity("Game updated (pre-SR)", $"{target.Name} · {result.Preset}");
        ShowToast($"Updated pre-SR for {target.Name}", true);
    }

    private async void PassesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedGame is not { IsPreSr: true } game || sender is not ComboBox combo || combo.SelectedItem is not int passes || passes == game.Passes) return;
        try { ShowToast((await _optiControl.SetPassesAsync(game, passes)).Message); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task RefreshPreSrDiagnosticsAsync(GameEntry target, bool toast)
    {
        var diag = await _optiDiagnostics.InspectAsync(target);
        if (!ReferenceEquals(SelectedGame, target) || _closing) return;
        DiagnosticsSummary = diag.Summary;
        var parts = new List<string> { "Evidence: amd_presr.log + OptiScaler.log tail (1 MiB), whole files hashed" };
        if (diag.HipAdapter is not null) parts.Add("HIP " + diag.HipAdapter);
        if (diag.MeanModelMs is not null) parts.Add($"model {diag.MeanModelMs:0.0} ms mean ({diag.CostSamples} samples)");
        if (diag.PassesInitialized > 0) parts.Add($"{diag.PassesInitialized} pass runtime(s) initialised");
        DiagnosticsDetail = string.Join(" · ", parts);
        if (diag.PreSrActive) target.RuntimeStatus = "Pre-SR observed";
        if (toast) ShowToast(diag.Summary, diag.PreSrActive);
    }

    private void RefreshLosslessLayers()
    {
        var ini = LosslessRuntimeIni;
        if (ini is null || !File.Exists(ini)) { LosslessLayerStatus = "Bridge runtime config not found. Install the bridge first."; return; }
        try
        {
            _losslessLayers = _runtime.ReadLayers(ini);
            OnPropertyChanged(nameof(LosslessStructure)); OnPropertyChanged(nameof(LosslessSkin)); OnPropertyChanged(nameof(LosslessTone)); OnPropertyChanged(nameof(LosslessSkinFollows));
            LosslessLayerStatus = $"Layers apply while the bridge runs; the runtime reloads {Path.GetFileName(ini)}. Hotkeys Ctrl+Alt+F9 (cycle) / F10 (−) / F11 (+) target: {_losslessLayerTarget}";
        }
        catch (Exception ex) { LosslessLayerStatus = "Could not read layers: " + ex.Message; }
    }

    private async Task ApplyLosslessLayerAsync(string key, double value)
    {
        var ini = LosslessRuntimeIni;
        if (ini is null) return;
        try { ShowToast((await _runtime.SetLayerAsync(ini, key, value)).Message); }
        catch (Exception ex) { ShowError(ex); }
        RefreshLosslessLayers();
    }

    private async void LosslessStructureSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyLosslessLayerAsync("LocalStructure", LosslessStructureSlider.Value);
    private async void LosslessSkinSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyLosslessLayerAsync("SkinStructure", LosslessSkinSlider.Value);
    private async void LosslessToneSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyLosslessLayerAsync("LocalTone", LosslessToneSlider.Value);
    private async void LosslessSkinFollows_Click(object sender, RoutedEventArgs e) => await ApplyLosslessLayerAsync("SkinStructure", LosslessSkinFollowsBox.IsChecked == true ? -1 : LosslessSkinSlider.Value);

    private async Task HandleLayerHotkeyAsync(SwapperHotkey hotkey)
    {
        var ini = LosslessRuntimeIni;
        if (ini is null || !File.Exists(ini)) return;
        if (hotkey == SwapperHotkey.CycleLayer)
        {
            _losslessLayerTarget = _losslessLayerTarget switch { "LocalStructure" => "SkinStructure", "SkinStructure" => "LocalTone", _ => "LocalStructure" };
            RefreshLosslessLayers();
            ShowToast("Layer hotkeys now adjust " + _losslessLayerTarget);
            return;
        }
        var result = await _runtime.AdjustLayerAsync(ini, _losslessLayerTarget, hotkey == SwapperHotkey.LayerIncrease ? 0.1 : -0.1);
        RefreshLosslessLayers();
        ShowToast($"{_losslessLayerTarget}: {result.Message}");
    }
}
```

- [ ] **Step 4: Wire `MainWindow.xaml.cs`**

Replace `InstallSelected_Click` body's try block:

```csharp
        try
        {
            switch (target.Route)
            {
                case InstallRoute.OptiScalerPreSr: await UpdatePreSrAsync(target); break;
                case InstallRoute.PostFsrRuntime: await InstallPostFsrAsync(target, update: true); break;
                default: await SetUpNewRouteAsync(target); break;
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _installOperation = false; }
```

Add the extracted post-FSR helper (the old body):

```csharp
    private async Task InstallPostFsrAsync(GameEntry target, bool update)
    {
        ShowToast("Preparing verified runtime sources…");
        var sources = await ResolveRuntimeSourcesAsync(promptForNr: true);
        ShowToast(update ? "Updating AMD Neural Rendering…" : "Installing AMD Neural Rendering…");
        var result = await _installer.InstallAsync(target, sources.SetupPath, sources.NrDllPath!, update);
        _runtime.Refresh(target);
        if (ReferenceEquals(SelectedGame, target)) await RefreshDiagnosticsAsync(false);
        RecordActivity(update ? "Game updated" : "Game installed", target.Name);
        ShowToast($"{(update ? "Updated" : "Installed")} {target.Name} · upstream {result.UpstreamTag} · rich config verified", true);
    }
```

`RestoreSelected_Click`: replace `var result = await _installer.RemoveAsync(target, removeModel: true);` with

```csharp
            var result = target.Route == InstallRoute.OptiScalerPreSr
                ? await OptiInstaller.RemoveAsync(target)
                : await _installer.RemoveAsync(target, removeModel: true);
```

`DirectEnabledSwitch_Click`: replace the guard and the call:

```csharp
        if (SelectedGame is null || SelectedGame.Route == InstallRoute.None) return;
        ...
            var result = target.IsPreSr ? await _optiControl.SetEnabledAsync(target, desired) : await _runtime.SetEnabledAsync(target, desired);
```

Sliders:

```csharp
    private async void StructureSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyScalarAsync(() => SelectedGame!.IsPreSr ? _optiControl.SetStructureAsync(SelectedGame!, StructureSlider.Value) : _runtime.SetStructureAsync(SelectedGame!, StructureSlider.Value));
    private async void ToneSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyScalarAsync(() => SelectedGame!.IsPreSr ? _optiControl.SetToneAsync(SelectedGame!, ToneSlider.Value) : _runtime.SetToneAsync(SelectedGame!, ToneSlider.Value));
    private async void SkinSlider_MouseUp(object sender, MouseButtonEventArgs e) => await ApplyScalarAsync(() => SelectedGame!.IsPreSr ? _optiControl.SetSkinAsync(SelectedGame!, SkinSlider.Value) : _runtime.SetSkinStructureAsync(SelectedGame!, SkinSlider.Value));
```

`ApplyScalarAsync` guard: `if (SelectedGame is null || SelectedGame.Route == InstallRoute.None) return;`

`RefreshDiagnosticsAsync`: after the null check add

```csharp
        if (SelectedGame.IsPreSr)
        {
            try { await RefreshPreSrDiagnosticsAsync(SelectedGame, toast); }
            catch (Exception ex) { if (toast) ShowError(ex); }
            return;
        }
```

`Navigate`: in `case "Lossless":` append `RefreshLosslessLayers();`. In `MainWindow_Loaded` after `RefreshLosslessStatus();` add `_ = ResolveOptiScalerSourcesAsync(false); RefreshLosslessLayers();`.

`UpdateHotkeyRegistration` — replace the whole method:

```csharp
    private void UpdateHotkeyRegistration()
    {
        if (_closing) return;
        var bridgeRunning = _runningProcessNames.Contains("DlssNrBridge");
        var losslessRunning = _runningProcessNames.Contains("LosslessScaling");
        var target = Games.FirstOrDefault(game => game.Installed && game.Running) ?? (SelectedGame?.Installed == true && SelectedGame.Running ? SelectedGame : null);
        HotkeySet? wanted = null;
        if (RegisterHotkeys && !bridgeRunning && target is not null) wanted = HotkeySet.DirectGame;
        else if (RegisterHotkeys && losslessRunning && LosslessRuntimeIni is { } ini && File.Exists(ini)) wanted = HotkeySet.LosslessLayers;

        if (wanted is null || (_hotkeys is not null && _hotkeys.Set != wanted))
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
        }
        if (wanted is null)
        {
            HotkeyStatus = bridgeRunning ? "Lossless Scaling bridge owns F6/F7/F8" : RegisterHotkeys ? "Waiting for a managed game or Lossless Scaling" : "Hotkeys disabled";
            return;
        }
        var label = wanted == HotkeySet.DirectGame ? $"F6/F7/F8 → {target!.Name}" : $"F9/F10/F11 → Lossless Scaling layers ({_losslessLayerTarget})";
        if (_hotkeys is not null) { HotkeyStatus = label; return; }
        try
        {
            _hotkeys = new HotkeyService(HandleHotkey);
            _hotkeys.Attach(new WindowInteropHelper(this).Handle, wanted.Value);
            HotkeyStatus = label;
        }
        catch (Exception ex)
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            HotkeyStatus = "Hotkeys unavailable: " + ex.Message;
        }
    }
```

`HandleHotkey` — first line of the method body:

```csharp
        if (hotkey is SwapperHotkey.CycleLayer or SwapperHotkey.LayerDecrease or SwapperHotkey.LayerIncrease) { try { await HandleLayerHotkeyAsync(hotkey); } catch (Exception ex) { ShowError(ex); } return; }
```

and the direct-game switch becomes route-aware:

```csharp
            RuntimeChangeResult result = hotkey switch
            {
                SwapperHotkey.Toggle => target.IsPreSr ? await _optiControl.SetEnabledAsync(target, !target.Enabled) : await _runtime.SetEnabledAsync(target, !target.Enabled),
                SwapperHotkey.Decrease => target.IsPreSr ? await _optiControl.SetStructureAsync(target, target.LocalStructure - 0.1) : await _runtime.AdjustStructureAsync(target, -0.1),
                SwapperHotkey.Increase => target.IsPreSr ? await _optiControl.SetStructureAsync(target, target.LocalStructure + 0.1) : await _runtime.AdjustStructureAsync(target, 0.1),
                _ => new RuntimeChangeResult(false, "No action")
            };
```

- [ ] **Step 5: XAML**

Library row (inside the `StackPanel Grid.Column="1"` of the game `DataTemplate`, after the `Store` TextBlock):

```xml
<TextBlock Text="{Binding RouteLabel}" Foreground="{DynamicResource MutedBrush}" FontSize="9" Margin="0,3,0,0"/>
```

Game detail "DIRECT-GAME STATUS" card: after the `SelectedGame.Status` TextBlock add

```xml
<TextBlock Text="{Binding SelectedGame.RouteLabel, TargetNullValue=No route}" Foreground="{DynamicResource Accent2Brush}" FontSize="11" Margin="0,4,0,0"/>
```

Effect controls card: before the Structure grid add

```xml
<Grid Margin="0,0,0,12" Visibility="{Binding SelectedGame.IsPreSr, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}">
    <Grid.ColumnDefinitions><ColumnDefinition Width="140"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
    <TextBlock Text="Neural passes" VerticalAlignment="Center"/>
    <ComboBox Grid.Column="1" Width="90" HorizontalAlignment="Left" ItemsSource="{Binding PassOptions}" SelectedItem="{Binding SelectedGame.Passes, Mode=OneWay}" SelectionChanged="PassesCombo_SelectionChanged" Margin="8,0"/>
</Grid>
```

If `BoolToVisibility` is not already declared in `App.xaml`/window resources, add `<BooleanToVisibilityConverter x:Key="BoolToVisibility"/>` to `Window.Resources`.

Lossless page: after the "Private runtime files" card add

```xml
<Border Style="{StaticResource CardStyle}" Margin="0,12,0,0">
    <StackPanel>
        <TextBlock Text="Neural layers" FontSize="16" FontWeight="SemiBold"/>
        <TextBlock Text="{Binding LosslessLayerStatus}" Foreground="{DynamicResource MutedBrush}" FontSize="10" TextWrapping="Wrap" Margin="0,4,0,14"/>
        <Grid>
            <Grid.ColumnDefinitions><ColumnDefinition Width="140"/><ColumnDefinition Width="*"/><ColumnDefinition Width="55"/></Grid.ColumnDefinitions>
            <TextBlock Text="Structure" VerticalAlignment="Center"/>
            <Slider Grid.Column="1" x:Name="LosslessStructureSlider" Minimum="0" Maximum="2" TickFrequency="0.1" Value="{Binding LosslessStructure, Mode=OneWay}" PreviewMouseLeftButtonUp="LosslessStructureSlider_MouseUp" Margin="8,0"/>
            <TextBlock Grid.Column="2" Text="{Binding LosslessStructure, StringFormat=F1}" HorizontalAlignment="Right" VerticalAlignment="Center" Foreground="{DynamicResource Accent2Brush}"/>
        </Grid>
        <Grid Margin="0,12,0,0">
            <Grid.ColumnDefinitions><ColumnDefinition Width="140"/><ColumnDefinition Width="*"/><ColumnDefinition Width="55"/></Grid.ColumnDefinitions>
            <TextBlock Text="Skin structure" VerticalAlignment="Center"/>
            <Slider Grid.Column="1" x:Name="LosslessSkinSlider" Minimum="0" Maximum="2" TickFrequency="0.1" Value="{Binding LosslessSkin, Mode=OneWay}" IsEnabled="{Binding LosslessSkinFollows, Converter={StaticResource InvertBool}}" PreviewMouseLeftButtonUp="LosslessSkinSlider_MouseUp" Margin="8,0"/>
            <TextBlock Grid.Column="2" Text="{Binding LosslessSkin, StringFormat=F1}" HorizontalAlignment="Right" VerticalAlignment="Center" Foreground="{DynamicResource Accent2Brush}"/>
        </Grid>
        <CheckBox x:Name="LosslessSkinFollowsBox" Content="Skin follows structure (runtime default)" IsChecked="{Binding LosslessSkinFollows, Mode=OneWay}" Click="LosslessSkinFollows_Click" Foreground="{DynamicResource TextBrush}" Margin="148,8,0,0"/>
        <Grid Margin="0,12,0,0">
            <Grid.ColumnDefinitions><ColumnDefinition Width="140"/><ColumnDefinition Width="*"/><ColumnDefinition Width="55"/></Grid.ColumnDefinitions>
            <TextBlock Text="Local tone" VerticalAlignment="Center"/>
            <Slider Grid.Column="1" x:Name="LosslessToneSlider" Minimum="0" Maximum="2" TickFrequency="0.1" Value="{Binding LosslessTone, Mode=OneWay}" PreviewMouseLeftButtonUp="LosslessToneSlider_MouseUp" Margin="8,0"/>
            <TextBlock Grid.Column="2" Text="{Binding LosslessTone, StringFormat=F1}" HorizontalAlignment="Right" VerticalAlignment="Center" Foreground="{DynamicResource Accent2Brush}"/>
        </Grid>
    </StackPanel>
</Border>
```

Add an `InvertBool` converter class `app/Dlss5AmdSwapper/InvertBoolConverter.cs` (`IValueConverter` returning `!(bool)value` both ways) and register `<local:InvertBoolConverter x:Key="InvertBool"/>` in `Window.Resources` (with `xmlns:local="clr-namespace:Dlss5AmdSwapper"`).

Settings page: after the "Runtime sources" card add

```xml
<Border Style="{StaticResource CardStyle}" Margin="0,0,0,12">
    <StackPanel>
        <TextBlock Text="OPTISCALER PRE-SR PACKAGE" Foreground="{DynamicResource MutedBrush}" FontSize="10" FontWeight="Bold"/>
        <TextBlock Text="{Binding OptiScalerPackageStatus}" FontSize="13" TextWrapping="Wrap" Margin="0,7,0,0"/>
        <TextBlock Text="The pre-SR route needs the OptiScaler AMD pre-SR multipass package you obtained yourself. Nothing is downloaded or bundled; the app only verifies and copies your local files." Foreground="{DynamicResource MutedBrush}" FontSize="10" TextWrapping="Wrap" Margin="0,6,0,12"/>
        <TextBlock Style="{StaticResource FieldLabel}" Text="PACKAGE FOLDER OR ZIP"/>
        <Grid>
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <TextBox Text="{Binding OptiScalerPackagePath, UpdateSourceTrigger=PropertyChanged}"/>
            <Button Grid.Column="1" Style="{StaticResource GhostButton}" Content="Browse" Click="BrowseOptiScalerPackage_Click" Margin="9,0,0,0"/>
            <Button Grid.Column="2" Style="{StaticResource GhostButton}" Content="Find in Downloads" Click="RefreshOptiScalerSources_Click" Margin="9,0,0,0"/>
        </Grid>
        <TextBlock Style="{StaticResource FieldLabel}" Text="GENERATED WEIGHTS (dlssnr_on_amd_weights.bin)" Margin="0,15,0,6"/>
        <Grid>
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <TextBox Text="{Binding LocalWeightsPath, UpdateSourceTrigger=PropertyChanged}"/>
            <Button Grid.Column="1" Style="{StaticResource GhostButton}" Content="Browse" Click="BrowseLocalWeights_Click" Margin="9,0,0,0"/>
        </Grid>
        <TextBlock Style="{StaticResource FieldLabel}" Text="DEFAULT PRESET" Margin="0,15,0,6"/>
        <ComboBox Width="180" HorizontalAlignment="Left" ItemsSource="{Binding PresetNames}" SelectedItem="{Binding OptiScalerDefaultPreset, Mode=TwoWay}"/>
    </StackPanel>
</Border>
```

Hotkeys card text: replace with "The manager registers Ctrl+Alt+F6/F7/F8 while a managed direct-game target is running, and Ctrl+Alt+F9/F10/F11 for Lossless Scaling layers while Lossless Scaling is running. The bridge keeps F6/F7/F8 while it is active."

- [ ] **Step 6: Build and run the smoke suite**

Run: `dotnet build .\app\Dlss5AmdSwapper\Dlss5AmdSwapper.csproj -c Release` → 0 errors. Then the smoke suite → all green.

- [ ] **Step 7: Manual checklist (run `dotnet run --project .\app\Dlss5AmdSwapper\Dlss5AmdSwapper.csproj -c Release`)**

1. Settings shows the package status line; "Find in Downloads" finds `OptiScaler-AMD-PreSR-Multipass-v1.2` inside `Arquivos necessarios` after the zip in Downloads is extracted there (or the zip itself).
2. Weights path auto-fills to the Lossless Scaling bridge runtime copy.
3. Games → Crimson Desert → route badge shows "Post-FSR runtime"; Set up (Update) still goes through the post-FSR path.
4. Any game with no route → Set up opens the dialog with both routes; pre-SR radio enabled only when package + weights are ready.
5. Lossless page shows the three layer sliders with the current INI values (`Structure 1.0`, skin follows checked, tone 0.0).
6. Minimum window size still lays out (existing verification rule).

- [ ] **Step 8: Commit**

```bash
git add app/Dlss5AmdSwapper
git commit -m "Add pre-SR route setup, package settings and Lossless Scaling layer controls to the manager

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 11: Python CLI mirror

**Files:**
- Modify: `direct-game/amd_dlss5.py`
- Create: `direct-game/tests/__init__.py` (empty), `direct-game/tests/test_amd_dlss5.py`

**Interfaces:**
- Produces functions in `amd_dlss5.py`: `is_real_weights(path: Path) -> bool`, `contains_marker(path: Path, marker: bytes) -> bool`, `pe_product_version(path: Path) -> tuple[str | None, str | None]` (parses `VS_VERSIONINFO` strings `ProductName`/`ProductVersion` from the PE resource; returns `(None, None)` when absent), `validate_package(root: Path, version_reader=None) -> dict`, `find_local_weights(configured: Path | None, game_dirs: list[Path], lossless: Path | None) -> dict | None`, `build_optiscaler_ini(base_text: str | None, preset: str, enabler: bool) -> str`, `install_optiscaler(args) -> dict`, `remove_optiscaler(args) -> dict`, `summarize_presr(folder: Path) -> dict`.
- CLI: `--route {post-fsr,optiscaler-presr}` (default `post-fsr`), `--package DIR`, `--weights FILE`, `--preset {quality,performance}` (default `quality`), `--proxy-name NAME` (default `dxgi.dll`), `--passes N`.
- Manifest JSON written by `install_optiscaler` uses exactly the schema 3 field names from the spec so the app can read it.

- [ ] **Step 1: Write the failing tests**

`direct-game/tests/test_amd_dlss5.py`:

```python
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import amd_dlss5 as helper  # noqa: E402


def fake_fork(_path):
    return ("OptiScaler", "10.0.0-dev (amd-presr-multipass-local) (20260907_075847)")


def write_pe(path: Path, marker: bytes = b"") -> Path:
    shutil.copy(sys.executable, path)
    if marker:
        with path.open("ab") as stream:
            stream.write(marker)
    return path


def make_package(parent: Path) -> Path:
    root = parent / "OptiScaler-AMD-PreSR-Multipass-v1.2"
    (root / "OptiScaler").mkdir(parents=True)
    write_pe(root / "OptiScaler.dll")
    for index in (1, 2, 3):
        write_pe(root / f"dlssnr_amd_pass{index}.dll", b"dlssnr_amd")
    (root / "OptiScaler.ini").write_text("[Upscalers]\nDx12Upscaler=auto\n\n[DlssNr]\nEnabled=auto\n", encoding="utf-8")
    (root / "dlssnr_on_amd_weights.bin").write_text("version https://git-lfs.github.com/spec/v1\n", encoding="utf-8")
    (root / "OptiScaler" / "amd_fidelityfx_upscaler_dx12.dll").write_bytes(b"\x01\x02")
    return root


class Fixture:
    def __init__(self):
        self.temp = Path(tempfile.mkdtemp(prefix="dlss5-presr-"))
        self.package = make_package(self.temp)
        self.game_dir = self.temp / "Game"
        self.game_dir.mkdir()
        self.exe = write_pe(self.game_dir / "FixtureGame.exe", b"d3d12.dll")
        (self.game_dir / "amd_fidelityfx_upscaler_dx12.dll").write_bytes(b"\x01")
        self.weights = self.temp / "weights.bin"
        self.weights.write_bytes(b"\x00" * (1024 * 1024 + 3))

    def cleanup(self):
        shutil.rmtree(self.temp, ignore_errors=True)


class PackageTests(unittest.TestCase):
    def test_validate_package(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        info = helper.validate_package(f.package, version_reader=fake_fork)
        self.assertEqual(info["layout"], "package")
        self.assertEqual(len(info["pass_dlls"]), 3)
        self.assertIsNone(info["weights"])
        self.assertIn("amd-presr", info["fork_version"])
        with self.assertRaises(RuntimeError):
            helper.validate_package(f.package, version_reader=lambda _p: ("OptiScaler", "10.0.0-dev (792f2f1)"))

    def test_weights_detection(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        self.assertFalse(helper.is_real_weights(f.package / "dlssnr_on_amd_weights.bin"))
        self.assertTrue(helper.is_real_weights(f.weights))
        found = helper.find_local_weights(None, [f.temp], None)
        self.assertIsNone(found)
        shutil.copy(f.weights, f.game_dir / "dlssnr_on_amd_weights.bin")
        found = helper.find_local_weights(None, [f.game_dir], None)
        self.assertEqual(found["size"], f.weights.stat().st_size)

    def test_ini_presets(self):
        quality = helper.build_optiscaler_ini("[DlssNr]\nEnabled=auto\n", "quality", False)
        self.assertIn("RunBeforeSR=true", quality)
        self.assertIn("Dx12Upscaler=ffx", quality)
        self.assertNotIn("FGInput", quality)
        performance = helper.build_optiscaler_ini(None, "performance", True)
        self.assertIn("UpscaleRatioOverrideValue=3.0", performance)
        self.assertIn("FGNvngxReplacement=combo", performance)
        self.assertIn("InterpolationCount=2", performance)
        self.assertIn("FGNvngxReplacement=ffx", helper.build_optiscaler_ini(None, "performance", False))


class InstallTests(unittest.TestCase):
    def test_install_and_remove(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        args = helper.parse_args(["--game", str(f.exe), "--install", "--route", "optiscaler-presr", "--package", str(f.package), "--weights", str(f.weights)])
        manifest = helper.install_optiscaler(args, version_reader=fake_fork)
        self.assertEqual(manifest["route"], "amd-optiscaler-presr")
        self.assertEqual(manifest["schema_version"], 3)
        for name in ("dxgi.dll", "OptiScaler.ini", "dlssnr_amd_pass3.dll", "dlssnr_on_amd_weights.bin", os.path.join("OptiScaler", "amd_fidelityfx_upscaler_dx12.dll")):
            self.assertTrue((f.game_dir / name).exists(), name)
        on_disk = json.loads((f.game_dir / ".dlss5-amd-swapper.json").read_text(encoding="utf-8"))
        self.assertEqual(on_disk["installed_proxy_names"], ["dxgi.dll"])
        self.assertIn("preexisting_dependencies", on_disk)
        with self.assertRaises(RuntimeError):
            helper.install_optiscaler(args, version_reader=fake_fork)

        removed = helper.remove_optiscaler(helper.parse_args(["--game", str(f.exe), "--remove"]))
        self.assertIn("dxgi.dll", removed["removed"])
        self.assertFalse((f.game_dir / "dlssnr_amd_pass1.dll").exists())
        self.assertFalse((f.game_dir / ".dlss5-amd-swapper.json").exists())

    def test_install_rolls_back(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "OptiScaler.ini").mkdir()
        args = helper.parse_args(["--game", str(f.exe), "--install", "--route", "optiscaler-presr", "--package", str(f.package), "--weights", str(f.weights)])
        with self.assertRaises(Exception):
            helper.install_optiscaler(args, version_reader=fake_fork)
        self.assertFalse((f.game_dir / "dxgi.dll").exists())
        self.assertFalse((f.game_dir / "dlssnr_on_amd_weights.bin").exists())
        self.assertFalse((f.game_dir / ".dlss5-amd-swapper.json").exists())

    def test_diagnose_presr(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "amd_presr.log").write_text("HIP adapter: AMD Radeon RX 9070 XT\nInitialized independent AMD pass 1\nCompleted AMD pre-SR passes=1\n", encoding="utf-8")
        (f.game_dir / "OptiScaler.log").write_text("DlssNr_Dx12::Dispatch DLSS-NR running before SR: target 3840x2160, model 1280x720, guides 1280x720 (preset 0)\nDLSS-NR cost: 12.00 ms total = 10.00 ms model + 2.00 ms ours (16% ours)\n", encoding="utf-8")
        summary = helper.summarize_presr(f.game_dir)
        self.assertTrue(summary["pre_sr_active"])
        self.assertEqual(summary["model_size"], "1280x720")
        self.assertEqual(summary["passes_completed"], 1)
        self.assertAlmostEqual(summary["mean_total_ms"], 12.0)
        self.assertEqual(len(summary["presr_log_sha256"]), 64)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `py -m unittest discover -s .\direct-game\tests -v`
Expected: `AttributeError: module 'amd_dlss5' has no attribute 'validate_package'` (and `parse_args`).

- [ ] **Step 3: Implement in `amd_dlss5.py`**

Add constants after `ANTI_CHEAT_MARKERS`:

```python
ROUTE_POST_FSR = "amd-fsr-direct"
ROUTE_OPTISCALER = "amd-optiscaler-presr"
OPTI_PROXY_NAMES = ("dxgi.dll", "version.dll", "winmm.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll")
OPTI_PASS_NAMES = ("dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll")
OPTI_ROOT_MANAGED = ("OptiScaler.ini", "OptiScaler.log", "amd_presr.log", *OPTI_PASS_NAMES, "dlssnr_on_amd_weights.bin")
OPTI_WEIGHTS = "dlssnr_on_amd_weights.bin"
OPTI_ENABLER = "dlss-enabler-headless.dll"
OPTI_DEPENDENCY_FOLDER = "OptiScaler"
OPTI_REQUIRED_UPSCALER = "amd_fidelityfx_upscaler_dx12.dll"
OPTI_FORK_MARKER = "amd-presr"
OPTI_PASS_MARKER = b"dlssnr_amd"
```

Helpers (after `file_state`):

```python
def is_real_weights(path: Path) -> bool:
    if not path.is_file() or path.stat().st_size <= 1024 * 1024:
        return False
    with path.open("rb") as stream:
        return not stream.read(32).startswith(b"version https://git-lfs")


def contains_marker(path: Path, marker: bytes) -> bool:
    carry = b""
    with path.open("rb") as stream:
        while True:
            chunk = stream.read(4 * 1024 * 1024)
            if not chunk:
                return False
            data = carry + chunk
            if marker in data:
                return True
            carry = data[-(len(marker) - 1):] if len(marker) > 1 else b""


def pe_product_version(path: Path) -> tuple[str | None, str | None]:
    """Read ProductName/ProductVersion from the VS_VERSIONINFO string table without Win32 APIs."""
    data = path.read_bytes()
    marker = "VS_VERSION_INFO".encode("utf-16le")
    start = data.find(marker)
    if start < 0:
        return None, None
    block = data[start:start + 8192]

    def read_value(key: str) -> str | None:
        needle = key.encode("utf-16le") + b"\x00\x00"
        index = block.find(needle)
        if index < 0:
            return None
        cursor = index + len(needle)
        while block[cursor:cursor + 2] == b"\x00\x00":
            cursor += 2
        end = block.find(b"\x00\x00", cursor)
        while end % 2:
            end = block.find(b"\x00\x00", end + 1)
        return block[cursor:end].decode("utf-16le", errors="replace").strip("\x00") or None

    return read_value("ProductName"), read_value("ProductVersion")


def parse_sha256sums(path: Path) -> list[tuple[str, str]]:
    entries: list[tuple[str, str]] = []
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = raw.strip()
        if len(line) < 66 or not all(ch in "0123456789abcdefABCDEF" for ch in line[:64]):
            continue
        entries.append((line[64:].lstrip(" *").replace("/", "\\"), line[:64].lower()))
    return entries


def validate_package(root: Path, version_reader=None) -> dict[str, Any]:
    version_reader = version_reader or pe_product_version
    root = root.resolve()
    if not root.is_dir():
        raise RuntimeError("The OptiScaler package folder does not exist")
    if (root / "OptiScaler.dll").is_file():
        fork, layout = root / "OptiScaler.dll", "package"
    elif (root / "dxgi.dll").is_file():
        fork, layout = root / "dxgi.dll", "vodkaman"
    else:
        raise RuntimeError("No OptiScaler.dll or dxgi.dll was found in the package folder")
    if pe_machine(fork) != 0x8664:
        raise RuntimeError(f"{fork.name} is not a 64-bit Windows PE file")
    product, version = version_reader(fork)
    if (product or "").lower() != "optiscaler":
        raise RuntimeError(f"{fork.name} does not identify itself as OptiScaler")
    if not version or OPTI_FORK_MARKER not in version.lower():
        raise RuntimeError("This OptiScaler build is not the AMD pre-SR fork; the pre-SR route needs a build whose version contains amd-presr")

    passes: list[Path] = []
    for name in OPTI_PASS_NAMES:
        candidate = root / name
        if not candidate.is_file():
            if not passes:
                raise RuntimeError("dlssnr_amd_pass1.dll was not found in the package folder")
            continue
        if pe_machine(candidate) != 0x8664:
            raise RuntimeError(f"{name} is not a 64-bit Windows PE file")
        if not contains_marker(candidate, OPTI_PASS_MARKER):
            raise RuntimeError(f"{name} does not look like a DLSS-NR-on-AMD runtime (marker missing)")
        passes.append(candidate)

    files: dict[str, dict[str, Any]] = {}

    def record(path: Path) -> None:
        files[str(path.relative_to(root))] = file_state(path)

    record(fork)
    for item in passes:
        record(item)
    ini = root / "OptiScaler.ini"
    enabler = root / OPTI_ENABLER
    deps = root / OPTI_DEPENDENCY_FOLDER
    sums = root / "SHA256SUMS.txt"
    if ini.is_file():
        record(ini)
    if enabler.is_file():
        record(enabler)
    if deps.is_dir():
        for path in sorted(deps.rglob("*")):
            if path.is_file():
                record(path)
    sums_verified = False
    if sums.is_file():
        for relative, expected in parse_sha256sums(sums):
            full = (root / relative).resolve()
            if root not in full.parents:
                raise RuntimeError(f"SHA256SUMS.txt lists a path outside the package: {relative}")
            if not full.is_file():
                continue
            actual = files.get(str(full.relative_to(root)), {}).get("sha256") or sha256(full)
            if actual != expected:
                raise RuntimeError(f"SHA256SUMS.txt does not match {relative}. Re-download the package before installing")
        sums_verified = True
    weights = root / OPTI_WEIGHTS
    return {
        "root": str(root),
        "layout": layout,
        "optiscaler_dll": str(fork),
        "pass_dlls": [str(item) for item in passes],
        "ini": str(ini) if ini.is_file() else None,
        "dependency_folder": str(deps) if deps.is_dir() else None,
        "enabler": str(enabler) if enabler.is_file() else None,
        "weights": str(weights) if is_real_weights(weights) else None,
        "fork_version": version,
        "files": files,
        "sha256sums_verified": sums_verified,
    }


def find_local_weights(configured: Path | None, game_dirs: list[Path], lossless: Path | None) -> dict[str, Any] | None:
    candidates: list[Path] = []
    if configured:
        candidates.append(configured)
    if lossless:
        candidates.append(lossless / "nr-bridge" / "runtime" / OPTI_WEIGHTS)
        candidates.append(lossless / OPTI_WEIGHTS)
    candidates.extend(directory / OPTI_WEIGHTS for directory in game_dirs)
    chosen: dict[str, Any] | None = None
    seen: set[str] = set()
    for candidate in candidates:
        key = str(candidate.resolve()).lower()
        if key in seen or not is_real_weights(candidate):
            continue
        seen.add(key)
        state = {"source": str(candidate.resolve()), **file_state(candidate)}
        if chosen is None:
            chosen = state
        elif chosen["sha256"] != state["sha256"]:
            raise RuntimeError(f"Two local weights files differ: {chosen['source']} and {state['source']}")
    return chosen


MINIMAL_INI_HEADER = "; Written by DLSS5 AMD Swapper. Unlisted OptiScaler keys keep their defaults.\n; Open the in-game OptiScaler menu (Insert) to change anything else.\n"


def _ini_set(lines: list[str], section: str, key: str, value: str) -> None:
    section_index = next((i for i, line in enumerate(lines) if line.strip().lower() == f"[{section.lower()}]"), -1)
    if section_index < 0:
        if lines and lines[-1].strip():
            lines.append("")
        lines.extend([f"[{section}]", f"{key}={value}"])
        return
    end = len(lines)
    for i in range(section_index + 1, len(lines)):
        stripped = lines[i].strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            end = i
            break
        if "=" in lines[i] and lines[i].split("=", 1)[0].strip().lower() == key.lower():
            lines[i] = f"{key}={value}"
            return
    lines.insert(end, f"{key}={value}")


def build_optiscaler_ini(base_text: str | None, preset: str, enabler: bool) -> str:
    lines = (base_text if base_text and base_text.strip() else MINIMAL_INI_HEADER).replace("\r\n", "\n").split("\n")
    for section, key, value in (
        ("Upscalers", "Dx12Upscaler", "ffx"),
        ("DlssNr", "Enabled", "true"), ("DlssNr", "RunBeforeSR", "true"), ("DlssNr", "Passes", "1"),
        ("DlssNr", "LocalTone", "0"), ("DlssNr", "LocalStructure", "1"), ("DlssNr", "SkinStructure", "1"), ("DlssNr", "ApplyAfterRR", "false"),
        ("Log", "LogToFile", "true"), ("Log", "LogLevel", "2"),
    ):
        _ini_set(lines, section, key, value)
    if preset == "performance":
        for section, key, value in (
            ("UpscaleRatio", "UpscaleRatioOverrideEnabled", "true"), ("UpscaleRatio", "UpscaleRatioOverrideValue", "3.0"),
            ("FrameGen", "Enabled", "true"), ("FrameGen", "FGInput", "nvngxfg"), ("FrameGen", "FGNvngxReplacement", "combo" if enabler else "ffx"),
            ("DLSSG", "InterpolationCount", "2"),
        ):
            _ini_set(lines, section, key, value)
    return "\n".join(lines).rstrip("\n") + "\n"
```

Install / remove / diagnose (after `remove`):

```python
def _read_manifest(folder: Path) -> tuple[Path | None, dict[str, Any] | None]:
    path = find_manifest(folder)
    if path is None:
        return None, None
    try:
        return path, json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return path, None


def manifest_route(folder: Path) -> str | None:
    path, manifest = _read_manifest(folder)
    if path is None or manifest is None:
        return None
    return manifest.get("route") or ROUTE_POST_FSR


def install_optiscaler(args: argparse.Namespace, version_reader=None) -> dict[str, Any]:
    game = Path(args.game).resolve()
    info = check_game(game)
    if not info["x64"]:
        raise RuntimeError("The pre-SR route supports 64-bit games only")
    if info["anti_cheat_markers"]:
        raise RuntimeError("Anti-cheat markers were found. Direct-game installation is blocked for this target")
    if not info["fsr_markers"] and not args.force:
        raise RuntimeError("No FSR runtime marker was found near the game executable")
    if not info["dx12_evidence"] and not args.force:
        raise RuntimeError("No DirectX 12 evidence was found near the game executable")
    if not args.package or not args.weights:
        raise RuntimeError("--route optiscaler-presr requires --package and --weights")
    package = validate_package(Path(args.package), version_reader)
    weights = Path(args.weights).resolve()
    if not is_real_weights(weights):
        raise RuntimeError("--weights must point to a generated dlssnr_on_amd_weights.bin")
    proxy_name = args.proxy_name or "dxgi.dll"
    if proxy_name.lower() not in OPTI_PROXY_NAMES:
        raise RuntimeError("Unsupported --proxy-name")
    folder = game.parent
    package_upscaler = Path(package["dependency_folder"]) / OPTI_REQUIRED_UPSCALER if package["dependency_folder"] else None
    if not (package_upscaler and package_upscaler.is_file()) and not (folder / OPTI_REQUIRED_UPSCALER).is_file():
        raise RuntimeError(f"{OPTI_REQUIRED_UPSCALER} is missing from both the package and the game folder")

    route = manifest_route(folder)
    previous_manifest: dict[str, Any] | None = None
    if route == ROUTE_POST_FSR:
        raise RuntimeError("This game has the post-FSR route installed. Run --remove first, then install the pre-SR route")
    if route == ROUTE_OPTISCALER and not args.update:
        raise RuntimeError("This game already has the pre-SR route. Use --update")
    if route == ROUTE_OPTISCALER:
        previous_manifest = _read_manifest(folder)[1]
    if route is None and args.update:
        raise RuntimeError("--update requires an existing managed pre-SR manifest")

    dependency_relatives: list[str] = []
    if package["dependency_folder"]:
        deps = Path(package["dependency_folder"])
        dependency_relatives = [str(Path(OPTI_DEPENDENCY_FOLDER) / path.relative_to(deps)) for path in sorted(deps.rglob("*")) if path.is_file()]
    enabler_relative = str(Path(OPTI_DEPENDENCY_FOLDER) / OPTI_ENABLER)
    managed = list(dict.fromkeys([*OPTI_PROXY_NAMES, *OPTI_ROOT_MANAGED, *dependency_relatives, enabler_relative]))

    def snapshot() -> dict[str, Any]:
        return {name: state for name in managed if (state := file_state(folder / name)) is not None}

    before = snapshot()
    if not args.update:
        unmanaged = [name for name in OPTI_PROXY_NAMES if name in before]
        if unmanaged:
            raise RuntimeError("A proxy DLL already exists in the game folder and is not managed by this helper: " + ", ".join(unmanaged))
    manifest_path = folder / MANIFEST_NAME
    previous_bytes = manifest_path.read_bytes() if manifest_path.is_file() else None

    with tempfile.TemporaryDirectory(prefix="dlss5-amd-swapper-presr-backup-") as temp_name:
        backup = Path(temp_name)
        for name in before:
            target = backup / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(folder / name, target)
        written: list[str] = []
        preexisting: list[str] = []
        try:
            def copy_verified(source: Path, relative: str) -> None:
                destination = folder / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, destination)
                if sha256(source) != sha256(destination):
                    raise RuntimeError(f"Copy verification failed for {relative}")
                written.append(relative)

            copy_verified(Path(package["optiscaler_dll"]), proxy_name)
            for index, name in enumerate(OPTI_PASS_NAMES):
                source = package["pass_dlls"][index] if index < len(package["pass_dlls"]) else package["pass_dlls"][0]
                copy_verified(Path(source), name)
            copy_verified(weights, OPTI_WEIGHTS)
            for relative in dependency_relatives:
                source = Path(package["root"]) / relative
                if relative in before and before[relative]["sha256"] == sha256(source):
                    preexisting.append(relative)
                    continue
                copy_verified(source, relative)
            enabler_available = package["enabler"] is not None
            if args.preset == "performance" and enabler_available:
                copy_verified(Path(package["enabler"]), enabler_relative)
            base_text = Path(package["ini"]).read_text(encoding="utf-8", errors="replace") if package["ini"] else None
            (folder / "OptiScaler.ini").write_text(build_optiscaler_ini(base_text, args.preset, enabler_available), encoding="utf-8")
            written.append("OptiScaler.ini")

            after = snapshot()
            manifest = {
                "schema_version": 3,
                "created_unix": int(time.time()),
                "route": ROUTE_OPTISCALER,
                "game_exe": game.name,
                "proxy_name": proxy_name,
                "preset": args.preset,
                "package": {
                    "root": package["root"], "layout": package["layout"], "fork_version": package["fork_version"],
                    "sha256sums_verified": package["sha256sums_verified"], "files": package["files"],
                },
                "weights": {"source": str(weights), **file_state(weights)},
                "compatibility": {"x64": info["x64"], "fsr_markers": info["fsr_markers"], "dx12_evidence": info["dx12_evidence"], "anti_cheat_markers": info["anti_cheat_markers"]},
                "previous_route": previous_manifest.get("previous_route") if previous_manifest else None,
                "before": previous_manifest.get("before") if previous_manifest else before,
                "after": after,
                "preexisting_dependencies": sorted(set((previous_manifest or {}).get("preexisting_dependencies", [])) | set(preexisting)),
                "installed_proxy_names": [proxy_name],
            }
            manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
            legacy = folder / LEGACY_MANIFEST_NAME
            if legacy.is_file():
                legacy.unlink()
            return manifest
        except Exception:
            for name in managed:
                target = folder / name
                if name in before:
                    source = backup / name
                    if source.is_file():
                        shutil.copy2(source, target)
                elif target.is_file():
                    target.unlink()
            deps_dir = folder / OPTI_DEPENDENCY_FOLDER
            if deps_dir.is_dir() and not any(deps_dir.rglob("*")):
                shutil.rmtree(deps_dir, ignore_errors=True)
            if previous_bytes is not None:
                manifest_path.write_bytes(previous_bytes)
            elif manifest_path.exists():
                manifest_path.unlink()
            raise


def remove_optiscaler(args: argparse.Namespace) -> dict[str, Any]:
    folder = Path(args.game).resolve().parent
    manifest_path, manifest = _read_manifest(folder)
    if manifest_path is None or manifest is None or manifest.get("route") != ROUTE_OPTISCALER:
        raise RuntimeError("No managed pre-SR manifest was found")
    before = manifest.get("before", {})
    after = manifest.get("after", {})
    preexisting = set(manifest.get("preexisting_dependencies", []))
    removed: list[str] = []
    preserved: list[str] = []
    for name in dict.fromkeys([*after.keys(), *manifest.get("installed_proxy_names", [])]):
        path = folder / name
        if name in before or name in preexisting:
            preserved.append(name)
            continue
        if not path.is_file():
            continue
        if after.get(name) == file_state(path):
            path.unlink()
            removed.append(name)
        else:
            preserved.append(name)
    deps_dir = folder / OPTI_DEPENDENCY_FOLDER
    if deps_dir.is_dir() and not any(path.is_file() for path in deps_dir.rglob("*")):
        shutil.rmtree(deps_dir, ignore_errors=True)
    remaining = sorted(name for name in after if name not in before and name not in preexisting and (folder / name).is_file())
    if not remaining:
        manifest_path.unlink()
    return {"removed": sorted(removed), "preserved": sorted(set(preserved)), "manifest_retained": bool(remaining), "remaining_managed_files": remaining}


def summarize_presr(folder: Path) -> dict[str, Any]:
    def read(name: str) -> tuple[str, str | None, int]:
        path = folder / name
        if not path.is_file():
            return "", None, 0
        raw = path.read_bytes()
        return raw[-1024 * 1024:].decode("utf-8", errors="replace"), hashlib.sha256(raw).hexdigest(), len(raw)

    presr, presr_hash, presr_bytes = read("amd_presr.log")
    opti, opti_hash, opti_bytes = read("OptiScaler.log")
    completed = [int(value) for value in re.findall(r"Completed AMD pre-SR passes=(\d+)", presr)]
    running = re.findall(r"DLSS-NR running [^:]*: target (\d+x\d+), model (\d+x\d+)", opti)
    costs = [(float(total), float(model)) for total, model in re.findall(r"DLSS-NR cost: ([\d.]+) ms total = ([\d.]+) ms model", opti)]
    fault = re.compile(r"^(AMD pre-SR: (?!idle)|HIP completion timeout|Unsupported AMD pre-SR|.*hash mismatch|dlssnr_on_amd_weights\.bin is required|.*LoadLibrary failed|AMD engine initialization failed|Cannot load amdhip64_7\.dll|AMD stopped|AMD timeout)")
    faults = [line.strip() for line in presr.splitlines() if fault.match(line.strip())]
    adapter = re.search(r"^HIP adapter:\s*(.+)$", presr, re.M)
    return {
        "pre_sr_active": bool(completed or running),
        "hip_adapter": adapter.group(1).strip() if adapter else None,
        "passes_initialized": len(re.findall(r"Initialized independent AMD pass \d+", presr)),
        "passes_completed": completed[-1] if completed else None,
        "target_size": running[-1][0] if running else None,
        "model_size": running[-1][1] if running else None,
        "mean_total_ms": sum(cost[0] for cost in costs) / len(costs) if costs else None,
        "mean_model_ms": sum(cost[1] for cost in costs) / len(costs) if costs else None,
        "cost_samples": len(costs),
        "last_fault": faults[-1] if faults else None,
        "presr_log_sha256": presr_hash, "presr_log_bytes": presr_bytes,
        "optiscaler_log_sha256": opti_hash, "optiscaler_log_bytes": opti_bytes,
    }
```

Refactor `main()` so the parser is reusable and route-aware:

```python
def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True, help="Path to the game's .exe")
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--check", action="store_true")
    action.add_argument("--install", action="store_true")
    action.add_argument("--update", action="store_true")
    action.add_argument("--remove", action="store_true")
    action.add_argument("--diagnose", action="store_true")
    parser.add_argument("--route", choices=("post-fsr", "optiscaler-presr"), default="post-fsr")
    parser.add_argument("--upstream-setup", help="User-downloaded official dlssnr_on_amd_setup.exe")
    parser.add_argument("--nr-dll", help="User-supplied nvngx_dlssnr.dll")
    parser.add_argument("--package", help="User-supplied OptiScaler AMD pre-SR package folder")
    parser.add_argument("--weights", help="Locally generated dlssnr_on_amd_weights.bin")
    parser.add_argument("--preset", choices=("quality", "performance"), default="quality")
    parser.add_argument("--proxy-name", default="dxgi.dll")
    parser.add_argument("--passes", type=int, choices=(1, 2, 3))
    parser.add_argument("--force", action="store_true", help="Override uncertain FSR/DX12 detection; anti-cheat remains blocked")
    parser.add_argument("--remove-model", action="store_true", help="Also remove a model DLL copied by this helper")
    parser.add_argument("--output", type=Path, help="Write JSON result/diagnostic to this path")
    return parser.parse_args(argv)


def main() -> int:
    args = parse_args()
    try:
        folder = Path(args.game).resolve().parent
        route = manifest_route(folder)
        if args.install or args.update:
            if args.route == "optiscaler-presr":
                result = install_optiscaler(args)
            else:
                if not args.upstream_setup or not args.nr_dll:
                    raise RuntimeError("--install/--update require --upstream-setup and --nr-dll")
                result = install(args)
        elif args.remove:
            result = remove_optiscaler(args) if route == ROUTE_OPTISCALER else remove(args)
        elif args.diagnose:
            result = diagnose(args)
            if route == ROUTE_OPTISCALER:
                result["route"] = ROUTE_OPTISCALER
                result["pre_sr"] = summarize_presr(folder)
        else:
            result = check_game(Path(args.game))
        encoded = json.dumps(result, indent=2) + "\n"
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(encoded, encoding="utf-8")
        print(encoded, end="")
        return 0
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError, subprocess.TimeoutExpired) as error:
        print(f"direct-game: {error}", file=sys.stderr)
        return 1
```

Also make `install()` refuse a pre-SR-managed folder: after `existing_manifest_path = find_manifest(folder)` add

```python
    if existing_manifest_path is not None and manifest_route(folder) == ROUTE_OPTISCALER:
        raise RuntimeError("This game is managed by the OptiScaler pre-SR route. Run --remove first")
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `py -m unittest discover -s .\direct-game\tests -v`
Expected: 6 tests OK.

- [ ] **Step 5: Commit**

```bash
git add direct-game/amd_dlss5.py direct-game/tests/__init__.py direct-game/tests/test_amd_dlss5.py
git commit -m "Mirror the OptiScaler pre-SR route in the direct-game CLI helper

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 12: Publication gate, package forbidden list, gitignore

**Files:**
- Modify: `tools/Check-Publication.py`
- Modify: `app/Build-Package.ps1:75-82` (forbidden list)
- Modify: `.gitignore`

- [ ] **Step 1: Extend the publication gate with a forbidden-filename scan**

In `Check-Publication.py` add after `FPS_NUMBER`:

```python
FORBIDDEN_FILENAMES = re.compile(
    r"(?i)^(dxgi\.dll|optiscaler.*\.dll|dlssnr_amd_pass\d\.dll|libxess.*\.dll|libxell\.dll|amd_fidelityfx_.*\.dll|d3d12core\.dll|"
    r"dlss-enabler.*\.dll|dlssnr_on_amd_weights\.bin|nvngx_dlssnr\.dll|nvngx\.dll_dlssnr\.dll|dlssnr_on_amd_setup\.exe|"
    r"lossless_original\.dll|instalar_amd\.ps1|diagnostico_amd\.ps1|amd_presr\.log|optiscaler\.log|dlssnr_on_amd\.log)$"
)
SKIP_DIRS = {".git", ".vs", "runs", "build", "bin", "obj", "runtime", "artifacts", "optiscaler-packages"}
```

Inside `main()` before the text loop, add a second pass that walks every tracked-looking file:

```python
    for path in ROOT.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(ROOT)
        if any(part in SKIP_DIRS for part in relative.parts):
            continue
        if FORBIDDEN_FILENAMES.match(path.name):
            failures.append(f"{relative}: forbidden third-party/private file present in the repository tree")
```

and reuse `SKIP_DIRS` in the existing text loop instead of the inline set.

- [ ] **Step 2: Extend the package forbidden list**

In `Build-Package.ps1` replace the `$forbidden` array with:

```powershell
$forbidden = @(
    "Lossless_original.dll", "LosslessScaling.exe", "version.dll", "dxgi.dll", "winmm.dll",
    "nvngx_dlssnr.dll", "nvngx.dll_dlssnr.dll", "dlssnr_on_amd_setup.exe", "dlssnr_on_amd_weights.bin",
    "OptiScaler.dll", "OptiScaler.ini", "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll",
    "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "libxell.dll", "D3D12Core.dll",
    "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_vk.dll",
    "dlss-enabler-headless.dll", "INSTALAR_AMD.ps1", "DIAGNOSTICO_AMD.ps1", "amd_presr.log", "OptiScaler.log"
)
```

- [ ] **Step 3: gitignore**

Append to `.gitignore` under "Private runtime assets":

```
optiscaler-packages/
backup-amd-presr-*/
amd_presr.log
OptiScaler.log
OptiScaler.ini
SHA256SUMS.txt
```

- [ ] **Step 4: Verify**

Run: `py .\tools\Check-Publication.py` → `Publication provenance check passed`. Then create a throwaway `dlssnr_amd_pass1.dll` in the repo root, run again → failure names it, delete it.

- [ ] **Step 5: Commit**

```bash
git add tools/Check-Publication.py app/Build-Package.ps1 .gitignore
git commit -m "Extend the publication gate to OptiScaler package files

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 13: Documentation, licensing, version and release metadata

**Files:**
- Create: `docs/optiscaler-presr.md`
- Modify: `docs/licensing.md`, `README.md`, `docs/install.md`, `direct-game/README.md`, `docs/development.md`, `docs/architecture.md`
- Create: `docs/releases/v0.3.0-pre.1.md`
- Modify: `VERSION`, `RELEASE.json`, `app/Dlss5AmdSwapper/Dlss5AmdSwapper.csproj` (`<Version>0.3.0-pre.1</Version>`)

Rules: no numeric frame-rate claim outside measurement JSON; describe speed with ms or ratios; every third-party name gets its licence row.

- [ ] **Step 1: `docs/optiscaler-presr.md`**

Sections and required content:

1. **What pre-SR means** — the fork evaluates the neural model on the render-resolution colour buffer before FSR upscales it. Cost scales with model pixels: a 3840x2160 frame rendered at 1280x720 sends about a quarter of the pixels of a 2560x1440 native pass. Reference this project's own Crimson Desert evidence only in ms terms (about 45 ms per network job at 2560x1440 with the post-FSR route).
2. **What the published 60-plus-frames-per-second setup actually used** — 3.0x FSR ratio, one pass, and 3x frame generation through OptiScaler. Say plainly that frame generation contributed and adds latency.
3. **Package contents and provenance** — table listing `OptiScaler.dll`/`dxgi.dll` (OptiScaler fork `amd-presr-multipass-local`), `dlssnr_amd_pass1/2/3.dll` (DLSS-NR-on-AMD v0.2.14 proxy builds), weights, `OptiScaler\` dependencies, `dlss-enabler-headless.dll`, `SHA256SUMS.txt`; what the manager verifies (PE x64, ProductName/ProductVersion, marker, SHA256SUMS); that nothing is downloaded or bundled and why (GPL-3.0 source not published for the fork; upstream runtime licence forbids redistribution; weights derive from NVIDIA's model).
4. **Setup** — Settings → package + weights → Games → Set up → route dialog → preset → launch with FSR enabled → `Insert` menu → diagnostics line.
5. **Presets** — the two INI blocks verbatim.
6. **Diagnostics fields** — the list from the spec.
7. **Restore** — hash-checked removal, pre-existing dependencies kept, manifest retained while a changed file remains.
8. **Limits** — DX12 + FSR call required, no Vulkan/D3D11, anti-cheat blocked, FG path needs the game's DLSS-G, hot reload of `OptiScaler.ini` not proven.
9. **CLI** — the `--route optiscaler-presr` examples.

- [ ] **Step 2: `docs/licensing.md`**

Add rows:

| Project | How I use it | Repository rule |
| --- | --- | --- |
| [cdozdil/OptiScaler](https://github.com/cdozdil/OptiScaler) | Upscaler/frame-generation host for the pre-SR route (GPL-3.0) | Never bundled. The pre-SR fork build is supplied by the user; the manager verifies and copies it locally. |
| [Dagherbou/OptiScaler_DLSSNR](https://github.com/Dagherbou/OptiScaler_DLSSNR) | DLSS-NR integration lineage of the fork | Reference only. |
| [Vodkaman23/DLSS-NR-UE5-Opti-DLL](https://github.com/Vodkaman23/DLSS-NR-UE5-Opti-DLL) | Public example of the fork + pass DLL layout | No licence published; its binaries are never redistributed, downloaded or modified by this project. |
| [gamegpu.com report](https://en.gamegpu.com/news/igry/dlss-5-teper-rabotaet-na-radeon-rx-9070-xt-i-rx-9060-xt-s-bolee-chem-60-fps-v-4k) | Credit for the pre-SR configuration | Their numbers are not measurements of this project. |

Add a paragraph: `dlssnr_amd_pass*.dll` are DLSS-NR-on-AMD proxy builds; the same non-redistribution rule applies to them as to the official setup. Add the OptiScaler package files to the "must not contain" list.

- [ ] **Step 3: README, install, direct-game README, development, architecture**

- README "Two routes" table → Direct Game row gains "two backends: post-FSR runtime, OptiScaler pre-SR"; "What the manager handles" gains "OptiScaler pre-SR package verification and reversible install"; Credits gain cdozdil/OptiScaler, Vodkaman23, gamegpu; Docs table gains `docs/optiscaler-presr.md`; "Public package boundary" gains OptiScaler package files.
- `docs/install.md`: new section "Direct Game — OptiScaler pre-SR" with the setup steps and the weights note.
- `direct-game/README.md`: CLI examples for `--route optiscaler-presr`.
- `docs/development.md`: smoke tests list gains the OptiScaler tests; Python tests command; direct-game invariants gain "pre-SR installs never bundle package files; weights come only from local generated copies".
- `docs/architecture.md`: add the `OptiScaler*` services and the manifest route field.

- [ ] **Step 4: Version and release metadata**

`VERSION` → `0.3.0-pre.1`. `Dlss5AmdSwapper.csproj` `<Version>0.3.0-pre.1</Version>`.

`RELEASE.json`: `version`/`tag` → `0.3.0-pre.1`/`v0.3.0-pre.1`, `date` → today, `published: false` until Task 14, `previous_published_version: "0.2.0-pre.1"`, add under `verification`: `"optiscaler_presr_route_smoke_tests_passed": true`, `"optiscaler_presr_python_tests_passed": true`, `"optiscaler_presr_live_install_verified": false`, `"optiscaler_presr_capture_recorded": false`; add `"external_optiscaler_package": { "expected_fork_marker": "amd-presr", "redistributed": false, "downloaded": false, "source": "user-supplied" }`.

`docs/releases/v0.3.0-pre.1.md`: "What changed" (pre-SR route, LS layers, gate), "What did not change" (bridge/compositor), "Verification" (tests; live results filled in Task 14), "Third-party boundary" (repeat the no-bundle statement).

- [ ] **Step 5: Gate and commit**

Run `py .\tools\Check-Publication.py` → passed.

```bash
git add docs README.md VERSION RELEASE.json direct-game/README.md app/Dlss5AmdSwapper/Dlss5AmdSwapper.csproj
git commit -m "Document the OptiScaler pre-SR route, licensing boundary and v0.3.0-pre.1 metadata

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 14: Live verification on the owner's PC, then publish

This task is interactive: the owner plays and judges visuals; the agent prepares, measures, records and publishes.

**Files:**
- Modify: `RELEASE.json`, `docs/releases/v0.3.0-pre.1.md`, `docs/measurements/<date>-presr-crimson-desert.json` (analyzer output)

- [ ] **Step 1: Build the package**

Run: `.\app\Build-Package.ps1` → smoke tests pass, ZIP under `artifacts\`. Confirm `SHA256SUMS.txt` lists no forbidden name.

- [ ] **Step 2: Prepare local sources**

Extract `C:\Users\Pine\Downloads\Videos pro Tech-20260911T003509Z-1-001.zip` → `Arquivos necessarios.zip` → folder `OptiScaler-AMD-PreSR-Multipass-v1.2` into `%USERPROFILE%\Downloads\`. Keep the 147 MB weights out of the repo. Launch `artifacts\DLSS5-AMD-Swapper-local\Dlss5AmdSwapper.exe`; Settings must show `OptiScaler package: 10.0.0-dev (amd-presr-multipass-local) … SHA256SUMS verified · weights ready` with weights from `D:\SteamLibrary\steamapps\common\Lossless Scaling\nr-bridge\runtime`.

- [ ] **Step 3: Install on Crimson Desert**

Games → Crimson Desert → Restore (post-FSR route) → Set up → OptiScaler pre-SR, Quality → confirm. Verify in `D:\SteamLibrary\steamapps\common\Crimson Desert\bin64`: `dxgi.dll`, `OptiScaler.ini` (`[DlssNr] RunBeforeSR=true`), `dlssnr_amd_pass1..3.dll`, `dlssnr_on_amd_weights.bin`, `OptiScaler\`, manifest route `amd-optiscaler-presr`, no `winmm.dll`.

- [ ] **Step 4: Owner plays**

Ask the owner to launch the game with FSR enabled (Quality mode first, then Performance/Ultra Performance to shrink the model input), open `Insert`, confirm "DLSS Neural Rendering → Enable Neural Rendering + AMD: apply before Super Resolution", play a fixed 60-second route, then close the game. Owner judges visuals.

- [ ] **Step 5: Evidence**

In the manager: Refresh evidence → expect `Pre-SR active · <w>x<h> model · <n> ms · passes 1`. Run:

```powershell
.\tools\Capture-Performance.ps1 -ProcessName CrimsonDesert.exe -Seconds 30
py .\bridge\scripts\Analyze-Run.py <capture inputs> --output docs\measurements\<date>-presr-crimson-desert.json
```

Use the analyzer's existing arguments (see `bridge/scripts/Analyze-Run.py --help`). Commit only the sanitized JSON. Record `model_size`, `mean_model_ms`, `passes_completed` in `RELEASE.json` under `verification.optiscaler_presr_live` and set `optiscaler_presr_live_install_verified: true`, `optiscaler_presr_capture_recorded: true`.

- [ ] **Step 6: Optional Performance preset**

If the owner wants the frame-generation setup: Update with Performance preset, relaunch, confirm FG state in the `Insert` menu, note the result in the release notes without a frame-rate number.

- [ ] **Step 7: Publish**

```bash
py .\tools\Check-Publication.py
git add RELEASE.json docs/releases/v0.3.0-pre.1.md docs/measurements
git commit -m "Record v0.3.0-pre.1 live verification

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
git checkout main
git merge --no-ff optiscaler-presr -m "Merge OptiScaler pre-SR route for v0.3.0-pre.1

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
git tag v0.3.0-pre.1
git push origin main v0.3.0-pre.1
gh release create v0.3.0-pre.1 artifacts/DLSS5-AMD-Swapper-v0.3.0-pre.1-win-x64.zip --title "DLSS5 AMD Swapper v0.3.0-pre.1" --notes-file docs/releases/v0.3.0-pre.1.md --prerelease
```

Before `git checkout main`: the worktree `nr-development` holds `main`; merge from there (`cd ..\nr-development && git merge --no-ff optiscaler-presr`) or use `git -C` accordingly — `main` cannot be checked out in two worktrees. Rename the ZIP produced by `Build-Package.ps1` to the tag name before uploading. Set `"published": true` in `RELEASE.json` in a final commit on `main`.

---

## Self-review

- **Spec coverage:** §1 architecture → Tasks 1–8, 10; §2 presets → Task 4 (+11); §3 manifest → Tasks 5–6 (+11); §4 diagnostics → Task 7 (+11); §5 LS layers → Tasks 8–10; §6 docs/licensing/gate/release → Tasks 12–13; §7 error table → Tasks 5, 6, 10; §8 testing → every task + Task 14. Weights rule (§3 "Weights") → Tasks 2, 3, 5, 11.
- **Placeholders:** none; every step carries code or exact commands. Task 13 lists required section content rather than prose because the prose is the deliverable the implementer writes.
- **Type consistency:** `OptiScalerPackage` fields match between Tasks 1, 2, 5; `LocalWeights(Path, Size, Sha256)` used identically in Tasks 3, 5, 10; `RemoveResult(Removed, Preserved, RemainingManagedFiles, ManifestRetained)` reused as defined in `DirectGameInstallerService`; `RuntimeChangeResult(LiveAcknowledged, Message)` unchanged; `HotkeySet`/`SwapperHotkey` names match Tasks 9–10; Python manifest keys match the C# `JsonPropertyName`s.
