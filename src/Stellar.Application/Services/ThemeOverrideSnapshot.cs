// src/Stellar.Application/Services/ThemeOverrideSnapshot.cs
using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>The Save / Discard baseline of the "Edit colours" dialog: every registered slot's override state for ONE
/// custom theme (its value, or "not overridden") at the moment the dialog opened or was last saved. Edits stay live and
/// write through to <see cref="IThemeOverrides"/> as before; this only remembers where they started, counts how many slots
/// differ, and puts the differing ones back on Discard. Pure (no Unity), so the diff/restore rules are unit-tested.
/// <para>Works on the ACTIVE theme through <see cref="IThemeOverrides"/> (the only theme it can write), so the caller
/// restores only while <see cref="Theme"/> is still active.</para></summary>
internal sealed class ThemeOverrideSnapshot
{
    // key → the override value at capture, or null = the slot was NOT overridden (theme/base-preset default).
    private readonly Dictionary<string, ColorRgba?> _state = new(StringComparer.Ordinal);

    private ThemeOverrideSnapshot(string theme) => Theme = theme;

    /// <summary>The custom theme the baseline belongs to. Settable: a rename keeps the same overrides under a new name.</summary>
    public string Theme { get; set; }

    /// <summary>Records the current override state of every slot in <paramref name="slots"/>.</summary>
    public static ThemeOverrideSnapshot Capture(string theme, IThemeOverrides overrides, IReadOnlyList<ColorSlotInfo> slots)
    {
        var snap = new ThemeOverrideSnapshot(theme);
        for (var i = 0; i < slots.Count; i++) snap._state[slots[i].Key] = Current(overrides, slots[i].Key);
        return snap;
    }

    /// <summary>How many slots differ from the baseline (override added or removed, or the value changed at 8-bit
    /// precision — a picker dragged back to the same 0–255 value is not a change). A slot registered AFTER the capture
    /// (a late plugin) is adopted at its current state, so a stored override it brings in never counts as an edit and
    /// Discard never clears it.</summary>
    public int CountChanges(IThemeOverrides overrides, IReadOnlyList<ColorSlotInfo> slots)
    {
        var n = 0;
        for (var i = 0; i < slots.Count; i++)
            if (Differs(overrides, slots[i].Key, out _)) n++;
        return n;
    }

    /// <summary>Puts every differing slot back exactly (re-set to the snapshot value, or cleared when it was not
    /// overridden) and returns how many it touched. Does NOT flush or re-skin — the caller does both once.</summary>
    public int Restore(IThemeOverrides overrides, IReadOnlyList<ColorSlotInfo> slots)
    {
        var n = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            var key = slots[i].Key;
            if (!Differs(overrides, key, out var then)) continue;
            if (then is { } v) overrides.SetOverride(key, v);
            else overrides.ClearOverride(key);
            n++;
        }
        return n;
    }

    private bool Differs(IThemeOverrides overrides, string key, out ColorRgba? then)
    {
        var now = Current(overrides, key);
        if (!_state.TryGetValue(key, out then)) { _state[key] = now; then = now; return false; }   // adopt a late slot
        return !Same(then, now);
    }

    private static ColorRgba? Current(IThemeOverrides overrides, string key)
        => overrides.HasOverride(key) ? overrides.Resolve(key) : null;

    /// <summary>Equal when both are "not overridden", or both overridden with the same 8-bit RGBA.</summary>
    internal static bool Same(ColorRgba? a, ColorRgba? b)
    {
        if (a is not { } x || b is not { } y) return a.HasValue == b.HasValue;
        return Byte(x.R) == Byte(y.R) && Byte(x.G) == Byte(y.G) && Byte(x.B) == Byte(y.B) && Byte(x.A) == Byte(y.A);
    }

    private static int Byte(float f) => Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
}
