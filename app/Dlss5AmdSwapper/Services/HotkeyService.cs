using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Dlss5AmdSwapper.Services;

public enum SwapperHotkey { Toggle = 1, Decrease = 2, Increase = 3, CycleLayer = 4, LayerDecrease = 5, LayerIncrease = 6 }

public enum HotkeySet { DirectGame, LosslessLayers }

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private HwndSource? _source;
    private IntPtr _handle;
    private readonly Action<SwapperHotkey> _callback;
    private readonly List<int> _registered = [];

    public HotkeyService(Action<SwapperHotkey> callback) => _callback = callback;

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

    public void Dispose()
    {
        foreach (var id in _registered) UnregisterHotKey(_handle, id);
        _registered.Clear();
        _source?.RemoveHook(Hook);
        _source = null;
    }

    private void Register(SwapperHotkey hotkey, uint key)
    {
        if (!RegisterHotKey(_handle, (int)hotkey, ModControl | ModAlt | ModNoRepeat, key))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not register Ctrl+Alt+F{key - 0x70 + 1}.");
        _registered.Add((int)hotkey);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            handled = true;
            _callback((SwapperHotkey)wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
