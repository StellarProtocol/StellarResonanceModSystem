using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

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
/// </summary>
internal sealed class PandaPlayerStatsProbe : IPlayerStatsProbe
{
    private readonly IPluginLog _log;
    private readonly PandaPlayerStateProbe _stateProbe;

    // Per-attribute-id memo of storage type. Defaults to long (most stats);
    // flips on the first `arr type err` for that ID.
    private readonly Dictionary<int, bool> _attrPrefersLong = new();

    // Per-attribute-id memo for IDs that neither Int64 nor Int32 can read
    // (e.g. float-stored stats like AttrVersatilityPct). Without this, the
    // first-read fall-through retried both T's every 60Hz tick, generating
    // a continuous `[Error : Unity] arr type err` log spam. Memoizing on
    // first total miss bounds the error to one line per such attr ID.
    private readonly HashSet<int> _attrUnreadable = new();

    // Per-attribute-id memo for IDs that read as float (TryGetAttr<float>) — e.g. cd-reduction 11760,
    // cd-acceleration 11960/11980, versatility%. Probed after Int64/Int32 miss; the float value is stored
    // (rounded) into the long-typed result so callers see the live per-10000 value.
    private readonly HashSet<int> _attrFloat = new();

    // Cached enum-value boxes keyed by EAttrType int. Built lazily.
    private readonly Dictionary<int, object?> _enumBoxByInt = new();

    private static readonly IReadOnlyDictionary<int, long> EmptyDict
        = new Dictionary<int, long>(0);

    public PandaPlayerStatsProbe(IPluginLog log, PandaPlayerStateProbe stateProbe)
    {
        _log = log;
        _stateProbe = stateProbe;
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

        var entity = _stateProbe.GetMainEntity(mgr);
        if (entity is null)
        {
            values = EmptyDict;
            return false;
        }

        var result = new Dictionary<int, long>(subscribed.Count);
        foreach (var id in subscribed)
        {
            SampleSingleAttribute(id, entity, result);
        }

        values = result;
        return true;
    }

    /// <summary>
    /// Read one attribute ID into <paramref name="result"/>, managing the three
    /// per-ID memo dictionaries (<see cref="_enumBoxByInt"/>, <see cref="_attrPrefersLong"/>,
    /// <see cref="_attrUnreadable"/>). On the first call per ID the method probes
    /// <c>Int64</c> then <c>Int32</c> and locks the winner; subsequent calls use the
    /// memo directly, producing no further Unity <c>arr type err</c> log lines for
    /// that ID.
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

        if (_attrUnreadable.Contains(id))
        {
            // First-read pass already found Int64, Int32 AND float miss;
            // skip without re-probing to avoid 60Hz `arr type err` spam.
            return;
        }

        if (_attrFloat.Contains(id))
        {
            // Float-stored attr (memo locked): read live as float, store rounded.
            result[id] = (long)System.MathF.Round(_stateProbe.ReadAttrSingleWithHit(entity, enumBox, out _));
            return;
        }

        if (_attrPrefersLong.TryGetValue(id, out var prefersLong))
        {
            // Memo locked in: call the known-good T directly. The wrong-T
            // branch is never taken, so no `arr type err` log line fires.
            var v = prefersLong
                ? _stateProbe.ReadAttrInt64WithHit(entity, enumBox, out _)
                : _stateProbe.ReadAttrInt32WithHit(entity, enumBox, out _);
            result[id] = v;
            return;
        }

        // First read for this ID — probe type and lock memo, then store value.
        ProbeAndMemoizeAttrType(id, entity, enumBox, result);
    }

    /// <summary>
    /// First-read probe for an attribute whose storage type is not yet memoized.
    /// Tries <c>Int64</c> first (most stats), falls back to <c>Int32</c>; marks
    /// the ID unreadable when both miss. The first wrong-T call emits one Unity
    /// <c>arr type err</c> line; the memo lock prevents repetition on all
    /// subsequent 60Hz ticks for this ID.
    /// </summary>
    private void ProbeAndMemoizeAttrType(int id, object entity, object enumBox, Dictionary<int, long> result)
    {
        var longV = _stateProbe.ReadAttrInt64WithHit(entity, enumBox, out var longHit);
        if (longHit)
        {
            _attrPrefersLong[id] = true;
            result[id] = longV;
            return;
        }
        var intV = _stateProbe.ReadAttrInt32WithHit(entity, enumBox, out var intHit);
        if (intHit)
        {
            _attrPrefersLong[id] = false;
            result[id] = intV;
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
            return;
        }
        // None of Int64/Int32/Float read it — memo as unreadable so the next
        // tick's foreach skips it and no further `arr type err` lines fire.
        _attrUnreadable.Add(id);
        _log.Info($"[Stellar][PlayerStats] attr {id} unreadable as Int64/Int32/Float; skipping future samples");
    }
}
