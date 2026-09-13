using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

internal static class OfficialRuntimeConfigTests
{
    private const string Common = "[DlssNrOnAmd]\nEnabled=1\nUseFsrInputs=1\nUseDepth=1\nTemporal=1\nInterop=1\n";
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Official 0.3 setup configuration validates its actual synchronous pre-upscale schema", () =>
        {
            using var fixture = new Fixture(Common + "Async=0\nPreUpscale=1\nPreHistory=0\nInlineWaitMs=200\n");
            var verified = DirectGameInstallerService.VerifyRichConfig(fixture.Game.ConfigPath, "v0.3.0");
            Check(verified.Count == 8 && verified["Async"] == 0 && verified["PreUpscale"] == 1 && verified["PreHistory"] == 0, "Modern mode settings were not verified.");
            Check(!verified.ContainsKey("Inline"), "Obsolete Inline was manufactured.");
            return Task.CompletedTask;
        });
        await run("Official modern validation rejects missing or disabled rich inputs and asynchronous mode", () =>
        {
            var valid = Common + "Async=0\nPreUpscale=1\nPreHistory=0\n";
            foreach (var invalid in new[] {
                valid.Replace("Async=0", "Async=1"), valid.Replace("Async=0\n", ""),
                valid.Replace("UseDepth=1", "UseDepth=0"), valid.Replace("UseFsrInputs=1", "UseFsrInputs=0"),
                valid.Replace("PreUpscale=1", "PreUpscale=0"), valid.Replace("PreHistory=0", "PreHistory=1"),
                valid.Replace("Enabled=1", "Enabled=0") })
            {
                using var fixture = new Fixture(invalid);
                try { DirectGameInstallerService.VerifyRichConfig(fixture.Game.ConfigPath, "v0.3.0"); throw new Exception("Broken rich path accepted."); }
                catch (InvalidOperationException error) { Check(error.Message.Contains("full AMD Neural Rendering path"), "Unexpected rejection."); }
            }
            return Task.CompletedTask;
        });
        await run("Legacy and unknown official versions retain Inline validation", () =>
        {
            foreach (var tag in new string?[] { "v0.2.17", "v0.2.18", null, "unknown" })
            {
                using var fixture = new Fixture(Common + "Inline=1\n");
                Check(DirectGameInstallerService.VerifyRichConfig(fixture.Game.ConfigPath, tag)["Inline"] == 1, "Legacy inline mode not verified.");
                File.WriteAllText(fixture.Game.ConfigPath, Common + "Async=0\nPreUpscale=1\nPreHistory=0\n");
                try { DirectGameInstallerService.VerifyRichConfig(fixture.Game.ConfigPath, tag); throw new Exception("Modern config bypassed an unknown/legacy schema."); }
                catch (InvalidOperationException error) { Check(error.Message.Contains("Inline=1"), "Legacy missing Inline not reported."); }
            }
            return Task.CompletedTask;
        });
        await run("Runtime mode controls write Async inversely for modern configs and Inline for legacy", () =>
        {
            using var modern = new Fixture(Common + "Async=1\nPreUpscale=1\nPreHistory=0\n[Other]\nKeep=yes\n");
            var service = new RuntimeControlService();
            service.SetInline(modern.Game, true);
            var ini = IniDocument.Load(modern.Game.ConfigPath);
            Check(ini.Get("DlssNrOnAmd", "Async") == "0" && ini.Get("DlssNrOnAmd", "Inline") is null, "Inline request did not select modern synchronous mode.");
            Check(ini.Get("Other", "Keep") == "yes", "Other settings were modified.");
            service.SetInline(modern.Game, false);
            Check(IniDocument.Load(modern.Game.ConfigPath).Get("DlssNrOnAmd", "Async") == "1", "Async request was not saved.");
            using var legacy = new Fixture(Common + "Inline=0\n");
            service.SetInline(legacy.Game, true);
            Check(IniDocument.Load(legacy.Game.ConfigPath).Get("DlssNrOnAmd", "Inline") == "1", "Legacy mode setter regressed.");
            return Task.CompletedTask;
        });
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "swapper-official-config", Guid.NewGuid().ToString("N"));
        public GameEntry Game { get; }
        public Fixture(string config)
        {
            Directory.CreateDirectory(root);
            Game = new GameEntry { ExePath = Path.Combine(root, "FixtureGame.exe") };
            File.WriteAllText(Game.ConfigPath, config);
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
