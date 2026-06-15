namespace Stellar.Abstractions.Domain;

/// <summary>A keyboard shortcut: a primary key plus zero or more modifier keys.</summary>
/// <param name="Key">The primary key for this binding.</param>
/// <param name="Modifiers">Modifier keys that must also be held (Shift / Ctrl / Alt).</param>
public readonly record struct KeyBinding(StellarKeyCode Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    /// <summary>"Shift+F12", "Ctrl+Alt+P", "F11" — for display in the Settings UI.</summary>
    public override string ToString()
    {
        if (Modifiers == ModifierKeys.None) return Key.ToString();
        var parts = new System.Collections.Generic.List<string>(4);
        if ((Modifiers & ModifierKeys.Ctrl)  != 0) parts.Add("Ctrl");
        if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Modifiers & ModifierKeys.Alt)   != 0) parts.Add("Alt");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}
