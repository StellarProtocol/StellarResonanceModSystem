using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Buff-first spec resolution behind <c>ICombatSpec</c> (spec-from-talent-buffs, owner go 2026-09-26; lifecycle
/// fixes from the same-day review round).
/// <list type="bullet">
/// <item>A spec ROOT buff (<c>SourceKind == 6</c> Talent, on a non-monster) makes that spec authoritative; cast
/// inference (kept by <see cref="CombatEntityTracker"/>) cannot overwrite it.</item>
/// <item>Removing the root starts a gap: the previous spec is held (no flicker, no 0) until a new root arrives,
/// for at most <see cref="GapTimeoutMs"/>; after that the talent spec is dropped.</item>
/// <item>A full buff snapshot (appear seed) replaces the talent state: with a root it sets it, without one it
/// clears it.</item>
/// <item>Disappear: the spec stops being authoritative (<see cref="TryGetTalentSpec"/> false) but the last value
/// keeps being reported until a seed or the scene reset.</item>
/// <item>If the class (attr 220) is a playable class that differs from the talent spec's class, the talent spec
/// is not reported: the cast-derived spec is used when it belongs to the new class, else 0. Battle Imagine
/// transform ids (8/14/15…) are not classes (owner ruling 2026-09-25) and impose no constraint.</item>
/// <item>No root ever seen → the cast-derived spec, unchanged.</item>
/// </list>
/// Change reporting: mutations mark the entity dirty; <see cref="TakeDirty"/> + <see cref="Publish"/> (main-thread
/// drain, skipped while a packet is mid-ingest) re-resolve and report only real value changes. A scene
/// <see cref="Reset"/> bumps a generation so a batch taken before it is dropped, not published into the new scene.
/// </summary>
internal sealed class TalentSpecResolver
{
    /// <summary><c>AttrProfessionId</c> — the entity's class.</summary>
    internal const int AttrProfessionId = 220;

    /// <summary>How long a removed root's spec is held waiting for the next root (the measured swap gap is ~2 s).</summary>
    internal const long GapTimeoutMs = 10_000;

    private const int TalentSourceKind = 6;   // EFightSource.Talent
    private static readonly HashSet<int> PlayableClasses = new() { 1, 2, 3, 4, 5, 9, 11, 12, 13 };

    private struct TalentState
    {
        public int Spec;
        public long GapStartMs;   // 0 = root present
        public bool Departed;     // left AOI — not authoritative any more
    }

    private readonly SpecRootBuffMap _roots;
    private readonly CombatEntityTracker _entities;
    private readonly object _lock = new();
    private readonly Dictionary<EntityId, TalentState> _talent = new();
    private readonly HashSet<EntityId> _inGap = new();
    private readonly Dictionary<EntityId, int> _reported = new();
    private readonly Dictionary<EntityId, long> _dirty = new();
    private int _generation;

    public TalentSpecResolver(SpecRootBuffMap roots, CombatEntityTracker entities)
    {
        _roots = roots;
        _entities = entities;
    }

    /// <summary>Wall clock (Unix ms) for the gap timer; replaceable for deterministic tests.</summary>
    internal Func<long> Clock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A buff was added/refreshed on <paramref name="entityId"/>; a Talent-sourced root sets its spec.</summary>
    public void NoteBuff(EntityId entityId, int baseId, int sourceKind, long timestampMs)
    {
        if (!TryRootSpec(entityId, baseId, sourceKind, out var spec)) return;
        lock (_lock) SetTalent(entityId, spec, timestampMs);
    }

    /// <summary>A buff was removed; removing the current root starts the gap timer.</summary>
    public void NoteBuffRemoved(EntityId entityId, int baseId, int sourceKind)
    {
        if (!TryRootSpec(entityId, baseId, sourceKind, out var spec)) return;
        lock (_lock)
        {
            if (!_talent.TryGetValue(entityId, out var st) || st.Spec != spec || st.GapStartMs != 0) return;
            st.GapStartMs = Clock();
            _talent[entityId] = st;
            _inGap.Add(entityId);
        }
    }

    /// <summary>A FULL snapshot replaced the entity's buffs: its root (if any) is now the whole truth.</summary>
    public void NoteSeed(EntityId entityId, IReadOnlyList<ActiveBuff> buffs, long timestampMs)
    {
        int spec = 0;
        for (int i = 0; i < buffs.Count; i++)
            if (TryRootSpec(entityId, buffs[i].BaseId, buffs[i].SourceKind, out var s)) spec = s;
        lock (_lock)
        {
            if (spec != 0) SetTalent(entityId, spec, timestampMs);
            else if (_talent.Remove(entityId)) { _inGap.Remove(entityId); _dirty[entityId] = timestampMs; }
        }
    }

    /// <summary>The entity left AOI (or was idle-swept): its talent spec stops being authoritative.</summary>
    public void NoteDeparted(EntityId entityId, long timestampMs)
    {
        lock (_lock)
        {
            if (_talent.TryGetValue(entityId, out var st))
            {
                st.Departed = true;
                st.GapStartMs = 0;
                _talent[entityId] = st;
                _inGap.Remove(entityId);
                _dirty[entityId] = timestampMs;
            }
            else if (_reported.ContainsKey(entityId)) _dirty[entityId] = timestampMs;
        }
    }

    /// <summary>Something that feeds resolution changed (cast spec, class attr) — re-check on the next drain.</summary>
    public void MarkDirty(EntityId entityId, long timestampMs)
    {
        lock (_lock) _dirty[entityId] = timestampMs;
    }

    public int GetSubProfession(EntityId entityId) => Resolve(entityId).Spec;

    public bool TryGetTalentSpec(EntityId entityId, out int subProfessionId)
    {
        var (spec, fromTalent) = Resolve(entityId);
        subProfessionId = fromTalent ? spec : 0;
        return fromTalent;
    }

    /// <summary>Snapshot the dirty set (plus entities whose gap just expired) with the current generation.</summary>
    public SpecDirtyBatch TakeDirty()
    {
        lock (_lock)
        {
            ExpireGaps();
            if (_dirty.Count == 0) return new SpecDirtyBatch(Array.Empty<KeyValuePair<EntityId, long>>(), _generation);
            var entries = new KeyValuePair<EntityId, long>[_dirty.Count];
            ((ICollection<KeyValuePair<EntityId, long>>)_dirty).CopyTo(entries, 0);
            _dirty.Clear();
            return new SpecDirtyBatch(entries, _generation);
        }
    }

    /// <summary>Re-resolve a batch and return its real changes (null when none, or when a
    /// <see cref="Reset"/> happened since the batch was taken).</summary>
    public List<CombatEvent.SpecChanged>? Publish(SpecDirtyBatch batch)
    {
        List<CombatEvent.SpecChanged>? changes = null;
        foreach (var (entityId, ts) in batch.Entries)
        {
            var (spec, fromTalent) = Resolve(entityId);
            var outcome = TryRecordReported(entityId, spec, batch.Generation, out var old);
            if (outcome == RecordOutcome.Stale) return null;
            if (outcome == RecordOutcome.Unchanged) continue;
            (changes ??= new List<CombatEvent.SpecChanged>()).Add(
                new CombatEvent.SpecChanged(ts, entityId, old, spec, fromTalent));
        }
        return changes;
    }

    /// <summary>Scene change: every spec returns to unknown (no change events are raised for it).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _generation++;
            _talent.Clear();
            _inGap.Clear();
            _reported.Clear();
            _dirty.Clear();
        }
    }

    private enum RecordOutcome { Changed, Unchanged, Stale }

    private RecordOutcome TryRecordReported(EntityId entityId, int spec, int generation, out int old)
    {
        lock (_lock)
        {
            old = 0;
            if (generation != _generation) return RecordOutcome.Stale;
            _reported.TryGetValue(entityId, out old);
            if (old == spec) return RecordOutcome.Unchanged;
            if (spec == 0) _reported.Remove(entityId);
            else _reported[entityId] = spec;
            return RecordOutcome.Changed;
        }
    }

    // Caller holds _lock.
    private void SetTalent(EntityId entityId, int spec, long timestampMs)
    {
        _talent[entityId] = new TalentState { Spec = spec };
        _inGap.Remove(entityId);
        _dirty[entityId] = timestampMs;
    }

    // Caller holds _lock. Drops talent state whose root-removal gap outlived GapTimeoutMs.
    private void ExpireGaps()
    {
        if (_inGap.Count == 0) return;
        long now = Clock();
        List<EntityId>? expired = null;
        foreach (var id in _inGap)
            if (!_talent.TryGetValue(id, out var st) || now - st.GapStartMs > GapTimeoutMs) (expired ??= new()).Add(id);
        if (expired is null) return;
        foreach (var id in expired)
        {
            _inGap.Remove(id);
            _talent.Remove(id);
            _dirty[id] = now;
        }
    }

    private bool TryRootSpec(EntityId entityId, int baseId, int sourceKind, out int spec)
    {
        spec = 0;
        if (baseId == 0 || sourceKind != TalentSourceKind || entityId.IsMonster) return false;
        return _roots.TryGetSpec(baseId, out spec);
    }

    private (int Spec, bool FromTalent) Resolve(EntityId entityId)
    {
        bool hasTalent;
        TalentState st;
        lock (_lock) hasTalent = _talent.TryGetValue(entityId, out st);
        if (hasTalent && st.GapStartMs != 0 && Clock() - st.GapStartMs > GapTimeoutMs) hasTalent = false;
        int cast = _entities.GetSubProfession(entityId);
        if (!hasTalent) return (cast, false);

        int cls = PlayableClass(entityId);
        if (cls == 0 || SpecRootBuffs.ProfessionOf(st.Spec) == cls) return (st.Spec, !st.Departed);
        return cast != 0 && SpecRootBuffs.ProfessionOf(cast) == cls ? (cast, false) : (0, false);
    }

    private int PlayableClass(EntityId entityId)
    {
        long cls = _entities.GetAttribute(entityId, AttrProfessionId);
        return cls > 0 && cls < int.MaxValue && PlayableClasses.Contains((int)cls) ? (int)cls : 0;
    }
}

/// <summary>A dirty-entity snapshot taken by <see cref="TalentSpecResolver.TakeDirty"/>, stamped with the reset
/// generation it belongs to.</summary>
internal readonly record struct SpecDirtyBatch(KeyValuePair<EntityId, long>[] Entries, int Generation);
