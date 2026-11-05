namespace AiPhoto.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A keyboard shortcut such as <c>Ctrl+Shift+F1</c>. <see cref="Key"/> uses WPF <c>System.Windows.Input.Key</c>
/// names (e.g. "F1", "A", "D1", "PrintScreen" / "Snapshot") so the UI layer can map it without ambiguity.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static readonly HotkeyGesture None = new(HotkeyModifiers.None, "");

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = None;
        if (string.IsNullOrWhiteSpace(text)) return true; // empty = disabled

        var mods = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Ctrl; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win" or "windows" or "meta": mods |= HotkeyModifiers.Win; break;
                default:
                    if (key is not null) return false; // two non-modifier keys
                    key = raw;
                    break;
            }
        }
        if (key is null) return false;
        gesture = new HotkeyGesture(mods, key);
        return true;
    }

    public static HotkeyGesture Parse(string? text)
        => TryParse(text, out var g) ? g : throw new FormatException($"Invalid hotkey: '{text}'");
}
