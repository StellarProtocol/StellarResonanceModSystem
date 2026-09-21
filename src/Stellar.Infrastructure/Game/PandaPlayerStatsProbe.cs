using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based <see cref="IPlayerStatsProbe"/>. Reuses every reflection
/// target the existing <see cref="PandaPlayerStateProbe"/> already resolved
/// (ZEntityMgr singleton, MainEntity property, TryGetAttr methods, EAttrType
/// enum) via the small <c>internal</c> accessor surface added in
/// <c>PandaPlayerStateProbe.Internal.cs</c>.
///
/// <para>
/// Per-attribute storage type is memoized: the first sample probes
/// <c>TryGetAttr&lt;long&gt;</c> first (most stats are long); on a miss it
/// retries with <c>TryGetAttr&lt;int&gt;</c> and locks the memo to the
/// winning T. Detection is via the <c>WithHit</c> bool out-parameter — the
/// game's wrong-T overload emits a <c>[Error : Unity] arr type err</c>
/// log line but does NOT throw, so a try/catch around the call would
/// never flip the memo. The hit-bool mirrors the pattern used by
/// <c>PandaPlayerStateProbe.Read.cs:TryReadInt</c>. After the first-frame
/// probe per ID the memo is locked, so subsequent 60Hz samples produce
/// zero log spam.
/// </para>
///
/// <para>
/// Enum-value boxing is also cached lazily — no need to materialize all 1289
/// EAttrType boxes if the user only tracks 10. <see cref="System.Enum.ToObject"/>
/// returns a boxed enum value even for ints that aren't defined members of
/// EAttrType; downstream <c>TryGetAttr</c> simply returns false for those IDs
/// and the result dict skips them.
/// </para>
///
/// <para>
/// The "this attribute cannot be read at all" decision does NOT live here — it is the pure
/// <see cref="AttrReadabilityMemo"/> in <c>Stellar.Application</c> (unit-pinned), shared with
/// <c>PlayerStatsService</c> so a plugin's <c>Subscribe</c> re-arms the probe and a logout
/// forgets every verdict. This probe only reports what each read did; the memo decides.
/// </para>
/// </summary>
internal sealed partial class PandaPlayerStatsProbe : IPlayerStatsProbe
{
    private readonly IPluginLog _log;
    private readonly PandaPlayerStateProbe _stateProbe;

    // Per-attribute-id memo of storage type. Defaults to long (most stats);
    // flips on the first `arr type err` for that ID.
    private readonly Dictionary<int, bool> _attrPrefersLong = new();

    // Shared with PlayerStatsService (constructed by the Host). Holds the IDs that neither
    // Int64 nor Int32 nor Float can read, so the per-tick loop skips them instead of emitting
    // a continuous `[Error : Unity] arr type err` flood. Its one hard rule: an all-miss pass
    // latches nothing (see AttrReadabilityMemo).
    private readonly AttrReadabilityMemo _attrMemo;

    // Reused per-tick scratch buffer of this pass's read outcomes. Owned by the tick thread;
    // handed to the memo read-only, never retained by it — so a steady-state sample allocates
    // nothing beyond the result dictionary.
    private readonly List<AttrReadOutcome> _passBuffer = new();

    // Per-attribute-id memo for IDs that read as float (TryGetAttr<float>) — e.g. cd-reduction 11760,
    // cd-acceleration 11960/11980, versatility%. Probed after Int64/Int32 miss; the float value is stored
    // (rounded) into the long-typed result so callers see the live per-10000 value.
    private readonly HashSet<int> _attrFloat = new();

    // Cached enum-value boxes keyed by EAttrType int. Built lazily.
    private readonly Dictionary<int, object?> _enumBoxByInt = new();

    private static readonly IReadOnlyDictionary<int, long> EmptyDict
        = new Dictionary<int, long>(0);

