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
/// <c>ParseLoadoutData</c> pass — itself only re-run when the raw dump actually changes. The refresh is
/// re-fired by <see cref="PandaLoadoutProbe.OnGearChanged"/>, which is the FIELD-AGNOSTIC container-merge
/// signal (<c>ContainerDirtyDeltaReader.IsMergeSignal</c> — a psychoscope edit's CharSerialize field 101
/// is covered, but so is every delta shape the old allowlist missed): merge → <c>_mergePending</c> →
/// <c>RefreshLiveStateIfArmed</c> arms <c>_refreshPending</c> → <c>RefreshIfDue</c> re-fires
/// <c>RefreshChunk</c> (cooldown-coalesced) → the dump's DSA rows change → <c>ParseLoadoutData</c> →
/// <see cref="UpdateDeepSlumberState"/>. So an in-Psychoscope edit refreshes this within one on-demand
/// refresh window, no polling added.</para>
///
/// <para><b>And it is REPORTED.</b> A change to the parsed state arms the same consumer event the live
/// rows arm (<c>ILoadout.LiveStateChanged</c>) — see <see cref="UpdateDeepSlumberState"/>. Before
/// 2026-08-23 the state refreshed silently, so a consumer that snapshots the player's setup on that
/// event never re-captured on a Deep-Slumber-only edit.</para>
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
        var previous = _deepSlumberState;
        _deepSlumberState = ParseDeepSlumber(raw);
        // ARM the SAME consumer event the live rows arm (ILoadout.LiveStateChanged) — never publish
        // directly. ONE event covers gear/modules/class/talents/imagines AND Deep-Slumber; the publish
        // happens in TryResolvePerClassDetails, which ParseLoadoutData re-arms (_resolvePending) on this
        // very changed parse, so it normally lands on the same tick — and when the resolve cannot run,
        // the change is delivered LATE, never dropped (see _liveStatePendingPublish's doc).
        //
        // WHY THIS EXISTS (owner staging run sea/dXkw1PSyOG, 2026-08-23): a Deep-Slumber factor was
        // UNEQUIPPED between two archives and RE-EQUIPPED after. The refresh chain re-read it correctly
        // (the panels updated), but nothing downstream was told — ApplyLiveRows only compares the LIVE
        // row + imagines — so the CombatMeter never re-captured and the run uploaded ONE setup for two
        // materially different builds. Owner ruling (CLAUDE.md, verbatim): "when any equipment change
        // such as module,talents,equipments,slumberdream etc., and use have a combat with that setup it
        // require plugin to take snapshot of it even class has no change."
        if (DeepSlumberStateDiffers(previous, _deepSlumberState)) _liveStatePendingPublish = true;
        (_lastDeepSlumberLineCount, _lastDeepSlumberErrors) = ParseDeepSlumberDiagnosticRows(raw);
        // no-op unless STELLAR_DIAGNOSTICS; non-latching until the bridge has actually run once
        LogDeepSlumberFirstRead(_deepSlumberState, _lastDeepSlumberLineCount, _lastDeepSlumberErrors);
    }

    /// <summary>Pure STRUCTURAL difference over everything the Deep-Slumber walk serves: season levels,
    /// every (lineId, subType) variant, and each of its areas' activation, score and three node maps.
    /// This is the change-event gate, so two properties are load-bearing and pinned
    /// (<c>PandaLoadoutProbeDeepSlumberChangeTests</c>):
    ///
    /// <para><b>Order-insensitive.</b> Every level of the DS walk iterates a zcontainer map with Lua
    /// <c>pairs</c>, whose order is unspecified — comparing sequences would raise a change on a
    /// re-serialization of the IDENTICAL state, and a spurious event makes every consumer re-snapshot the
    /// player's build on each container delta. Same rule <c>SameUuidMap</c> follows for the live rows.</para>
    ///
    /// <para><b>Null is NO-SIGNAL, never a change.</b> <c>ParseDeepSlumber</c> returns null when the dump
    /// carries no "DSLV" row at all (bridge not resolved, stale in-flight read) — that is "not read yet",
    /// not "the player cleared their psychoscope". Raising on null↔state would fire a phantom change at
    /// every login and after every failed read.</para></summary>
    internal static bool DeepSlumberStateDiffers(DeepSlumberState? a, DeepSlumberState? b)
    {
        if (ReferenceEquals(a, b)) return false;
        if (a is null || b is null) return false;   // no-signal in either direction
        return !SamePairMap(a.SeasonLevels, b.SeasonLevels) || !SameDeepSlumberLines(a.Lines, b.Lines);
    }

    private static bool SameDeepSlumberLines(IReadOnlyList<DeepSlumberLine> a, IReadOnlyList<DeepSlumberLine> b)
    {
        if (a.Count != b.Count) return false;
        // Keyed by (lineId, subType) — the game holds several subType variants under ONE lineId
        // (measured on the owner's character: lineId 2 and 3 each carry subTypes 800522 + 800523).
        var index = new Dictionary<long, DeepSlumberLine>(b.Count);
        foreach (var line in b) index[LineKey(line)] = line;
        foreach (var line in a)
        {
            if (!index.TryGetValue(LineKey(line), out var other)) return false;
            if (!SameDeepSlumberAreas(line.Areas, other.Areas)) return false;
        }
        return true;
    }

    private static long LineKey(DeepSlumberLine line) => ((long)line.LineId << 32) ^ (uint)line.SubType;

    private static bool SameDeepSlumberAreas(IReadOnlyList<DeepSlumberArea> a, IReadOnlyList<DeepSlumberArea> b)
    {
        if (a.Count != b.Count) return false;
        var index = new Dictionary<int, DeepSlumberArea>(b.Count);
        foreach (var area in b) index[area.AreaId] = area;
        foreach (var area in a)
        {
            if (!index.TryGetValue(area.AreaId, out var other)) return false;
            if (area.IsActive != other.IsActive || area.Score != other.Score) return false;
            if (!SamePairMap(area.BigNodes, other.BigNodes)) return false;
            if (!SamePairMap(area.MiddleNodes, other.MiddleNodes)) return false;
            if (!SamePairMap(area.NormalNodes, other.NormalNodes)) return false;
        }
        return true;
    }

    /// <summary>Order-insensitive [key, value] pair-list equality — the DS node maps and the season-level
    /// map are keyed containers serialized as pair lists, so only the key→value mapping is meaningful.
    /// A malformed (short) pair is ignored on BOTH sides rather than throwing, matching every other
    /// parser here.</summary>
    internal static bool SamePairMap(IReadOnlyList<int[]> a, IReadOnlyList<int[]> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        var index = new Dictionary<int, int>(b.Count);
        foreach (var pair in b) if (pair.Length >= 2) index[pair[0]] = pair[1];
        var matched = 0;
        foreach (var pair in a)
        {
            if (pair.Length < 2) continue;
            if (!index.TryGetValue(pair[0], out var value) || value != pair[1]) return false;
            matched++;
        }
        return matched == index.Count;
    }
}
