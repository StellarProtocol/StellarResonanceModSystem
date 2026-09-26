using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Buff-first spec resolution behind <c>ICombatSpec</c> (spec-from-talent-buffs, owner go 2026-09-26).
/// <list type="bullet">
/// <item>A spec ROOT buff seen on an entity makes that spec authoritative; cast inference (kept by
/// <see cref="CombatEntityTracker"/>) cannot overwrite it.</item>
/// <item>The root being removed does not clear it — the ~2 s same-class swap gap holds the previous spec
/// until the new root arrives (no flicker, no 0).</item>
/// <item>If the class (attr 220) is a playable class that differs from the talent spec's class, the talent
/// spec is not reported: the cast-derived spec is used when it belongs to the new class, else 0. Battle
/// Imagine transform ids (8/14/15…) are not classes (owner ruling 2026-09-25) and impose no constraint.</item>
/// <item>No root ever seen → the cast-derived spec, unchanged.</item>
/// </list>
/// Change reporting: every mutation that can move the resolved value marks the entity dirty;
/// <see cref="CollectChanges"/> (main-thread drain) re-resolves dirty entities and reports only real value
/// changes, so a whole wire packet's net effect yields one change.
/// </summary>
internal sealed class TalentSpecResolver
{
    /// <summary><c>AttrProfessionId</c> — the entity's class.</summary>
    internal const int AttrProfessionId = 220;

    private static readonly HashSet<int> PlayableClasses = new() { 1, 2, 3, 4, 5, 9, 11, 12, 13 };

    private readonly SpecRootBuffMap _roots;
    private readonly CombatEntityTracker _entities;
    private readonly object _lock = new();
    private readonly Dictionary<EntityId, int> _talentSpec = new();
    private readonly Dictionary<EntityId, int> _reported = new();
    private readonly Dictionary<EntityId, long> _dirty = new();

    public TalentSpecResolver(SpecRootBuffMap roots, CombatEntityTracker entities)
    {
        _roots = roots;
        _entities = entities;
    }

    /// <summary>Record a buff present on <paramref name="entityId"/>; a spec root buff sets its talent spec.</summary>
    public void NoteBuff(EntityId entityId, int baseId, long timestampMs)
    {
        if (baseId == 0 || !_roots.TryGetSpec(baseId, out var spec)) return;
        lock (_lock)
        {
            _talentSpec[entityId] = spec;
            _dirty[entityId] = timestampMs;
        }
    }

    /// <summary>Something that feeds resolution changed (cast spec, class attr) — re-check on the next drain.</summary>
    public void MarkDirty(EntityId entityId, long timestampMs)
    {
        lock (_lock) _dirty[entityId] = timestampMs;
    }

    /// <summary>As <see cref="MarkDirty"/>, but only for entities that already have spec state (avoids
    /// queueing every disappearing mob).</summary>
    public void MarkDirtyIfKnown(EntityId entityId, long timestampMs)
    {
        lock (_lock)
        {
            if (_talentSpec.ContainsKey(entityId) || _reported.ContainsKey(entityId)) _dirty[entityId] = timestampMs;
        }
    }

    public int GetSubProfession(EntityId entityId) => Resolve(entityId).Spec;

    public bool TryGetTalentSpec(EntityId entityId, out int subProfessionId)
    {
        var (spec, fromTalent) = Resolve(entityId);
        subProfessionId = fromTalent ? spec : 0;
        return fromTalent;
    }

    /// <summary>Re-resolve every dirty entity; returns the real changes (null when none).</summary>
    public List<CombatEvent.SpecChanged>? CollectChanges()
    {
        KeyValuePair<EntityId, long>[] dirty;
        lock (_lock)
        {
            if (_dirty.Count == 0) return null;
            dirty = new KeyValuePair<EntityId, long>[_dirty.Count];
            ((ICollection<KeyValuePair<EntityId, long>>)_dirty).CopyTo(dirty, 0);
            _dirty.Clear();
        }
        List<CombatEvent.SpecChanged>? changes = null;
        foreach (var (entityId, ts) in dirty)
        {
            var (spec, fromTalent) = Resolve(entityId);
            if (!TryRecordReported(entityId, spec, out var old)) continue;
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
            _talentSpec.Clear();
            _reported.Clear();
            _dirty.Clear();
        }
    }

    private bool TryRecordReported(EntityId entityId, int spec, out int old)
    {
        lock (_lock)
        {
            _reported.TryGetValue(entityId, out old);
            if (old == spec) return false;
            if (spec == 0) _reported.Remove(entityId);
            else _reported[entityId] = spec;
            return true;
        }
    }

    private (int Spec, bool FromTalent) Resolve(EntityId entityId)
    {
        bool hasTalent;
        int talent;
        lock (_lock) hasTalent = _talentSpec.TryGetValue(entityId, out talent);
        int cast = _entities.GetSubProfession(entityId);
        if (!hasTalent) return (cast, false);

        int cls = PlayableClass(entityId);
        if (cls == 0 || SpecRootBuffs.ProfessionOf(talent) == cls) return (talent, true);
        return cast != 0 && SpecRootBuffs.ProfessionOf(cast) == cls ? (cast, false) : (0, false);
    }

    private int PlayableClass(EntityId entityId)
    {
        long cls = _entities.GetAttribute(entityId, AttrProfessionId);
        return cls > 0 && cls < int.MaxValue && PlayableClasses.Contains((int)cls) ? (int)cls : 0;
    }
}
