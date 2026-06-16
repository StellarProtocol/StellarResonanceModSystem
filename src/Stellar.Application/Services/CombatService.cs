using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Mixed-shape combat surface. Polled snapshots for cooldowns/buffs/server time,
/// queued event stream for skills/damage. Wire-thread producers (the probe)
/// call <see cref="EnqueueEvent"/> / the setters; the main thread drains the
/// queue via <see cref="Drain"/> once per <c>Game.Update</c> postfix and fans
/// out to subscribers there.
///
/// <para>Per-entity state (vitals, DPS/HPS, teamId, names) is delegated to
/// <see cref="CombatEntityTracker"/> (C-11 SRP split).</para>
/// </summary>
internal sealed partial class CombatService : ICombatSnapshot, ICombatLookup, ICombatEvents, ICombatEventSink, IEntityDetail
{
    private const int RingCapacity = 500;

    private readonly IPluginLog                   _log;
    private readonly CombatEntityTracker          _entities;
    private readonly SocialDataCache              _social;
    private readonly ConcurrentQueue<CombatEvent> _queue   = new();
    private readonly Queue<CombatEvent>           _ring    = new(RingCapacity);
    private readonly object                       _ringLock = new();

    private CombatEvent[] _ringSnapshot = Array.Empty<CombatEvent>();
    private int           _ringVersion;
    private int           _snapshotVersion;

    private Action<CombatEvent>[]? _handlers;
    private readonly object        _handlersLock = new();

    private EntityId                     _localEntityId  = EntityId.None;
    // Cooldowns arrive as DELTAS from SyncToMeDeltaInfo.SyncSkillCDs — each
    // message carries only the cooldowns that changed this tick. Storing them
    // keyed by SkillId so SetLocalCooldowns can merge (upsert) instead of
    // replacing wholesale; replacing dropped every other active cooldown each
    // tick and emptied the bar within ~1 frame after a skill press.
    private readonly Dictionary<int, SkillCooldown> _localCooldowns = new();
    private readonly object                         _localCooldownsLock = new();
    private SkillCooldown[]                         _cooldownsSnapshot  = Array.Empty<SkillCooldown>();
    private int                                     _cooldownsVersion;
    private int                                     _cooldownsSnapshotVersion = -1;
    private IReadOnlyList<ActiveBuff>    _localBuffs     = Array.Empty<ActiveBuff>();
    // Anchor + capture timestamp form the server-time interpolation pair.
    // SyncServerTime (WorldNtf method 43) fires only every ~5s, so reading
    // _serverNowMs directly would freeze the cooldown countdown for 5s at a
    // time. Instead we cache the server-time anchor PLUS the local
    // Environment.TickCount64 at the moment of capture; the ServerNowMs
    // accessor adds the elapsed local ticks since capture, producing a smooth
    // monotonic clock between anchor updates. The next SyncServerTime
    // overwrites both, snapping back to authoritative time.
    private long                         _serverNowMs;
    private long                         _serverTimeCapturedAtTicks;

    private readonly Dictionary<EntityId, Dictionary<int, ActiveBuff>> _buffsByEntity = new();
    private readonly object _buffsByEntityLock = new();

    // One-shot diagnostic: log the raw damage fields of the first N hits after
    // framework load so we can verify which field (Value vs HpLessenValue vs
    // LuckyValue) matches the floating damage number the user sees on-screen.
    // Unconditional (not gated on StellarDiagnostics) because it's bounded to
    // a fixed count and produces a tiny amount of log volume. Static so the
    // counter survives any (theoretical) service re-instantiation within a
    // process lifetime.
    private static int _firstHitsLogged;
    private const  int DiagFirstHits = 3;

    public CombatService(IPluginLog log, CombatEntityTracker entities, SocialDataCache social)
    {
        _log      = log      ?? throw new ArgumentNullException(nameof(log));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _social   = social   ?? throw new ArgumentNullException(nameof(social));
    }

    // --- ICombatSnapshot / ICombatLookup / ICombatEvents read surface ---

    public bool     IsAvailable    => !_localEntityId.IsNone;
    public EntityId LocalEntityId  => _localEntityId;

