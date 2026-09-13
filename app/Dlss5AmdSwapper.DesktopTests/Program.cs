using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Dlss5AmdSwapper.Models;
using Dlss5AmdSwapper.Services;

namespace Dlss5AmdSwapper.DesktopTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--scenario") return RunScenario(args[1]);
        foreach (var scenario in new[] { "fresh", "close", "tray", "installation", "background" })
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("--scenario");
            start.ArgumentList.Add(scenario);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15000))
            {
                child.Kill(entireProcessTree: true);
                Console.Error.WriteLine($"FAIL {scenario}: process survived application exit for 15 seconds.");
                return 1;
            }
            Console.Write(output.GetAwaiter().GetResult());
            Console.Error.Write(error.GetAwaiter().GetResult());
            if (child.ExitCode != 0) return child.ExitCode;
        }
        Console.WriteLine("PASS all five desktop processes exited without any input automation.");
        return 0;
    }

    private static int RunScenario(string scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Dlss5Swapper-DesktopTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var settings = new AppSettingsService(folder);
            if (scenario == "fresh")
                Assert(!settings.Load().MinimizeToTray, "Fresh installations should exit on close by default.");
            else
                settings.Save(new AppSettings { MinimizeToTray = scenario == "tray", RegisterHotkeys = false });
            var app = new App();
            app.InitializeComponent();
            var window = new MainWindow(settings, initializeBackgroundServices: false)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            app.MainWindow = window;
            Exception? failure = null;
            var closed = false;
            Window? helper = null;
            CancellationTokenSource? scan = null;
            window.Closed += (_, _) => closed = true;
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    Assert(app.ShutdownMode == ShutdownMode.OnMainWindowClose, "Main window does not own application lifetime.");
                    // A hidden helper window reproduced the old OnLastWindowClose
                    // lifetime trap without creating a visible blank clone.
                    helper = new Window { ShowActivated = false, ShowInTaskbar = false };
                    new WindowInteropHelper(helper).EnsureHandle();

                    if (scenario == "tray")
                    {
                        Assert(GetField<object?>(window, "_tray") is not null, "Tray initialization failed.");
                        window.Close();
                        Assert(!closed && !window.IsVisible, "Close did not keep a hidden tray window.");
                        window.RestoreFromTray(activate: false);
                        Assert(window.IsVisible && !closed, "Tray restore did not restore the same window.");
                        window.RequestExit();
                    }
                    else if (scenario == "installation")
                    {
                        SetField(window, "_installOperation", true);
                        window.RequestExit();
                        Assert(!closed && window.IsVisible, "Exit interrupted an installation.");
                        SetField(window, "_installOperation", false);
                        window.RequestExit();
                    }
                    else
                    {
                        if (scenario == "background")
                        {
                            scan = new CancellationTokenSource();
                            SetField(window, "_scanCancellation", scan);
                            GetField<DispatcherTimer>(window, "_runtimeTimer").Start();
                            GetField<DispatcherTimer>(window, "_toastTimer").Start();
                        }
                        window.Close();
                    }
                    Assert(closed, "Main window never closed.");
                    Assert(GetField<object?>(window, "_tray") is null, "Tray icon survived the accepted close.");
                    Assert(!GetField<DispatcherTimer>(window, "_runtimeTimer").IsEnabled, "Runtime polling survived close.");
                    Assert(!GetField<DispatcherTimer>(window, "_toastTimer").IsEnabled, "Toast timer survived close.");
                    if (scan is not null) Assert(scan.IsCancellationRequested, "Closing failed to cancel the library scan.");
                }
                catch (Exception exception)
                {
                    failure = exception;
                    app.Shutdown(1);
                }
            }));
            var exitCode = app.Run(window);
            scan?.Dispose();
            if (failure is not null) throw failure;
            Assert(exitCode == 0 && closed, "Application did not exit successfully.");
            Assert(app.Windows.Count == 0, "Application retained a window after shutdown.");
            Console.WriteLine($"PASS {scenario}: main window, tray and helper window closed; settings isolated.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {scenario}: {exception}");
            return 1;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static T GetField<T>(object target, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
