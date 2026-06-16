using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Holds all per-entity mutable state for the combat session: vitals (HP),
/// DPS/HPS accumulators, team-id, and display names. Extracted from
/// <see cref="CombatService"/> to satisfy the Single Responsibility Principle
/// (C-11); <see cref="CombatService"/> delegates all entity-state reads/writes
/// here and keeps only the event pipeline (ring buffer, cooldowns, buff diffs,
/// server clock).
///
/// <para>Thread-safety: all public members are individually guarded by their
/// own dedicated locks (matching the original pattern in CombatService). The
/// class is designed for concurrent producer writes (network receive thread)
/// and main-thread reads — no cross-field consistency guarantee is provided,
/// consistent with the original design.</para>
/// </summary>
internal sealed class CombatEntityTracker
{
    // Per-entity HP cache fed by PandaCombatStubProbe's AttrCollection pass.
    private readonly Dictionary<EntityId, EntityVitals> _vitalsByEntity = new();
    private readonly object _vitalsByEntityLock = new();

    // Per-source DPS accumulators (5s sliding window encounter average).
    private readonly Dictionary<EntityId, DpsAccumulator> _dpsBySource = new();
    private readonly object _dpsBySourceLock = new();

    // Per-source HPS accumulators (parallel to DPS, IsHeal=true events only).
    private readonly Dictionary<EntityId, DpsAccumulator> _hpsBySource = new();
    private readonly object _hpsBySourceLock = new();

    // Per-entity team id from AttrTeamId (194) observations. 0 = unknown/solo.
    private readonly Dictionary<EntityId, long> _teamIdByEntity = new();
    private readonly object _teamIdByEntityLock = new();

    // Per-entity ability/combat score from AttrFightPoint (10030).
    private readonly Dictionary<EntityId, long> _fightPointByEntity = new();
    private readonly object _fightPointByEntityLock = new();

    // Per-entity equipped skill loadout from AttrSkillLevelIdList (116). Used to
    // surface Battle Imagines for all players in AOI.
    private readonly Dictionary<EntityId, IReadOnlyList<SkillLevel>> _skillsByEntity = new();
    private readonly object _skillsByEntityLock = new();

    // Per-entity full numeric attribute map from AttrCollection broadcasts.
    private readonly Dictionary<EntityId, Dictionary<int, long>> _attrsByEntity = new();
    private readonly object _attrsByEntityLock = new();

    // Per-entity equipment list from AttrEquipData (EquipNine) broadcasts.
    private readonly Dictionary<EntityId, IReadOnlyList<EquippedItem>> _equipByEntity = new();
    private readonly object _equipByEntityLock = new();

    // Per-entity worn-cosmetics list from AttrFashionData (201) broadcasts.
    private readonly Dictionary<EntityId, IReadOnlyList<FashionEntry>> _fashionByEntity = new();
    private readonly object _fashionByEntityLock = new();

    // Display-name cache (AttrName, EAttrType=1).
    private readonly EntityNameRegistry _names = new();

    // ── Read surface ────────────────────────────────────────────────────────

    public EntityVitals GetVitals(EntityId entityId)
    {
        lock (_vitalsByEntityLock)
        {
            return _vitalsByEntity.TryGetValue(entityId, out var v) ? v : EntityVitals.Unknown;
        }
    }

    public long GetLiveDps(EntityId sourceId)
    {
        lock (_dpsBySourceLock)
        {
            return _dpsBySource.TryGetValue(sourceId, out var acc) ? acc.Live : 0;
        }
    }

    public long GetLiveHps(EntityId sourceId)
    {
        lock (_hpsBySourceLock)
        {
            return _hpsBySource.TryGetValue(sourceId, out var acc) ? acc.Live : 0;
        }
    }

    public long GetTeamId(EntityId entityId)
    {
        lock (_teamIdByEntityLock)
        {
            return _teamIdByEntity.TryGetValue(entityId, out var t) ? t : 0;
        }
    }

