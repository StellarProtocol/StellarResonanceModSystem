using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>Editor-facing view of the active theme's colours: enumerate slots,
/// read the resolved value, and set/clear a per-theme override. Overrides only
/// apply when a custom theme is active (built-ins are read-only).</summary>
internal interface IThemeOverrides
{
    IReadOnlyList<ColorSlotInfo> Slots { get; }

    /// <summary>Number of registered slots. Cheap (no allocation) — use this to
    /// detect slot-set changes per frame instead of materialising <see cref="Slots"/>.</summary>
    int SlotCount { get; }

    ColorRgba Resolve(string slotKey);
    bool HasOverride(string slotKey);
    void SetOverride(string slotKey, ColorRgba value);
    void ClearOverride(string slotKey);

    /// <summary>Persist pending override edits to config. The editor calls this
    /// on mouse-release so a slider drag writes once, not per frame.</summary>
    void Flush();
}
