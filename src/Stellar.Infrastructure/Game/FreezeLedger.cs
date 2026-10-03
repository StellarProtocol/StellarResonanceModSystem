using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>Who one freeze's position hold and deferred removals never touch. The time pause stops everyone, the local
/// player included (Photo Studio scene-stays spec, amendment 2026-10-02 late, superseding § 3 "your own character is never
/// frozen"); but the HOLD must never pin the local player (a server correction of their own position would fight it) nor the
/// mount they ride (review I1, regression <c>scene_stays_own_mount_never_frozen</c>), and the local player's removal is never
/// deferred. <see cref="Begin"/> names the player, <see cref="Exclude"/> adds the mount, the entity list drops them
/// (<see cref="WithoutSelf"/>), and every admission refuses them (regression <c>scene_stays_self_never_held</c>). Pure
/// (unit-tested).</summary>
internal sealed class FreezeLedger
{
    /// <summary>A drawn speed at or below this counts as stopped (shared with posing's model freeze).</summary>
    internal const float SpeedEpsilon = 0.001f;

    private readonly HashSet<long> _excluded = new();

    /// <summary>Everyone this freeze's hold never touches: the local player and their own mount.</summary>
    public IReadOnlyCollection<long> Excluded => _excluded;

    /// <summary>The local player's uuid for this freeze (0 = unknown: the player is not excluded until learned).</summary>
    public long Self { get; private set; }

    /// <summary>Starts a freeze: clears everything and records who the local player is.</summary>
    public void Begin(long self)
    {
        Clear();
        if (self == 0) return;
        Self = self;
        _excluded.Add(self);
    }

    /// <summary>Excludes <paramref name="uuid"/> (the local player's own mount) from this freeze. True when newly excluded;
    /// 0 is ignored.</summary>
    public bool Exclude(long uuid) => uuid != 0 && _excluded.Add(uuid);

    /// <summary>True for the local player and their own mount — never held, never deferred.</summary>
    public bool Excludes(long uuid) => uuid != 0 && _excluded.Contains(uuid);

    /// <summary>Drops every excluded entity (the local player, their own mount) from this freeze's entity list.</summary>
    public void WithoutSelf(List<long> ids)
    {
        for (var i = ids.Count - 1; i >= 0; i--)
            if (Excludes(ids[i])) ids.RemoveAt(i);
    }

    public void Clear()
    {
        _excluded.Clear();
        Self = 0;
    }
}
