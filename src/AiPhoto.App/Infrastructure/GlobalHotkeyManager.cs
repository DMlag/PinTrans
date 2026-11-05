using System.Windows.Input;
using System.Windows.Interop;
using AiPhoto.Core.Hotkeys;
using static AiPhoto.App.Native.NativeMethods;

namespace AiPhoto.App.Infrastructure;

/// <summary>Registers system-wide hotkeys (RegisterHotKey) on a hidden message-only window.</summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = [];
    private int _nextId = 0x1000;

    public GlobalHotkeyManager()
    {
        var parameters = new HwndSourceParameters("AiPhotoHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: message-only window
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a hotkey. Returns false if the gesture is invalid or already taken by another program.</summary>
    public bool Register(HotkeyGesture gesture, Action handler)
    {
        if (gesture.IsEmpty) return true;
        if (!TryGetVirtualKey(gesture.Key, out var vk)) return false;

        var mods = MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= MOD_ALT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= MOD_CONTROL;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= MOD_WIN;

        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, mods, vk)) return false;
        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys) UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
    }

    public static bool TryGetVirtualKey(string keyName, out uint vk)
    {
        vk = 0;
        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out var key) || key == Key.None) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            handler();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
