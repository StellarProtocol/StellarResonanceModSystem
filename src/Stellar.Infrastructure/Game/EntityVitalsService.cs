using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

// Diagnostics live in EntityVitalsService.Diagnostics.cs (per-event gated on StellarDiagnostics —
// recon §6 grammar lines 4/6.4 "the acceptance test for the tap").

/// <summary>
/// Reads boss HP (blood percent + stage) by entity id via the SAME merged per-entity store the
/// game's own boss bar reads — <c>Panda.ZUi.BossBloodUtil.ConversionBloodLogicDataToViewData(ZEntity)</c>
/// — instead of the combat wire's lossy <c>AttrCollection</c> mirror. Mirrors
/// <see cref="EntityTransformsService"/>'s shape (lazily-resolved reflection handles, handle-presence
/// retry, main-thread only): <c>ZSingleton&lt;ZEntityMgr&gt;.Instance</c> → <c>IsEntityExist</c>/
/// <c>IsEntityActive</c> gate → <c>GetEntity(long)</c> → <c>BossBloodUtil.Conversion…</c>.
///
/// <para>Event-driven cache layered on top (no-polling doctrine, 2026-08-26 raid-bosshp-capture-design
/// § decision 2): <see cref="TrackForWatcher"/> best-effort-binds
/// <c>ZEntityMgr.BindEntityLuaAttrWatcher</c> per tracked id so a change marks it dirty; <see cref="Tick"/>
/// (driven by the framework's per-frame Update, Host wiring) re-reads only DIRTY ids and evicts ids the
/// manager no longer reports live for. <see cref="TryGetBlood"/> itself NEVER depends on the watcher
/// succeeding — it always attempts a direct guarded live read first (same reliability envelope as
/// <see cref="EntityTransformsService.TryGetTransform"/>) and falls back to the last cached value only
/// when the live read misses this exact frame (stale-but-known beats Unknown). The watcher/dirty-cache
/// machinery is therefore a performance/purity refinement, not a correctness dependency — which matters
/// because the exact IL2CPP delegate-marshaling behavior of <c>BindEntityLuaAttrWatcher</c> is
/// UNVERIFIABLE headless (no real game process in CI); see <c>docs/il2cpp-probing-safety.md</c>.</para>
/// </summary>
internal sealed partial class EntityVitalsService : IBossVitals
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic   = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private const string ManagerTypeName   = "Panda.ZGame.ZEntityMgr";
    private const string EntityTypeName    = "Panda.ZGame.ZEntity";
    private const string BloodUtilTypeName = "Panda.ZUi.BossBloodUtil";

    // Periodic liveness sweep interval — bounds _watcherTokens/_cache to entities the manager still
    // reports live, mirroring the idle-TTL pattern elsewhere (CombatService.IdleEntityTtlMs) but at a
    // much tighter interval appropriate for a handful of tracked bosses/elites.
    private const long SweepIntervalMs = 5_000;

    private readonly IGameTypeRegistry _typeRegistry;
    private readonly ICombatLookup     _combatLookup;
    private readonly IPluginLog        _log;

    // Cached reflection handles — resolved lazily, retried until the CORE set is non-null. Do NOT add a
    // permanent "resolved" bool; the guard is handle-presence so that a failed attempt (Panda.* not
    // loaded yet) retries on the next call (mirrors EntityTransformsService's I-1).
    private PropertyInfo? _mgrInstanceProperty;  // ZUtil.ZSingleton<ZEntityMgr>.Instance
    private MethodInfo?   _getEntityMethod;      // ZEntityMgr.GetEntity(long uuid) → ZEntity
    private MethodInfo?   _conversionMethod;     // BossBloodUtil.ConversionBloodLogicDataToViewData(ZEntity) → Nullable<BossBloodLogicData>
    private PropertyInfo? _isBossProperty;       // ZEntity.IsBoss → bool

    // _isEntityExistMethod/_isEntityActiveMethod are MANDATORY for any live read (I3 review fix — see
    // IsLive in EntityVitalsService.Reflection.cs): unresolved means the whole tap stays inert rather
    // than an ungated GetEntity. _bindWatcherMethod/_unbindWatcherMethod stay genuinely optional — their
    // absence only costs the push-driven cache refresh between TryGetBlood calls.
    private MethodInfo? _isEntityExistMethod;    // ZEntityMgr.IsEntityExist(long uuid) → bool
    private MethodInfo? _isEntityActiveMethod;   // ZEntityMgr.IsEntityActive(long uuid) → bool
    private MethodInfo? _bindWatcherMethod;      // ZEntityMgr.BindEntityLuaAttrWatcher(long, uint[], Action<ZEntity>) → uint
    private MethodInfo? _unbindWatcherMethod;    // ZEntityMgr.UnbindEntityLuaAttrWater(long, uint)

    // BossBloodLogicData field/property handles — resolved from the live result type on first success.
    private bool          _bloodFieldsResolved;
    private FieldInfo?    _percentField;
    private PropertyInfo? _percentProperty;
    private FieldInfo?    _stageField;
    private PropertyInfo? _stageProperty;

    private readonly Dictionary<long, (int Percent, int Stage)> _cache = new();
    private readonly HashSet<long> _dirty = new();
    private readonly Dictionary<long, uint> _watcherTokens = new();
    private readonly object _cacheLock = new();
    private long _lastSweepMs;

    public EntityVitalsService(IGameTypeRegistry typeRegistry, ICombatLookup combatLookup, IPluginLog log)
    {
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _combatLookup = combatLookup ?? throw new ArgumentNullException(nameof(combatLookup));
        _log          = log          ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc/>
    public bool TryGetBlood(EntityId id, out int percent, out int stage)
    {
        percent = 0;
        stage = 0;
        EnsureResolved();
        if (_mgrInstanceProperty is null || _getEntityMethod is null || _conversionMethod is null)
            return false;

        if (TryReadLive(id.Value, out var pct, out var stg))
        {
            SetCache(id.Value, pct, stg);
            // C3a review fix: only start watching AFTER a live read actually returned boss-blood data
            // (ConversionBloodLogicDataToViewData had a value) — TryGetBlood is called for every
            // sampled entity, players included, and the old unconditional TrackForWatcher() here bound
            // a watcher (and, via OnAnyAttrDirty, drove a per-tick reflected re-read) for every combat
            // entity, not just bosses. Gating on a successful boss-blood read keeps the watched/dirty
            // sets bounded to the handful of real bosses/elites a raid actually has.
            TrackForWatcher(id.Value);
            percent = pct;
            stage = stg;
            DiagNativeRead(id, pct, stg);
            return true;
        }
        // Live read missed THIS frame (culled / not resolvable) — fall back to the last-known cached
        // value (populated by a previous live read here, or by the watcher's dirty-drain in Tick).
        // Stale-but-known beats Unknown — the same principle as the L1 wire fix (decision 1).
        return TryGetCached(id.Value, out percent, out stage);
    }

    /// <inheritdoc/>
    public bool IsBoss(EntityId id)
    {
        EnsureResolved();
        if (_mgrInstanceProperty is null || _isBossProperty is null) return false;
        if (!IsLive(id.Value)) return false;
        var entity = ResolveEntity(id.Value);
        if (entity is null) return false;
        try { return _isBossProperty.GetValue(entity) is true; }
        catch { return false; }
    }

    /// <summary>
    /// Drains the dirty set (ids the watcher marked changed since the last drain) and periodically
    /// sweeps tracked ids the manager no longer reports live for. Called once per framework tick
    /// (Host wiring); cheap no-op when nothing is dirty and the sweep interval hasn't elapsed.
    /// </summary>
    public void Tick()
    {
        DrainDirty();
        SweepDeadWatched();
    }

    /// <summary>
    /// Drops all cached vitals + watcher bookkeeping — the scene-change/logout reset, mirroring where
    /// <c>CombatEntityTracker.Reset()</c>/<c>ResetEntities()</c> fires
    /// (<c>PandaCombatStubProbe.OnEnterScene</c>, <c>Wiring.Wire.cs</c>'s <c>OnLogout</c>). I1 review
    /// fix — bounds <see cref="_cache"/>/<see cref="_watcherTokens"/> to one scene's/session's
    /// lifetime and prevents a stale cached percent surviving onto a RECYCLED entity id in the next
    /// scene. Deliberately does NOT call <c>UnbindEntityLuaAttrWater</c> here: <c>OnEnterScene</c> can
    /// invoke this from the network receive thread during exactly the mass-teardown window
    /// <c>docs/il2cpp-probing-safety.md</c> warns is unsafe for a live IL2CPP call, and the native
    /// watcher tokens are being invalidated by the scene teardown anyway — an orphaned native binding
    /// is harmless: its trampoline still closes over the old uuid and can still call
    /// <c>MarkDirty</c> if it fires again, but that only re-adds the uuid to the (now-cleared)
    /// <see cref="_dirty"/> set — the next <see cref="Tick"/> just does one benign extra live re-read
    /// for that uuid via <c>TryReadLive</c> (itself liveness-gated, so a truly torn-down entity still
    /// answers false), not a crash or a leak.
    /// </summary>
    public void Reset()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _dirty.Clear();
            _watcherTokens.Clear();
            _trampolines.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // Cache
    // -------------------------------------------------------------------------

    private bool TryGetCached(long uuid, out int percent, out int stage)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(uuid, out var v))
            {
                percent = v.Percent;
                stage = v.Stage;
                return true;
            }
        }
        percent = 0;
        stage = 0;
        return false;
    }

    private void SetCache(long uuid, int percent, int stage)
    {
        lock (_cacheLock) { _cache[uuid] = (percent, stage); }
    }

    // C3c review fix: caps the per-tick MethodInfo.Invoke cost. Only the ids actually TAKEN this tick
    // are REMOVED from _dirty — that removal is what prevents starvation, not enumeration order: an id
    // left over past the budget simply stays in _dirty, so next tick's iteration set is the remainder
    // (plus anything newly dirtied since), naturally rotating through rather than the same first-N
    // winning forever. With C3a/C3b bounding the tracked set to real bosses/elites, the budget is a
    // defensive cap, not a steady-state limiter.
    private const int MaxDrainPerTick = 8;

    private void DrainDirty()
    {
        long[] batch;
        lock (_cacheLock)
        {
            if (_dirty.Count == 0) return;
            int take = Math.Min(_dirty.Count, MaxDrainPerTick);
            batch = new long[take];
            int i = 0;
            foreach (var uuid in _dirty)
            {
                if (i >= take) break;
                batch[i++] = uuid;
            }
            foreach (var uuid in batch) _dirty.Remove(uuid);
        }
        foreach (var uuid in batch)
        {
            if (TryReadLive(uuid, out var pct, out var stg)) SetCache(uuid, pct, stg);
        }
    }

    // I1 review fix: sweeps BOTH _watcherTokens and _cache — a live-read-succeeded-but-watcher-bind-
    // failed id lands in _cache with no watcher token, and was previously never swept, risking a stale
    // cached percent surviving a scene change onto a RECYCLED entity id (mob ids recycle; see
    // CombatEntityTracker's own notes on this). EntityVitalsService.Reset() also bounds this at every
    // scene-change/logout, but the 5s sweep is the mid-scene backstop for an entity that quietly
    // despawns without a matching AOI-disappear.
    private void SweepDeadWatched()
    {
        var now = Environment.TickCount64;
        if (now - _lastSweepMs < SweepIntervalMs) return;
        _lastSweepMs = now;
        if (_mgrInstanceProperty is null) return;

        long[] tracked;
        lock (_cacheLock)
        {
            var ids = new System.Collections.Generic.HashSet<long>(_watcherTokens.Keys);
            ids.UnionWith(_cache.Keys);
            tracked = new long[ids.Count];
            ids.CopyTo(tracked);
        }
        foreach (var uuid in tracked)
        {
            if (!IsLive(uuid)) Untrack(uuid);
        }
    }
}
