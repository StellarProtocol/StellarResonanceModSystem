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

    /// <summary>The current live Deep-Slumber state, or null before the first parse that carries a
    /// "DSLV" row (bridge unresolved, or a stale in-flight read from before this enrichment shipped).</summary>
    public DeepSlumberState? Read() => _deepSlumberState;

    // Parses the DS rows out of the SAME raw dump ParseLoadoutData just decoded and latches the
    // result (mirrors how _loadouts/_currentId are replaced on every changed parse). Called once per
    // ParseLoadoutData pass — see PandaLoadoutProbe.cs.
    private void UpdateDeepSlumberState(string raw)
    {
        _deepSlumberState = ParseDeepSlumber(raw);
        LogDeepSlumberFirstRead(_deepSlumberState);   // no-op unless STELLAR_DIAGNOSTICS; non-latching on empty
    }
}