    public long GetFightPoint(EntityId entityId)
    {
        lock (_fightPointByEntityLock)
        {
            return _fightPointByEntity.TryGetValue(entityId, out var fp) ? fp : 0;
        }
    }

    public IReadOnlyList<SkillLevel> GetSkillLevels(EntityId entityId)
    {
        lock (_skillsByEntityLock)
        {
            return _skillsByEntity.TryGetValue(entityId, out var v) ? v : System.Array.Empty<SkillLevel>();
        }
    }

    public string? GetEntityName(EntityId entityId) => _names.Get(entityId);

    public IReadOnlyDictionary<int, long> GetAttributes(EntityId entityId)
    {
        lock (_attrsByEntityLock)
            return _attrsByEntity.TryGetValue(entityId, out var map)
                ? new Dictionary<int, long>(map)
                : new Dictionary<int, long>();
    }

    public IReadOnlyList<EquippedItem> GetEquipment(EntityId entityId)
    {
        lock (_equipByEntityLock)
            return _equipByEntity.TryGetValue(entityId, out var v)
                ? v
                : System.Array.Empty<EquippedItem>();
    }

    public IReadOnlyList<FashionEntry> GetFashion(EntityId entityId)
    {
        lock (_fashionByEntityLock)
            return _fashionByEntity.TryGetValue(entityId, out var v)
                ? v
                : System.Array.Empty<FashionEntry>();
    }

    // ── Write surface ───────────────────────────────────────────────────────

    public void UpdateEntityVitals(EntityId entityId, long hp, long maxHp)
    {
        // -1 sentinel = "no update for this side this tick" — see ICombatEventSink.
        lock (_vitalsByEntityLock)
        {
            _vitalsByEntity.TryGetValue(entityId, out var cur);
            var newHp    = hp    >= 0 ? hp    : cur.Hp;
            var newMaxHp = maxHp >= 0 ? maxHp : cur.MaxHp;
            _vitalsByEntity[entityId] = new EntityVitals(newHp, newMaxHp, IsKnown: true);
        }
    }

    public void AccumulateDps(EntityId sourceId, long timestampMs, long amount)
    {
        lock (_dpsBySourceLock)
        {
            if (!_dpsBySource.TryGetValue(sourceId, out var acc))
                _dpsBySource[sourceId] = acc = new DpsAccumulator();
            acc.RecordDamage(timestampMs, amount);
        }
    }

    public void AccumulateHps(EntityId sourceId, long timestampMs, long amount)
    {
        lock (_hpsBySourceLock)
        {
            if (!_hpsBySource.TryGetValue(sourceId, out var acc))
                _hpsBySource[sourceId] = acc = new DpsAccumulator();
            acc.RecordDamage(timestampMs, amount);
        }
    }

    public void UpdateEntityTeamId(EntityId entityId, long teamId)
    {
        lock (_teamIdByEntityLock)
        {
            _teamIdByEntity[entityId] = teamId;
        }
    }

    public void UpdateEntityFightPoint(EntityId entityId, long fightPoint)
    {
        lock (_fightPointByEntityLock)
        {
            _fightPointByEntity[entityId] = fightPoint;
        }
    }

    public void UpdateEntitySkillLevels(EntityId entityId, IReadOnlyList<SkillLevel> skills)
    {
        if (skills is null || skills.Count == 0) return;
        lock (_skillsByEntityLock)
        {
            _skillsByEntity[entityId] = skills;
        }
    }

    public void UpdateEntityName(EntityId entityId, string name) => _names.Set(entityId, name);

    public void SetEntityAttribute(EntityId entityId, int attrId, long value)
    {
        lock (_attrsByEntityLock)
        {
            if (!_attrsByEntity.TryGetValue(entityId, out var map))
            {
                map = new Dictionary<int, long>();
                _attrsByEntity[entityId] = map;
            }
            map[attrId] = value;
        }
    }