    /// <summary>
    /// Snapshot of active local cooldowns. Built fresh from
    /// <see cref="_localCooldowns"/> on each read; cheap because the active set
    /// is small (≤ ~30 skills). Also evicts expired entries (ended &gt; 1s ago)
    /// so the dictionary doesn't grow unbounded across short-lived skill ids
    /// — only when <see cref="_serverNowMs"/> has been set (otherwise we can't
    /// tell what's stale).
    /// </summary>
    public IReadOnlyList<SkillCooldown> LocalCooldowns
    {
        get
        {
            // Fast path: version unchanged since last build — return cached snapshot.
            // Mirror the RecentEvents pattern: volatile read outside the lock, double-
            // check inside.
            if (Volatile.Read(ref _cooldownsVersion) == _cooldownsSnapshotVersion)
                return _cooldownsSnapshot;

            lock (_localCooldownsLock)
            {
                if (_localCooldowns.Count == 0)
                {
                    _cooldownsSnapshot        = Array.Empty<SkillCooldown>();
                    _cooldownsSnapshotVersion = _cooldownsVersion;
                    return _cooldownsSnapshot;
                }

                // Use the interpolated ServerNowMs (not the raw anchor) so
                // eviction stays in lock-step with what CooldownBar.draw sees
                // — otherwise an entry could remain visible in the bar for
                // up to 5s after its true end time while it waits for the
                // next anchor refresh to trip the eviction threshold.
                long now = ServerNowMs;
                if (now > 0)
                {
                    List<int>? expired = null;
                    foreach (var kv in _localCooldowns)
                    {
                        var cd = kv.Value;
                        var remaining = (cd.BeginTimeMs + cd.DurationMs) - now;
                        if (remaining < -1000)
                        {
                            expired ??= new List<int>();
                            expired.Add(kv.Key);
                        }
                    }
                    if (expired != null)
                    {
                        foreach (var id in expired) _localCooldowns.Remove(id);
                        // Eviction mutated the dict — treat as a version bump so
                        // a subsequent read from another caller sees the pruned set.
                        _cooldownsVersion++;
                    }
                }

                // Rebuild and cache if the version (from external mutation or the
                // eviction above) differs from the last snapshot.
                var cur = _cooldownsVersion;
                if (cur != _cooldownsSnapshotVersion)
                {
                    _cooldownsSnapshot        = _localCooldowns.Values.ToArray();
                    _cooldownsSnapshotVersion = cur;
                }
                return _cooldownsSnapshot;
            }
        }
    }

    public IReadOnlyList<ActiveBuff>    LocalBuffs     => _localBuffs;

    /// <summary>
    /// Interpolated server clock. Returns the last anchor (set by
    /// <see cref="SetServerNowMs"/>) plus the local time elapsed since that
    /// anchor was captured. Returns 0 until the first anchor has been set —
    /// callers (e.g. <see cref="LocalCooldowns"/>) already treat 0 as "no
    /// server time yet" so the contract is preserved.
    /// </summary>
    public long ServerNowMs
    {
        get
        {
            var anchor = _serverNowMs;
            if (anchor == 0) return 0;
            var elapsedMs = Environment.TickCount64 - _serverTimeCapturedAtTicks;
            return anchor + elapsedMs;
        }
    }

    public IReadOnlyList<CombatEvent> RecentEvents
    {
        get
        {
            if (Volatile.Read(ref _ringVersion) == _snapshotVersion) return _ringSnapshot;
            lock (_ringLock)
            {
                var cur = _ringVersion;
                if (cur != _snapshotVersion)
                {
                    _ringSnapshot = _ring.ToArray();
                    _snapshotVersion = cur;
                }
                return _ringSnapshot;
            }
        }
    }

    public event Action<CombatEvent> CombatEventOccurred
    {
        add
        {
            if (value is null) return;
            lock (_handlersLock)
            {
                if (_handlers is null) { _handlers = new[] { value }; return; }
                var next = new Action<CombatEvent>[_handlers.Length + 1];
                Array.Copy(_handlers, next, _handlers.Length);
                next[_handlers.Length] = value;
                _handlers = next;
            }
        }
        remove
        {
            if (value is null) return;
            lock (_handlersLock)
            {
                if (_handlers is null) return;
                var idx = Array.IndexOf(_handlers, value);
                if (idx < 0) return;
                if (_handlers.Length == 1) { _handlers = null; return; }
                var next = new Action<CombatEvent>[_handlers.Length - 1];
                Array.Copy(_handlers, 0, next, 0, idx);
                Array.Copy(_handlers, idx + 1, next, idx, _handlers.Length - idx - 1);
                _handlers = next;
            }
        }
    }

    public IReadOnlyList<ActiveBuff> BuffsFor(EntityId entityId)
    {
        lock (_buffsByEntityLock)
        {
            return _buffsByEntity.TryGetValue(entityId, out var set) && set.Count > 0
                ? new List<ActiveBuff>(set.Values)
                : Array.Empty<ActiveBuff>();
        }
    }

    public string? GetEntityName(EntityId entityId) => _entities.GetEntityName(entityId);

