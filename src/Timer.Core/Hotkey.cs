namespace Timer.Core;

/// <summary>
/// A keyboard shortcut such as Ctrl+Alt+N, kept as plain data so it can be saved, shown and tested away from any window. A shortcut must hold
/// Ctrl, Alt or the Windows key, so that a letter on its own can never take over typing, and its key is a letter, a digit or F1 to F12.
/// </summary>
public sealed record HotkeySpec(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    public const int Control = 0x2, AltMod = 0x1, ShiftMod = 0x4, WinMod = 0x8;

    /// <summary>The Windows key code for the key, or null when the key is not one a shortcut may use.</summary>
    public static int? VirtualKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        key = key.ToUpperInvariant();
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' || key[0] is >= '0' and <= '9')) return key[0];
        if (key[0] == 'F' && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 12) return 0x70 + f - 1;
        return null;
    }

    public bool IsValid => (Ctrl || Alt || Win) && VirtualKey(Key) is not null;

    /// <summary>The modifier flags in the form Windows registers them.</summary>
    public int Modifiers => (Ctrl ? Control : 0) | (Alt ? AltMod : 0) | (Shift ? ShiftMod : 0) | (Win ? WinMod : 0);

    /// <summary>Written as Ctrl+Alt+N, always in the same order.</summary>
    public override string ToString() => string.Join('+', new[] { Ctrl ? "Ctrl" : null, Alt ? "Alt" : null, Shift ? "Shift" : null, Win ? "Win" : null, Key.ToUpperInvariant() }.Where(p => p is not null));

    /// <summary>Reads Ctrl+Alt+N (any case, any order of modifiers). Anything that is not a valid shortcut gives null, which means no shortcut.</summary>
    public static HotkeySpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool ctrl = false, alt = false, shift = false, win = false; string? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default: if (key is not null) return null; key = part.ToUpperInvariant(); break;
            }
        }
        var spec = new HotkeySpec(ctrl, alt, shift, win, key ?? "");
        return spec.IsValid ? spec : null;
    }
}
