using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace DlssNrControl;

internal static class Program
{
    private const string SectionName = "DlssNrOnAmd";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(Options.HelpText);
                return 0;
            }

            if (options.SelfTest)
            {
                SelfTests.Run();
                Console.WriteLine("Self-tests passed.");
                return 0;
            }

            if (options.ListenerSmoke)
            {
                ListenerSmokeTests.Run();
                Console.WriteLine("Listener smoke test passed.");
                return 0;
            }

            if (options.ConfigPath is null)
            {
                Console.Error.WriteLine("Missing required --config <path>.");
                Console.Error.WriteLine(Options.HelpText);
                return 2;
            }

            var configPath = Path.GetFullPath(options.ConfigPath);
            var controller = new DlssNrController(configPath);

            if (options.Listen)
            {
                return RunListener(configPath, controller);
            }

            var operation = options.Command ?? Operation.Get;
            var status = controller.Apply(operation);
            Console.WriteLine(StatusFormatter.Format(status));
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunListener(string configPath, DlssNrController controller)
    {
        using var singleInstance = SingleInstanceLock.TryAcquire(configPath);
        if (singleInstance is null)
        {
            Console.Error.WriteLine("DlssNrControl is already listening for this config path.");
            return 3;
        }

        ApplicationConfiguration.Initialize();
        using var context = new HotkeyApplicationContext(controller);
        Application.Run(context);
        return 0;
    }
}

internal enum Operation
{
    Get,
    On,
    Off,
    Toggle,
    Increase,
    Decrease
}

internal sealed class Options
{
    public string? ConfigPath { get; private init; }
    public Operation? Command { get; private init; }
    public bool Listen { get; private init; }
    public bool ShowHelp { get; private init; }
    public bool SelfTest { get; private init; }
    public bool ListenerSmoke { get; private init; }

    public const string HelpText =
        """
        DlssNrControl

        Required:
          --config <path>       INI file containing [DlssNrOnAmd].

        Commands:
          --get                 Print configured Enabled and LocalStructure values.
          --on                  Set Enabled=1.
          --off                 Set Enabled=0.
          --toggle              Toggle Enabled.
          --increase            Increase LocalStructure by 0.1, clamped to 2.0.
          --decrease            Decrease LocalStructure by 0.1, clamped to 0.0.
          --listen              Run tray helper and global Ctrl+Alt hotkeys.
          --self-test           Run dependency-free INI command tests using temp files.
          --listener-smoke      Run bounded native listener smoke test using a temp INI.

        Hotkeys while listening:
          Ctrl+Alt+F6           Toggle Enabled.
          Ctrl+Alt+F7           Decrease LocalStructure.
          Ctrl+Alt+F8           Increase LocalStructure.
        """;

    public static Options Parse(string[] args)
    {
        string? configPath = null;
        Operation? operation = null;
        var listen = false;
        var showHelp = false;
        var selfTest = false;
        var listenerSmoke = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--config":
                    if (++i >= args.Length)
                    {
                        throw new ArgumentException("--config requires a path.");
                    }
                    configPath = args[i];
                    break;
                case "--get":
                    operation = SetOperation(operation, Operation.Get);
                    break;
                case "--on":
                    operation = SetOperation(operation, Operation.On);
                    break;
                case "--off":
                    operation = SetOperation(operation, Operation.Off);
                    break;
                case "--toggle":
                    operation = SetOperation(operation, Operation.Toggle);
                    break;
                case "--increase":
                    operation = SetOperation(operation, Operation.Increase);
                    break;
                case "--decrease":
                    operation = SetOperation(operation, Operation.Decrease);
                    break;
                case "--listen":
                    listen = true;
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--listener-smoke":
                    listenerSmoke = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        if (listen && operation is not null)
        {
            throw new ArgumentException("--listen cannot be combined with a one-shot command.");
        }

        return new Options
        {
            ConfigPath = configPath,
            Command = operation,
            Listen = listen,
            ShowHelp = showHelp,
            SelfTest = selfTest,
            ListenerSmoke = listenerSmoke
        };
    }

    private static Operation SetOperation(Operation? current, Operation next)
    {
        if (current is not null)
        {
            throw new ArgumentException("Specify only one command.");
        }

        return next;
    }
}