    public PandaPlayerStatsProbe(IPluginLog log, PandaPlayerStateProbe stateProbe, AttrReadabilityMemo attrMemo)
    {
        _log = log;
        _stateProbe = stateProbe;
        _attrMemo = attrMemo;
    }

    public bool TrySample(
        IReadOnlyCollection<int> subscribed,
        out IReadOnlyDictionary<int, long> values)
    {
        if (!_stateProbe.IsBootstrapped)
        {
            values = EmptyDict;
            return false;
        }

        var mgr = _stateProbe.GetSingletonInstance();
        if (mgr is null)
        {
            values = EmptyDict;
            return false;
        }

        // Same-tick handoff from the state probe (Host refreshes player-state
        // immediately before player-stats). Using this instead of the manager's raw
        // playerEnt_ means the mounted-blackout rescue applies here too — reading
        // playerEnt_ directly blanked every stat while mounted.
        var entity = _stateProbe.GetLocalPlayerEntity(mgr);
        if (entity is null)
        {
            values = EmptyDict;
            return false;
        }

        var result = new Dictionary<int, long>(subscribed.Count);
        _passBuffer.Clear();
        foreach (var id in subscribed)
        {
            SampleSingleAttribute(id, entity, result);
        }
        CommitPass(entity);

        values = result;
        return true;
    }

    /// <summary>
    /// Hands this tick's read outcomes to the shared <see cref="AttrReadabilityMemo"/>, together
    /// with an EXPLICIT sheet-readiness signal, and reports whatever it latched. The memo — not
    /// this probe — decides whether the pass is evidence at all.
    ///
    /// <para>Readiness is never inferred from the pass. The subscribed set is the union of what
    /// the installed plugins asked for, and a client may subscribe only ids the game does not
    /// publish (a CombatMeter-only client subscribes exactly 11760 + 11980): such a pass is
    /// all-miss forever, so "nothing read ⇒ sheet not ready" would keep those ids out of the memo
    /// and re-probe them — three reflective invokes each — every tick for the whole process
    /// (PR #88 review). <see cref="PandaPlayerStateProbe.IsAttrSheetPopulated"/> answers it from
    /// an always-present attribute on the same entity instead.</para>
    ///
    /// <para>That read is paid for ONLY while the pass is ambiguous — all-miss and holding a
    /// first-read probe. A pass that read something, or that can latch nothing, decides itself;
    /// once the absent ids are latched the pass is empty and nothing is read at all.</para>
    /// </summary>
    private void CommitPass(object entity)
    {
        var sheetReady = AttrReadabilityMemo.NeedsReadinessSignal(_passBuffer)
                         && _stateProbe.IsAttrSheetPopulated(entity);
        var pass = _attrMemo.Record(_passBuffer, sheetReady);
        var latched = pass.Latched;
        for (var i = 0; i < latched.Count; i++)
        {
            _log.Info($"[Stellar][PlayerStats] attr {latched[i]} unreadable as Int64/Int32/Float; skipping future samples");
        }
        LogMemoPass(pass);
    }

