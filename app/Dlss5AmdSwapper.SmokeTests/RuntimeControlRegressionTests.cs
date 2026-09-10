using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

internal static class RuntimeControlRegressionTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Concurrent runtime adjustments preserve every change", async () =>
        {
            using var fixture = new Fixture();
            await File.WriteAllTextAsync(fixture.Game.ConfigPath, "[Other]\nKeep=42\n[DlssNrOnAmd]\nLocalStructure=0.0\n");
            await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
                await new RuntimeControlService().AdjustStructureAsync(new GameEntry { ExePath = fixture.Game.ExePath }, 0.1))));
            var ini = IniDocument.Load(fixture.Game.ConfigPath);
            Check(Math.Abs(ini.GetDouble("DlssNrOnAmd", "LocalStructure", -1) - 1.0) < 0.001, "Concurrent adjustments were lost.");
            Check(ini.Get("Other", "Keep") == "42", "Unrelated configuration changed.");
        });
        await run("Removed runtime config clears stale controls", () =>
        {
            using var fixture = new Fixture();
            fixture.Game.Enabled = fixture.Game.LiveAcknowledged = fixture.Game.Installed = true;
            fixture.Game.LocalStructure = fixture.Game.LocalTone = fixture.Game.SkinStructure = 2;
            new RuntimeControlService().Refresh(fixture.Game, false);
            Check(!fixture.Game.Enabled && !fixture.Game.LiveAcknowledged && !fixture.Game.Installed, "Removed config retained active state.");
            Check(fixture.Game.LocalStructure == 1 && fixture.Game.LocalTone == 1 && fixture.Game.SkinStructure == 1, "Removed config retained slider values.");
            Check(fixture.Game.RuntimeStatus == "Not installed", "Removed config retained running status.");
            return Task.CompletedTask;
        });
        await run("Generic reload log does not prove a requested value was applied", async () =>
        {
            using var fixture = new Fixture();
            await File.WriteAllTextAsync(fixture.Game.ConfigPath, "[DlssNrOnAmd]\nEnabled=1\n");
            await File.WriteAllTextAsync(fixture.Game.LogPath, "settings change\nini changed\n");
            fixture.Game.LiveAcknowledged = true;
            var result = await new RuntimeControlService().SetEnabledAsync(fixture.Game, false);
            Check(!result.LiveAcknowledged && !fixture.Game.LiveAcknowledged, "Uncorrelated runtime log claimed live acknowledgment.");
            Check(!IniDocument.Load(fixture.Game.ConfigPath).GetBool("DlssNrOnAmd", "Enabled", true), "Requested value was not saved.");
        });
        await run("Cancelled runtime changes do not modify the file", async () =>
        {
            using var fixture = new Fixture();
            const string config = "[DlssNrOnAmd]\nEnabled=1\n";
            await File.WriteAllTextAsync(fixture.Game.ConfigPath, config);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            try { await new RuntimeControlService().SetEnabledAsync(fixture.Game, false, cancel.Token); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            Check(await File.ReadAllTextAsync(fixture.Game.ConfigPath) == config, "Cancelled change altered the file.");
        });
        await run("Process matching requires the executable path", () =>
        {
            var processPath = Environment.ProcessPath ?? throw new Exception("No process path.");
            Check(RuntimeControlService.IsRunning(processPath), "Current process was not recognized.");
            using var fixture = new Fixture();
            Check(!RuntimeControlService.IsRunning(Path.Combine(fixture.Path, System.IO.Path.GetFileName(processPath))), "A same-name executable in another directory matched.");
            return Task.CompletedTask;
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swapper-runtime-tests", Guid.NewGuid().ToString("N"));
        public GameEntry Game { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Path);
            Game = new GameEntry { ExePath = System.IO.Path.Combine(Path, "FixtureGame.exe") };
        }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
