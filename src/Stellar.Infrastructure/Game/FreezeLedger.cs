using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>What one freeze touched: effect uids to unfreeze, each entity's prior animation factor (stage 1) and drawn
/// speed to restore (stage 2), and who is never frozen. The local player (Photo Studio scene-stays spec § 3, regression
/// <c>scene-stays-self-never-frozen</c>) and the mount they ride (review I1, regression
/// <c>scene_stays_own_mount_never_frozen</c>) are EXCLUDED: <see cref="Begin"/> names the player, <see cref="Exclude"/>
/// adds the mount, the entity list drops them (<see cref="WithoutSelf"/>), and every admission below refuses them — the
/// backend writes a frozen value only after an admission, so a refusal means nothing is written.
/// <para><b>Restore values</b> (combat-resume fix, regression <c>freeze_combat_resume_*</c>): the factor keeps the FIRST
/// prior — a re-apply writes 0 again and never touches it (the game's mid-freeze factor writes are resets to its own
/// default, recon R3-10). The drawn speed keeps the LATEST value the game wanted: first the value read at stage 2, then
/// every game write made while frozen (the <c>AnimCompBase.set_Speed</c> gate, or a re-apply that finds a game-written
/// speed) — on unfreeze the entity resumes at the speed the game last asked for, not a stale pre-freeze one.</para>
/// Pure (unit-tested).</summary>
internal sealed class FreezeLedger
{
    internal const float FrozenFactor = 0f;
    /// <summary>A drawn speed at or below this counts as stopped.</summary>
    internal const float SpeedEpsilon = 0.001f;

    private readonly HashSet<long> _effects = new();
    private readonly Dictionary<long, float> _factors = new();
    private readonly Dictionary<long, float> _speeds = new();
    private readonly HashSet<long> _excluded = new();

    public IReadOnlyCollection<long> Effects => _effects;
    public IReadOnlyDictionary<long, float> Factors => _factors;
    public IReadOnlyDictionary<long, float> Speeds => _speeds;
    /// <summary>Everyone this freeze never touches: the local player and their own mount.</summary>
    public IReadOnlyCollection<long> Excluded => _excluded;

    /// <summary>The local player's uuid for this freeze (0 = unknown: the player is not excluded until learned).</summary>
    public long Self { get; private set; }

    /// <summary>Starts a freeze: clears everything and records who the local player is.</summary>
    public void Begin(long self)
    {
        Clear();
        LearnSelf(self);
    }

    /// <summary>Records the local player when this freeze did not know them yet (uuid 0 at the press — review M1). True
    /// when newly learned: the caller then releases whatever the press already froze on them.</summary>
    public bool LearnSelf(long self)
    {
        if (Self != 0 || self == 0) return false;
        Self = self;
        _excluded.Add(self);
        return true;
    }

    /// <summary>Excludes <paramref name="uuid"/> (the local player's own mount) from this freeze. True when newly excluded;
    /// 0 is ignored.</summary>
    public bool Exclude(long uuid) => uuid != 0 && _excluded.Add(uuid);

    /// <summary>True for the local player and their own mount — never frozen (no factor, no drawn speed, no hold, no
    /// appear re-check).</summary>
    public bool Excludes(long uuid) => uuid != 0 && _excluded.Contains(uuid);

    /// <summary>Drops every excluded entity (the local player, their own mount) from this freeze's entity list.</summary>
    public void WithoutSelf(List<long> ids)
    {
        for (var i = ids.Count - 1; i >= 0; i--)
            if (Excludes(ids[i])) ids.RemoveAt(i);
    }

    public void TouchEffect(long uid) => _effects.Add(uid);

    /// <summary>Remembers <paramref name="prior"/> for <paramref name="uuid"/>; the first value saved wins. False — keep
    /// nothing — for an excluded entity or a second save.</summary>
    public bool SaveFactor(long uuid, float prior) => !Excludes(uuid) && _factors.TryAdd(uuid, prior);

    /// <summary>Remembers a prior drawn <c>AnimComp.Speed</c>; the first value saved wins. False — keep nothing — for an
    /// excluded entity or a second save.</summary>
    public bool SaveSpeed(long uuid, float prior) => !Excludes(uuid) && _speeds.TryAdd(uuid, prior);

    /// <summary>Stage 2 or a re-apply found a drawn speed above 0 on <paramref name="uuid"/>: true = write 0 now (every
    /// time — a re-apply after a game rewrite is never a no-op). The first reading is the restore value; a later reading
    /// above 0 can only be a game write since ours, so it becomes the restore value (latest wins). False only for an
    /// excluded entity.</summary>
    public bool AdmitSpeed(long uuid, float observed)
    {
        if (Excludes(uuid)) return false;
        if (!SaveSpeed(uuid, observed) && observed > SpeedEpsilon) _speeds[uuid] = observed;
        return true;
    }

    /// <summary>The game wrote <paramref name="value"/> to a frozen entity's drawn speed (the <c>set_Speed</c> gate): a
    /// value above 0 becomes the restore value (latest wins) and the gate writes 0 instead. A game write of 0 never
    /// replaces a kept speed — while frozen it is most likely the game applying OUR frozen factor (0) to the speed, and
    /// keeping it would leave the entity stopped after unfreeze. False — substitute nothing — for an excluded entity.</summary>
    public bool NoteGameSpeed(long uuid, float value)
    {
        if (Excludes(uuid)) return false;
        if (value > SpeedEpsilon) _speeds[uuid] = value;
        return true;
    }

    /// <summary>Drops everything kept for <paramref name="uuid"/> (an entity this freeze released after the press: the
    /// late-learned local player, a mount that turned out to be theirs).</summary>
    public void Forget(long uuid)
    {
        _factors.Remove(uuid);
        _speeds.Remove(uuid);
    }

    public void Clear()
    {
        _effects.Clear();
        _factors.Clear();
        _speeds.Clear();
        _excluded.Clear();
        Self = 0;
    }

    /// <summary>The value to write back on unfreeze, or null when the game has written its own value since.</summary>
    public static float? RestoreValue(float prior, float current) => current == FrozenFactor ? prior : null;

    /// <summary>The drawn speed to write back on unfreeze, or null when the game has set its own since.</summary>
    public static float? RestoreSpeed(float prior, float current) => current <= SpeedEpsilon ? prior : null;
}
