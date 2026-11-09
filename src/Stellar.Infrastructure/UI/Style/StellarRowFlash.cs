using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Stellar.Infrastructure.UI.Style;

/// <summary>
/// The R13 "done" flash on the row the player clicked: <see cref="Flash"/> marks a row (keyed by ITEM identity — a
/// plugin id, a hotkey action id — never by pool slot, so a re-render or scroll keeps the flash on the right row) for
/// <see cref="FlashSeconds"/>; the row's tile reads <see cref="IsOn"/> for its green wash + ring and shows
/// <see cref="Text"/> ("On", "Saved", …) in place of its trailing value. Pure state + a clock: no UI of its own.
/// </summary>
internal sealed class StellarRowFlash
{
    public const float FlashSeconds = 1f;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly Dictionary<string, (string Text, float Until)> _rows = new(StringComparer.Ordinal);
    private readonly Func<float> _now;

    /// <summary><paramref name="now"/> = a seconds clock (deltas only); default a process-wide stopwatch.</summary>
    public StellarRowFlash(Func<float>? now = null) => _now = now ?? (() => (float)Clock.Elapsed.TotalSeconds);

    /// <summary>Flashes the row <paramref name="key"/> with <paramref name="text"/> (a repeat restarts it).</summary>
    public void Flash(string key, string text)
    {
        if (string.IsNullOrEmpty(key)) return;
        _rows[key] = (text ?? "", _now() + FlashSeconds);
    }

    public bool IsOn(string? key) => key != null && _rows.TryGetValue(key, out var e) && _now() < e.Until;

    /// <summary>The flash label while <see cref="IsOn"/>, else "".</summary>
    public string Text(string? key) => key != null && _rows.TryGetValue(key, out var e) && _now() < e.Until ? e.Text : "";
}