    /// <summary>
    /// Read one attribute ID into <paramref name="result"/>, managing the per-ID memo
    /// dictionaries (<see cref="_enumBoxByInt"/>, <see cref="_attrPrefersLong"/>,
    /// <see cref="_attrFloat"/>) and appending this read's outcome to
    /// <see cref="_passBuffer"/> for <see cref="CommitPass"/>. On the first call per ID the
    /// method probes <c>Int64</c> then <c>Int32</c> then <c>Float</c> and locks the winner;
    /// subsequent calls use the memo directly, producing no further Unity
    /// <c>arr type err</c> log lines for that ID.
    /// </summary>
    private void SampleSingleAttribute(int id, object entity, Dictionary<int, long> result)
    {
        if (!_enumBoxByInt.TryGetValue(id, out var enumBox))
        {
            enumBox = _stateProbe.BoxEnumValue(id);
            _enumBoxByInt[id] = enumBox;  // cache null too — don't retry
        }
        if (enumBox is null)
        {
            return;
        }

        if (_attrMemo.IsUnreadable(id))
        {
            // A previous pass with live reads found Int64, Int32 AND float all miss;
            // skip without re-probing to avoid 60Hz `arr type err` spam. Re-armed by
            // IPlayerStats.Subscribe (re-tick the stat) or by logout.
            return;
        }

        if (_attrFloat.Contains(id))
        {
            // Float-stored attr (memo locked): read live as float, store rounded.
            result[id] = (long)System.MathF.Round(
                _stateProbe.ReadAttrSingleWithHit(entity, enumBox, out var floatHit));
            _passBuffer.Add(AttrReadOutcome.Memoized(id, floatHit));
            return;
        }

        if (_attrPrefersLong.TryGetValue(id, out var prefersLong))
        {
            // Memo locked in: call the known-good T directly. The wrong-T
            // branch is never taken, so no `arr type err` log line fires.
            var v = prefersLong
                ? _stateProbe.ReadAttrInt64WithHit(entity, enumBox, out var hit)
                : _stateProbe.ReadAttrInt32WithHit(entity, enumBox, out hit);
            result[id] = v;
            // A miss here is NOT evidence the attribute is absent — the storage type is
            // already known good, so the sheet simply went quiet (e.g. mounted blackout).
            // Memoized() reports the hit as readiness evidence but never latches the miss.
            _passBuffer.Add(AttrReadOutcome.Memoized(id, hit));
            return;
        }

        // First read for this ID — probe type and lock memo, then store value.
        ProbeAndMemoizeAttrType(id, entity, enumBox, result);
    }

    /// <summary>
    /// First-read probe for an attribute whose storage type is not yet memoized.
    /// Tries <c>Int64</c> first (most stats), falls back to <c>Int32</c>, then
    /// <c>Float</c>. Records the outcome in <see cref="_passBuffer"/>; a total miss is a
    /// latch candidate, but only <see cref="CommitPass"/>/<see cref="AttrReadabilityMemo"/>
    /// decides whether this pass is allowed to condemn it. The first wrong-T call emits one
    /// Unity <c>arr type err</c> line; the memo lock prevents repetition on all subsequent
    /// 60Hz ticks for this ID.
    /// </summary>
    private void ProbeAndMemoizeAttrType(int id, object entity, object enumBox, Dictionary<int, long> result)
    {
        var longV = _stateProbe.ReadAttrInt64WithHit(entity, enumBox, out var longHit);
        if (longHit)
        {
            _attrPrefersLong[id] = true;
            result[id] = longV;
            _passBuffer.Add(AttrReadOutcome.Probed(id, read: true));
            return;
        }
        var intV = _stateProbe.ReadAttrInt32WithHit(entity, enumBox, out var intHit);
        if (intHit)
        {
            _attrPrefersLong[id] = false;
            result[id] = intV;
            _passBuffer.Add(AttrReadOutcome.Probed(id, read: true));
            return;
        }
        // Both int T's missed — try float (cd-reduction 11760, accel 11960/11980,
        // versatility%, etc. are float-stored). Store the rounded float into the
        // long result so callers see the live per-10000 value.
        var floatV = _stateProbe.ReadAttrSingleWithHit(entity, enumBox, out var floatHit);
        if (floatHit)
        {
            _attrFloat.Add(id);
            result[id] = (long)System.MathF.Round(floatV);
            _passBuffer.Add(AttrReadOutcome.Probed(id, read: true));
            return;
        }
        // None of Int64/Int32/Float read it. Offer it to the memo as a latch candidate —
        // committed only if some OTHER id in this same pass did read, which is what tells
        // "this attribute does not exist" apart from "the sheet is not populated yet".
        _passBuffer.Add(AttrReadOutcome.Probed(id, read: false));
    }
}
