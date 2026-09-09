using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Dlss5AmdSwapper.Services;

public enum SwapperHotkey { Toggle = 1, Decrease = 2, Increase = 3 }

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

    public void Attach(IntPtr handle)
    {
        _handle = handle;
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Could not attach to the app window.");
        _source.AddHook(Hook);
        Register(SwapperHotkey.Toggle, 0x75);   // F6
        Register(SwapperHotkey.Decrease, 0x76); // F7
        Register(SwapperHotkey.Increase, 0x77); // F8
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not register Ctrl+Alt+F{(int)hotkey + 5}.");
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
