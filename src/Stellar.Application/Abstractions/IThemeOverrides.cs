using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>Editor-facing view of the active theme's colours: enumerate slots,
/// read the resolved value, and set/clear a per-theme override. Overrides only
/// apply when a custom theme is active (built-ins are read-only).
/// AT THE STELLAR0005 8-member cap (Slots, SlotCount, Revision, Resolve, HasOverride, SetOverride,
/// ClearOverride, Flush) — split this interface rather than adding a 9th member.</summary>
internal interface IThemeOverrides
{
    IReadOnlyList<ColorSlotInfo> Slots { get; }

    /// <summary>Number of registered slots. Cheap (no allocation) — use this to
    /// detect slot-set changes per frame instead of materialising <see cref="Slots"/>.</summary>
    int SlotCount { get; }

    /// <summary>Bumped on every slot register/unregister/relabel. A cache keyed on <see cref="SlotCount"/>
    /// alone misses a label-only change (a Relabel never changes the count) — key the cache on this too.</summary>
    int Revision { get; }

    ColorRgba Resolve(string slotKey);
    bool HasOverride(string slotKey);
    void SetOverride(string slotKey, ColorRgba value);
    void ClearOverride(string slotKey);

    /// <summary>Persist pending override edits to config. The editor calls this
    /// on mouse-release so a slider drag writes once, not per frame.</summary>
    void Flush();
}