internal sealed class DlssNrController(string configPath)
{
    private const decimal LocalStructureStep = 0.1m;
    private const decimal LocalStructureDefault = 1.0m;
    private const decimal LocalStructureMin = 0.0m;
    private const decimal LocalStructureMax = 2.0m;

    public DlssNrStatus Apply(Operation operation)
    {
        var document = IniDocument.Load(configPath);
        var section = document.GetOrAddSection("DlssNrOnAmd");

        var enabled = ParseBool(section.GetValue("Enabled")) ?? true;
        var localStructure = ParseDecimal(section.GetValue("LocalStructure")) ?? LocalStructureDefault;

        switch (operation)
        {
            case Operation.On:
                enabled = true;
                section.SetValue("Enabled", "1");
                document.SaveAtomic(configPath);
                break;
            case Operation.Off:
                enabled = false;
                section.SetValue("Enabled", "0");
                document.SaveAtomic(configPath);
                break;
            case Operation.Toggle:
                enabled = !enabled;
                section.SetValue("Enabled", enabled ? "1" : "0");
                document.SaveAtomic(configPath);
                break;
            case Operation.Increase:
                localStructure = Clamp(RoundOneDecimal(localStructure + LocalStructureStep));
                section.SetValue("LocalStructure", FormatDecimal(localStructure));
                document.SaveAtomic(configPath);
                break;
            case Operation.Decrease:
                localStructure = Clamp(RoundOneDecimal(localStructure - LocalStructureStep));
                section.SetValue("LocalStructure", FormatDecimal(localStructure));
                document.SaveAtomic(configPath);
                break;
            case Operation.Get:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }

        var refreshed = IniDocument.Load(configPath).GetOrAddSection("DlssNrOnAmd");
        return new DlssNrStatus(
            ParseBool(refreshed.GetValue("Enabled")) ?? true,
            Clamp(ParseDecimal(refreshed.GetValue("LocalStructure")) ?? LocalStructureDefault),
            configPath);
    }

    private static bool? ParseBool(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "enabled" => true,
            "0" or "false" or "no" or "off" or "disabled" => false,
            _ => null
        };
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static decimal Clamp(decimal value) => Math.Min(LocalStructureMax, Math.Max(LocalStructureMin, value));