    public void SetEntityEquipment(EntityId entityId, IReadOnlyList<EquipNineEntry> equip)
    {
        var copy = new List<EquippedItem>(equip.Count);
        foreach (var e in equip) copy.Add(new EquippedItem(e.Slot, e.ItemId));
        lock (_equipByEntityLock) _equipByEntity[entityId] = copy;
    }

    public void SetEntityFashion(EntityId entityId, IReadOnlyList<FashionEntry> fashion)
    {
        // Already a fully-decoded immutable snapshot from AttrFashionDataReader — store as-is.
        lock (_fashionByEntityLock) _fashionByEntity[entityId] = fashion;
    }

    public void OnEntityDisappeared(EntityId entityId)
    {
        lock (_vitalsByEntityLock)  _vitalsByEntity.Remove(entityId);
        lock (_dpsBySourceLock)     _dpsBySource.Remove(entityId);
        lock (_hpsBySourceLock)     _hpsBySource.Remove(entityId);
        lock (_teamIdByEntityLock)  _teamIdByEntity.Remove(entityId);
        // Fight-point rides the same per-entity AOI lifecycle as vitals/team-id (re-broadcast on
        // re-appear), so evict it too — it was omitted here, leaking one entry per entity ever seen.
        lock (_fightPointByEntityLock) _fightPointByEntity.Remove(entityId);
        // Evict the inspector caches on disappear — every mob/NPC/player that ever entered AOI flows through
        // here, so retaining these would grow unbounded for the process lifetime. Gear re-broadcasts on
        // re-appear (like vitals), so dropping it is safe and the inspector shows "no data" once out of AOI.
        lock (_attrsByEntityLock)   _attrsByEntity.Remove(entityId);
        lock (_equipByEntityLock)   _equipByEntity.Remove(entityId);
        lock (_fashionByEntityLock) _fashionByEntity.Remove(entityId);
        _names.Evict(entityId);
        // NOTE: equipped skill loadout (_skillsByEntity) is NOT evicted on AOI-disappear. It is static
        // loadout data, not transient AOI state — a player walking out of range shouldn't blank their
        // Imagines. It's only overwritten when fresh AttrSkillLevelIdList data arrives for that entity.
    }

    /// <summary>
    /// Drop ALL per-entity state — the scene-change reset. AOI-disappear (<see cref="OnEntityDisappeared"/>)
    /// is the per-entity eviction hook, but it only fires for entities the server enumerates in a
    /// SyncNearEntities disappear list. Dungeon/combat mobs are frequently touched only via damage packets
    /// (which create _dpsBySource/_hpsBySource/vitals rows) and never get a matching disappear, so their
    /// accumulators would otherwise survive for the whole process — piling up across every dungeon re-entry
    /// and driving steadily rising GC cost / falling FPS. A scene transition is a hard session boundary: the
    /// new scene re-broadcasts self (EnterScene) and everyone else (SyncNearEntities appears), so clearing
    /// everything here — including the otherwise-retained skill loadouts — is safe and bounds the tracker to
    /// a single scene's lifetime.
    /// </summary>
    public void Reset()
    {
        lock (_vitalsByEntityLock)     _vitalsByEntity.Clear();
        lock (_dpsBySourceLock)        _dpsBySource.Clear();
        lock (_hpsBySourceLock)        _hpsBySource.Clear();
        lock (_teamIdByEntityLock)     _teamIdByEntity.Clear();
        lock (_fightPointByEntityLock) _fightPointByEntity.Clear();
        lock (_skillsByEntityLock)     _skillsByEntity.Clear();
        lock (_attrsByEntityLock)      _attrsByEntity.Clear();
        lock (_equipByEntityLock)      _equipByEntity.Clear();
        lock (_fashionByEntityLock)    _fashionByEntity.Clear();
        _names.Clear();
    }
}
