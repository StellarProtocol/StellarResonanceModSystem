using System;
using System.Collections.Concurrent;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game.Protobuf;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// A fresh, in-staleness-window wire position read back from the cache.
/// <see cref="AgeMs"/> is the entry's age at the moment of the read.
/// </summary>
internal readonly record struct WirePositionSample(float X, float Y, float Z, float Yaw, bool HasYaw, long AgeMs);

/// <summary>
/// Last-known server-synced world position (<c>EAttrType.AttrPos=52</c>) + facing
/// (<c>AttrDir=50</c> / <c>Position.dir</c>) per entity uuid, fed from the AOI delta stream by
/// <see cref="PandaCombatStubProbe"/> and read on the main thread by <see cref="EntityTransformsService"/>
/// as a fallback when the rendered GameObject transform reads the ≈(0,0,0) sentinel during the
/// silent intra-scene teleport view-settle window (see <c>docs/recon/thanatos-walkin-geo.md</c> and
/// <c>docs/recon/logic-position-accessor.md</c>).
///
/// <para>Thread-safety: a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by uuid with an
/// immutable value struct. There is a single producer (the network receive thread runs the probe's
/// attr fan-out) and a single consumer (the main-thread framework tick); ConcurrentDictionary swaps
/// the whole node for a value-type update, so a main-thread read always sees a coherent entry —
/// never a torn struct. Matches the producer-writes / main-thread-reads pattern of the sibling
/// combat caches (<c>CombatEntityTracker</c>).</para>
///
/// <para>Bounding: cleared on scene change (<see cref="Clear"/>, driven from the probe's EnterScene
/// reset — the same lifecycle as <c>CombatEntityTracker.Reset</c>), evicted on AOI-disappear
/// (<see cref="Remove"/>), and hard-capped at <see cref="MaxEntities"/> (the oldest entry is evicted
/// on a new insert over the cap) so a pathological no-scene-change session cannot grow it without
/// bound. AOI only ever carries nearby entities, so steady state is far below the cap.</para>
/// </summary>
internal sealed class WireEntityPositions
{
    /// <summary>Hard cap on tracked entities; the oldest entry is evicted when a new insert exceeds it.</summary>
    internal const int MaxEntities = 1024;

    // (X/Y/Z folded into Position3D so the ctor stays within the analyzer's dependency cap.)
    private readonly record struct Entry(Position3D Pos, float Yaw, bool HasPos, bool HasYaw, long StampMs);

    private readonly ConcurrentDictionary<long, Entry> _byUuid = new();
    private readonly Func<long> _clock;

    /// <summary>Production ctor — clock is <see cref="Environment.TickCount64"/> (monotonic, cross-thread).</summary>
    public WireEntityPositions() : this(static () => Environment.TickCount64) { }

    /// <summary>Test ctor — inject a deterministic monotonic clock (milliseconds).</summary>
    internal WireEntityPositions(Func<long> clock) => _clock = clock;

    /// <summary>Current tracked-entity count (test/diagnostic surface).</summary>
    internal int Count => _byUuid.Count;

    /// <summary>
    /// Record a fresh position for <paramref name="uuid"/> from an <c>AttrPos(52)</c> read.
    /// Preserves the entity's existing yaw unless this position carried its own <c>dir</c>.
    /// </summary>
    public void OnPosition(long uuid, in WirePos pos)
    {
        var now = _clock();
        _byUuid.TryGetValue(uuid, out var old);
        _byUuid[uuid] = new Entry(
            new Position3D(pos.X, pos.Y, pos.Z),
            pos.HasDir ? pos.Dir : old.Yaw,
            HasPos: true,
            HasYaw: pos.HasDir || old.HasYaw,
            StampMs: now);
        PruneIfOverCap();
    }

    /// <summary>
    /// Record a facing for <paramref name="uuid"/> from an <c>AttrDir(50)</c> read. If no position
    /// has been seen yet this creates a yaw-only entry (<c>HasPos=false</c>) — never served as a
    /// position by <see cref="TryGetFresh"/>; it only carries the yaw for a later position tick.
    /// </summary>
    public void OnDir(long uuid, float yaw)
    {
        var existed = _byUuid.TryGetValue(uuid, out var old);
        // CRITICAL: preserve the POSITION's StampMs. Position freshness (the 5s staleness bound) may
        // ONLY ever be advanced by OnPosition — a facing-only AttrDir(50) delta must NOT re-freshen a
        // genuinely stale position, or a player whose AttrPos stream stopped would stay frozen at their
        // last cached position instead of dropping out cleanly at 5s. A brand-new yaw-only entry keeps
        // StampMs=0 (HasPos=false → never served as a position regardless).
        _byUuid[uuid] = new Entry(old.Pos, yaw, HasPos: old.HasPos, HasYaw: true, StampMs: old.StampMs);
        if (!existed) PruneIfOverCap();
    }

    /// <summary>
    /// Read the last-known position for <paramref name="uuid"/> when it carries a real position and is
    /// no more than <paramref name="maxStaleMs"/> old. Returns false when absent, position-less, or stale
    /// — so the caller keeps today's behaviour (never invents data).
    /// </summary>
    public bool TryGetFresh(long uuid, long maxStaleMs, out WirePositionSample sample)
    {
        sample = default;
        if (!_byUuid.TryGetValue(uuid, out var e) || !e.HasPos) return false;
        var age = _clock() - e.StampMs;
        if (age < 0 || age > maxStaleMs) return false;
        sample = new WirePositionSample(e.Pos.X, e.Pos.Y, e.Pos.Z, e.Yaw, e.HasYaw, age);
        return true;
    }

    /// <summary>Drop one entity (AOI-disappear).</summary>
    public void Remove(long uuid) => _byUuid.TryRemove(uuid, out _);

    /// <summary>Drop all entities (scene change).</summary>
    public void Clear() => _byUuid.Clear();

    // Evict the single oldest entry when a new insert pushes past the cap. Only ever scans when over
    // cap (rare — AOI is bounded and Clear() runs on every scene change), so the O(n) walk is cold.
    private void PruneIfOverCap()
    {
        if (_byUuid.Count <= MaxEntities) return;
        long oldestKey = 0, oldestMs = long.MaxValue;
        var found = false;
        foreach (var kv in _byUuid)
        {
            if (kv.Value.StampMs >= oldestMs) continue;
            oldestMs = kv.Value.StampMs;
            oldestKey = kv.Key;
            found = true;
        }
        if (found) _byUuid.TryRemove(oldestKey, out _);
    }
}