    public EntityVitals GetVitals(EntityId entityId) => _entities.GetVitals(entityId);

    public long GetLiveDps(EntityId sourceId) => _entities.GetLiveDps(sourceId);

    public long GetLiveHps(EntityId sourceId) => _entities.GetLiveHps(sourceId);

    public long GetTeamId(EntityId entityId) => _entities.GetTeamId(entityId);

    public long GetFightPoint(EntityId entityId) => _entities.GetFightPoint(entityId);

    public IReadOnlyList<SkillLevel> GetSkillLevels(EntityId entityId) => _entities.GetSkillLevels(entityId);

    // --- IEntityDetail ---

    public IReadOnlyDictionary<int, long> GetAttributes(EntityId entity) => _entities.GetAttributes(entity);

    public IReadOnlyList<EquippedItem> GetEquipment(EntityId entity) => _entities.GetEquipment(entity);

    public IReadOnlyList<FashionEntry> GetFashion(EntityId entity) => _entities.GetFashion(entity);

    public SocialSnapshot? GetSocialSnapshot(EntityId entity) => _social.GetSocialSnapshot(entity);

    // --- ICombatEventSink (wire thread) ---

    public void EnqueueEvent(CombatEvent evt) => _queue.Enqueue(evt);

    public void SetLocalCooldowns(IReadOnlyList<SkillCooldown> cooldowns)
    {
        if (cooldowns is null || cooldowns.Count == 0) return;
        lock (_localCooldownsLock)
        {
            for (int i = 0; i < cooldowns.Count; i++)
            {
                var cd = cooldowns[i];
                // Upsert by SkillId. The server only sends rows that CHANGED
                // this tick — never the full active list — so a wholesale
                // replace would silently drop every other live cooldown.
                _localCooldowns[cd.SkillId] = cd;
            }
            // Bump version so the next LocalCooldowns read rebuilds the snapshot.
            _cooldownsVersion++;
        }
    }

    public void SetLocalEntityId(EntityId entityId)
    {
        if (_localEntityId.IsNone && !entityId.IsNone)
            _localEntityId = entityId;
    }

    /// <summary>
    /// Anchor the server clock. Captures both the supplied <paramref name="epochMs"/>
    /// and <see cref="Environment.TickCount64"/> at this moment, so the
    /// <see cref="ServerNowMs"/> accessor can interpolate between anchor
    /// updates (SyncServerTime fires only every ~5s).
    /// </summary>
    public void SetServerNowMs(long epochMs)
    {
        // Capture the local clock FIRST so a reader that catches us mid-write
        // sees, at worst, an anchor that's a hair behind reality — never one
        // that's a hair AHEAD. This keeps ServerNowMs monotonic in practice.
        _serverTimeCapturedAtTicks = Environment.TickCount64;
        _serverNowMs = epochMs;
    }

    public void OnEntityDisappeared(EntityId entityId)
    {
        lock (_buffsByEntityLock)
        {
            _buffsByEntity.Remove(entityId);
            if (entityId == _localEntityId) _localBuffs = Array.Empty<ActiveBuff>();
        }
        _entities.OnEntityDisappeared(entityId);
    }

    public void ResetEntities() => _entities.Reset();

    public void ClearAllBuffs()
    {
        lock (_buffsByEntityLock)
        {
            _buffsByEntity.Clear();
            _localBuffs = Array.Empty<ActiveBuff>();
        }
    }

    public void UpdateEntityName(EntityId entityId, string name) => _entities.UpdateEntityName(entityId, name);

    public void UpdateEntityVitals(EntityId entityId, long hp, long maxHp)
        => _entities.UpdateEntityVitals(entityId, hp, maxHp);

    public void UpdateEntityTeamId(EntityId entityId, long teamId)
        => _entities.UpdateEntityTeamId(entityId, teamId);

    public void UpdateEntityFightPoint(EntityId entityId, long fightPoint)
        => _entities.UpdateEntityFightPoint(entityId, fightPoint);

    public void UpdateEntitySkillLevels(EntityId entityId, IReadOnlyList<SkillLevel> skills)
        => _entities.UpdateEntitySkillLevels(entityId, skills);

    public void SetEntityAttribute(EntityId entityId, int attrId, long value)
        => _entities.SetEntityAttribute(entityId, attrId, value);

    public void SetEntityEquipment(EntityId entityId, IReadOnlyList<EquipNineEntry> equip)
        => _entities.SetEntityEquipment(entityId, equip);

    public void SetEntityFashion(EntityId entityId, IReadOnlyList<FashionEntry> fashion)
        => _entities.SetEntityFashion(entityId, fashion);

}