    private static decimal RoundOneDecimal(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static string FormatDecimal(decimal value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}

internal sealed record DlssNrStatus(bool Enabled, decimal LocalStructure, string ConfigPath);

internal static class StatusFormatter
{
    public static string Format(DlssNrStatus status)
    {
        var enabled = status.Enabled ? "1" : "0";
        var local = status.LocalStructure.ToString("0.0", CultureInfo.InvariantCulture);
        return $"Configured Enabled={enabled}, LocalStructure={local}. Runtime neural rendering activity is not inferred by this helper.";
    }
}

internal sealed class HotkeyApplicationContext : ApplicationContext
{
    private readonly DlssNrController _controller;
    private readonly HotkeyWindow _window;
    private readonly NotifyIcon _notifyIcon;

    public HotkeyApplicationContext(DlssNrController controller, bool showReadyBalloon = true)
    {
        _controller = controller;
        _window = new HotkeyWindow(HandleHotkey);
        _notifyIcon = CreateNotifyIcon();

        try
        {
            _window.RegisterAll();
            if (showReadyBalloon)
            {
                ShowBalloon("DLSS NR controls ready");
            }
        }
        catch
        {
            _window.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _window.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private NotifyIcon CreateNotifyIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Status", null, (_, _) => ApplyAndNotify(Operation.Get));
        menu.Items.Add("On", null, (_, _) => ApplyAndNotify(Operation.On));
        menu.Items.Add("Off", null, (_, _) => ApplyAndNotify(Operation.Off));
        menu.Items.Add("Toggle", null, (_, _) => ApplyAndNotify(Operation.Toggle));
        menu.Items.Add("Increase effect", null, (_, _) => ApplyAndNotify(Operation.Increase));
        menu.Items.Add("Decrease effect", null, (_, _) => ApplyAndNotify(Operation.Decrease));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        return new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Text = "DLSS NR on AMD controls",
            Visible = true
        };
    }

    private void HandleHotkey(HotkeyCommand command)
    {
        var operation = command switch
        {
            HotkeyCommand.Toggle => Operation.Toggle,
            HotkeyCommand.Decrease => Operation.Decrease,
            HotkeyCommand.Increase => Operation.Increase,
            _ => Operation.Get
        };

        ApplyAndNotify(operation);
    }

    private void ApplyAndNotify(Operation operation)
    {
        try
        {
            ShowBalloon(StatusFormatter.Format(_controller.Apply(operation)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowBalloon($"Config update failed: {ex.Message}");
        }
    }

    private void ShowBalloon(string message)
    {
        _notifyIcon.BalloonTipTitle = "DLSS NR on AMD";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(1200);
    }

    public void PostHotkeyForSmoke(HotkeyCommand command)
    {
        _window.PostHotkeyForSmoke(command);
    }
}

internal enum HotkeyCommand
{
    Toggle = 1,
    Decrease = 2,
    Increase = 3
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private readonly Action<HotkeyCommand> _onHotkey;
    private readonly List<int> _registeredIds = [];

    public HotkeyWindow(Action<HotkeyCommand> onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams());
    }

    public void RegisterAll()
    {
        try
        {
            Register(HotkeyCommand.Toggle, Keys.F6);
            Register(HotkeyCommand.Decrease, Keys.F7);
            Register(HotkeyCommand.Increase, Keys.F8);
        }
        catch
        {
            UnregisterAll();
            throw;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            _onHotkey((HotkeyCommand)m.WParam.ToInt32());
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();

        DestroyHandle();
    }

    private void Register(HotkeyCommand command, Keys key)
    {
        var modifiers = ModControl | ModAlt | ModNoRepeat;
        if (!RegisterHotKey(Handle, (int)command, modifiers, (uint)key))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not register Ctrl+Alt+{key}.");
        }

        _registeredIds.Add((int)command);
    }

    private void UnregisterAll()
    {
        foreach (var id in _registeredIds)
        {
            UnregisterHotKey(Handle, id);
        }

        _registeredIds.Clear();
    }

    public void PostHotkeyForSmoke(HotkeyCommand command)
    {
        if (!PostMessage(Handle, WmHotkey, (nint)(int)command, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not post smoke hotkey {command}.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, nint wParam, nint lParam);
}

internal sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;

    private SingleInstanceLock(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static SingleInstanceLock? TryAcquire(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
        var mutex = new Mutex(initiallyOwned: false, $"Local\\DlssNrControl-{hash}");
        try
        {
            if (mutex.WaitOne(0))
            {
                return new SingleInstanceLock(mutex, ownsMutex: true);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceLock(mutex, ownsMutex: true);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}

internal sealed class IniDocument
{
    private readonly List<IniLine> _lines;

    private IniDocument(List<IniLine> lines)
    {
        _lines = lines;
    }

    public static IniDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            return new IniDocument([]);
        }

        var lines = File.ReadAllLines(path).Select(IniLine.Parse).ToList();
        return new IniDocument(lines);
    }

    public IniSection GetOrAddSection(string name)
    {
        var sectionIndex = FindSectionIndex(name);
        if (sectionIndex < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Raw.Length > 0)
            {
                _lines.Add(IniLine.RawLine(""));
            }

            _lines.Add(IniLine.Section(name));
            sectionIndex = _lines.Count - 1;
        }

        return new IniSection(_lines, sectionIndex);
    }

    public void SaveAtomic(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllLines(tempPath, _lines.Select(line => line.ToText()), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, path, overwrite: true);
    }

    private int FindSectionIndex(string name)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Kind == IniLineKind.Section &&
                string.Equals(_lines[i].SectionName, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

internal sealed class IniSection
{
    private readonly List<IniLine> _lines;
    private readonly int _sectionIndex;

    public IniSection(List<IniLine> lines, int sectionIndex)
    {
        _lines = lines;
        _sectionIndex = sectionIndex;
    }

    public string? GetValue(string key)
    {
        for (var i = _sectionIndex + 1; i < SectionEndIndex(); i++)
        {
            var line = _lines[i];
            if (line.Kind == IniLineKind.KeyValue &&
                string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return line.Value;
            }
        }

        return null;
    }

    public void SetValue(string key, string value)
    {
        var end = SectionEndIndex();
        for (var i = _sectionIndex + 1; i < end; i++)
        {
            var line = _lines[i];
            if (line.Kind == IniLineKind.KeyValue &&
                string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = line.WithValue(value);
                return;
            }
        }

        _lines.Insert(end, IniLine.KeyValue(key, value));
    }

    private int SectionEndIndex()
    {
        for (var i = _sectionIndex + 1; i < _lines.Count; i++)
        {
            if (_lines[i].Kind == IniLineKind.Section)
            {
                return i;
            }
        }

        return _lines.Count;
    }
}

internal enum IniLineKind
{
    Raw,
    Section,
    KeyValue
}

internal sealed record IniLine(IniLineKind Kind, string Raw, string? SectionName, string? Key, string? Value)
{
    public static IniLine Parse(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
            trimmed.EndsWith("]", StringComparison.Ordinal) &&
            trimmed.Length > 2)
        {
            return Section(trimmed[1..^1]);
        }

        var equalsIndex = raw.IndexOf('=');
        if (equalsIndex > 0)
        {
            var key = raw[..equalsIndex].Trim();
            if (key.Length > 0)
            {
                return new IniLine(IniLineKind.KeyValue, raw, null, key, raw[(equalsIndex + 1)..].Trim());
            }
        }

        return RawLine(raw);
    }

    public static IniLine RawLine(string raw) => new(IniLineKind.Raw, raw, null, null, null);

    public static IniLine Section(string name) => new(IniLineKind.Section, $"[{name}]", name, null, null);

    public static IniLine KeyValue(string key, string value) => new(IniLineKind.KeyValue, $"{key}={value}", null, key, value);

    public IniLine WithValue(string value)
    {
        if (Key is null)
        {
            throw new InvalidOperationException("Cannot set a value on a non-key INI line.");
        }

        return KeyValue(Key, value);
    }

    public string ToText() => Raw;
}

internal static class SelfTests
{
    public static void Run()
    {
        TestDefaultsAndGet();
        TestClamp();
        TestPreserveOtherSections();
        TestMalformedValues();
        TestKeyCommands();
    }

    private static void TestDefaultsAndGet()
    {
        using var temp = TempIni("[DlssNrOnAmd]\r\nUseFsrInputs=1\r\n");
        var status = new DlssNrController(temp.Path).Apply(Operation.Get);
        Assert(status.Enabled, "Missing Enabled should read as enabled.");
        Assert(status.LocalStructure == 1.0m, "Missing LocalStructure should read default 1.0.");
    }

    private static void TestClamp()
    {
        using var temp = TempIni("[DlssNrOnAmd]\r\nEnabled=1\r\nLocalStructure=2.0\r\n");
        new DlssNrController(temp.Path).Apply(Operation.Increase);
        Assert(File.ReadAllText(temp.Path).Contains("LocalStructure=2.0", StringComparison.Ordinal), "Increase should clamp at 2.0.");

        File.WriteAllText(temp.Path, "[DlssNrOnAmd]\r\nEnabled=1\r\nLocalStructure=0.0\r\n");
        new DlssNrController(temp.Path).Apply(Operation.Decrease);
        Assert(File.ReadAllText(temp.Path).Contains("LocalStructure=0.0", StringComparison.Ordinal), "Decrease should clamp at 0.0.");
    }

    private static void TestPreserveOtherSections()
    {
        var original = "[Other]\r\nAlpha=1\r\n\r\n[DlssNrOnAmd]\r\nUseFsrInputs=1\r\nEnabled=0\r\n\r\n[Later]\r\nBeta=2\r\n";
        using var temp = TempIni(original);
        new DlssNrController(temp.Path).Apply(Operation.On);
        var text = File.ReadAllText(temp.Path);
        Assert(text.Contains("[Other]\r\nAlpha=1", StringComparison.Ordinal), "Other section should be preserved.");
        Assert(text.Contains("[Later]\r\nBeta=2", StringComparison.Ordinal), "Later section should be preserved.");
        Assert(text.Contains("UseFsrInputs=1", StringComparison.Ordinal), "Unrelated key should be preserved.");
        Assert(text.Contains("Enabled=1", StringComparison.Ordinal), "Enabled should be updated.");
    }

    private static void TestMalformedValues()
    {
        using var temp = TempIni("[DlssNrOnAmd]\r\nEnabled=maybe\r\nLocalStructure=auto\r\n");
        var controller = new DlssNrController(temp.Path);
        var status = controller.Apply(Operation.Get);
        Assert(status.Enabled, "Malformed Enabled should read as enabled.");
        Assert(status.LocalStructure == 1.0m, "Malformed LocalStructure should read as default 1.0.");

        status = controller.Apply(Operation.Increase);
        Assert(status.LocalStructure == 1.1m, "Malformed LocalStructure should increase from default.");
    }

    private static void TestKeyCommands()
    {
        using var temp = TempIni("[DlssNrOnAmd]\r\nEnabled=0\r\nLocalStructure=1.0\r\n");
        var controller = new DlssNrController(temp.Path);

        Assert(controller.Apply(Operation.Toggle).Enabled, "Toggle should enable.");
        Assert(!controller.Apply(Operation.Toggle).Enabled, "Toggle should disable.");
        Assert(controller.Apply(Operation.On).Enabled, "On should enable.");
        Assert(!controller.Apply(Operation.Off).Enabled, "Off should disable.");
        Assert(controller.Apply(Operation.Increase).LocalStructure == 1.1m, "Increase should add 0.1.");
        Assert(controller.Apply(Operation.Decrease).LocalStructure == 1.0m, "Decrease should subtract 0.1.");

        using var minimal = TempIni("[DlssNrOnAmd]\r\nUseFsrInputs=1\r\n");
        new DlssNrController(minimal.Path).Apply(Operation.Toggle);
        Assert(File.ReadAllText(minimal.Path).Contains("Enabled=0", StringComparison.Ordinal), "First toggle on missing Enabled should write Enabled=0.");
    }

    private static TempFile TempIni(string contents)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"DlssNrControl-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new TempFile(path);
    }

    public static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}

internal static class ListenerSmokeTests
{
    public static void Run()
    {
        using var temp = TempIni("[DlssNrOnAmd]\r\nUseFsrInputs=1\r\n");
        var controller = new DlssNrController(temp.Path);

        ApplicationConfiguration.Initialize();
        using var singleInstance = SingleInstanceLock.TryAcquire(temp.Path);
        SelfTests.Assert(singleInstance is not null, "Smoke listener should acquire its first config mutex.");
        using var context = new HotkeyApplicationContext(controller, showReadyBalloon: false);

        AssertSecondListenerExitsWithSingleInstanceCode(temp.Path);
        PostSmokeHotkeysAndExit(context);

        var text = File.ReadAllText(temp.Path);
        SelfTests.Assert(text.Contains("Enabled=0", StringComparison.Ordinal), "Smoke Ctrl+Alt+F6 should toggle missing Enabled to 0.");
        SelfTests.Assert(text.Contains("LocalStructure=1.0", StringComparison.Ordinal), "Smoke Ctrl+Alt+F8 then F7 should persist LocalStructure back to 1.0.");
    }

    private static void AssertSecondListenerExitsWithSingleInstanceCode(string configPath)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = Assembly.GetExecutingAssembly().Location;
        }

        using var process = new Process();
        process.StartInfo.FileName = processPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.ArgumentList.Add("--config");
        process.StartInfo.ArgumentList.Add(configPath);
        process.StartInfo.ArgumentList.Add("--listen");
        process.Start();

        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Second listener did not exit during smoke test.");
        }

        SelfTests.Assert(process.ExitCode == 3, $"Second listener should exit 3, got {process.ExitCode}.");
    }

    private static void PostSmokeHotkeysAndExit(HotkeyApplicationContext context)
    {
        var step = 0;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            step++;
            switch (step)
            {
                case 1:
                    context.PostHotkeyForSmoke(HotkeyCommand.Toggle);
                    break;
                case 2:
                    context.PostHotkeyForSmoke(HotkeyCommand.Increase);
                    break;
                case 3:
                    context.PostHotkeyForSmoke(HotkeyCommand.Decrease);
                    break;
                default:
                    timer.Stop();
                    context.ExitThread();
                    break;
            }
        };
        timer.Start();
        Application.Run(context);
    }

    private static TempFile TempIni(string contents)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"DlssNrControlListenerSmoke-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new TempFile(path);
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
