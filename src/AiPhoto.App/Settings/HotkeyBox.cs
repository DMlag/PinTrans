using System.Windows.Controls;
using System.Windows.Input;
using AiPhoto.Core.Hotkeys;

namespace AiPhoto.App.Settings;

/// <summary>A text box that records a keyboard shortcut when focused. Backspace/Delete clears it.</summary>
public sealed class HotkeyBox : TextBox
{
    private HotkeyGesture _gesture = HotkeyGesture.None;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = "点击后按下新的快捷键组合；Backspace 清除";
        // With a Chinese/Japanese IME active, letter keys would arrive as Key.ImeProcessed and never be recorded.
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    /// <summary>
    /// Raised with true when a hotkey box starts recording (gets keyboard focus) and false when it stops.
    /// Registered global hotkeys are delivered as WM_HOTKEY instead of key presses, so the app host must
    /// suspend them while recording; otherwise pressing the current capture hotkey here starts a capture.
    /// </summary>
    public static event Action<bool>? RecordingChanged;

    public HotkeyGesture Gesture
    {
        get => _gesture;
        set
        {
            _gesture = value;
            Text = value.IsEmpty ? "（未设置）" : value.ToString();
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        RecordingChanged?.Invoke(true);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        // Windows never sends WM_KEYDOWN for Print Screen, only WM_KEYUP: record it here.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Snapshot)
        {
            e.Handled = true;
            Gesture = new HotkeyGesture(CurrentModifiers(), key.ToString());
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            Gesture = HotkeyGesture.None;
            return;
        }
        if (key is Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = false; // keep keyboard navigation
            return;
        }
        // Wait for a non-modifier key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            Text = FormatModifiers(Keyboard.Modifiers) + "…";
            return;
        }

        Gesture = new HotkeyGesture(CurrentModifiers(), key.ToString());
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        return mods;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Gesture = _gesture; // restore text if only modifiers were pressed
        RecordingChanged?.Invoke(false);
    }

    private static string FormatModifiers(ModifierKeys m)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts.Count == 0 ? "" : string.Join('+', parts) + "+";
    }
}
