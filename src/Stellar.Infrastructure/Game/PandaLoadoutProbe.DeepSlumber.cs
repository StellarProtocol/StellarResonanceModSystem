using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IDeepSlumberProbe"/> via the SAME Lua bridge + on-demand refresh chunk
/// <see cref="PandaLoadoutProbe"/> already drives for Role Plan data. Owner-verified gap
/// (2026-08-19): the C# reflection mirror (<c>PandaInventoryPullReader.ReadDeepSlumber</c>, still
/// wired on <see cref="PandaInventoryProbe"/> but no longer the Host-selected implementation)
/// populates <c>CharSerialize.SeasonCultivateLineData</c>/<c>SeasonRoleLevelData</c> LAZILY — a fresh
/// session that never opens the Psychoscope UI has EMPTY containers there, so an early archive
/// uploaded no Deep-Slumber block at all. This reads the LUA mirror instead: the "DSLV"/"DSA" rows
/// <c>PandaLoadoutProbe.Resolution.cs</c>'s refresh chunk appends to the SAME <c>_StellarLoadoutData</c>
/// global — populated at login, the same source the game's own season views read.
///
/// <para>No separate resolution/refresh path: <see cref="IsResolved"/> is the loadout bridge's own
/// <c>_bridgeResolved</c> (one Lua bridge, one resolution), and the state updates every
/// <c>ParseLoadoutData</c> pass — itself only re-run when the raw dump actually changes, and re-fired
/// by <see cref="PandaLoadoutProbe.OnGearChanged"/> (field-101/field-12 deltas), so an in-Psychoscope
/// edit refreshes this within one on-demand refresh window, no polling added.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe : IDeepSlumberProbe
{
    private DeepSlumberState? _deepSlumberState;
    private int? _lastDeepSlumberLineCount;
    private IReadOnlyList<string> _lastDeepSlumberErrors = Array.Empty<string>();

    /// <summary>The current live Deep-Slumber state, or null before the first parse that carries a
    /// "DSLV" row (bridge unresolved, or a stale in-flight read from before this enrichment shipped).</summary>
    public DeepSlumberState? Read() => _deepSlumberState;

    /// <summary>Tiny internal accessor for the raw "DSN"/"DSERR" diagnostic rows from the last refresh-
    /// chunk parse (Task: DS iteration fix, owner run sea/O1jJepsgKC, 2026-08-20) — diagnostics-only,
    /// never used for state-building. <c>LineCount</c> is null when no "DSN" row was present (an OLD
    /// dump predating this enrichment); <c>Errors</c> is empty when every pcall'd section succeeded.</summary>
    internal (int? LineCount, IReadOnlyList<string> Errors) LastDeepSlumberDiagnosticRows
        => (_lastDeepSlumberLineCount, _lastDeepSlumberErrors);

    // Parses the DS rows out of the SAME raw dump ParseLoadoutData just decoded and latches the
    // result (mirrors how _loadouts/_currentId are replaced on every changed parse). Called once per
    // ParseLoadoutData pass — see PandaLoadoutProbe.cs.
    private void UpdateDeepSlumberState(string raw)
    {
        _deepSlumberState = ParseDeepSlumber(raw);
        (_lastDeepSlumberLineCount, _lastDeepSlumberErrors) = ParseDeepSlumberDiagnosticRows(raw);
        // no-op unless STELLAR_DIAGNOSTICS; non-latching until the bridge has actually run once
        LogDeepSlumberFirstRead(_deepSlumberState, _lastDeepSlumberLineCount, _lastDeepSlumberErrors);
    }
}
