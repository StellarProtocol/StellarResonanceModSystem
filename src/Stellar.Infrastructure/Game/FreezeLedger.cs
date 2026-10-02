using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>What one freeze touched: effect uids to unfreeze, each entity's prior animation factor (stage 1) and prior
/// drawn speed (stage 2). The local player is never admitted (Photo Studio scene-stays spec § 3, regression
/// <c>scene-stays-self-never-frozen</c>): <see cref="Begin"/> names them, the entity list drops them
/// (<see cref="WithoutSelf"/>), and <see cref="SaveFactor"/> / <see cref="SaveSpeed"/> refuse them — the backend writes a
/// frozen value only after its save was admitted, so a refusal means nothing is written. Pure (unit-tested).</summary>
internal sealed class FreezeLedger
{
    internal const float FrozenFactor = 0f;
    /// <summary>A drawn speed at or below this counts as stopped.</summary>
    internal const float SpeedEpsilon = 0.001f;

    private readonly HashSet<long> _effects = new();
    private readonly Dictionary<long, float> _factors = new();
    private readonly Dictionary<long, float> _speeds = new();

    public IReadOnlyCollection<long> Effects => _effects;
    public IReadOnlyDictionary<long, float> Factors => _factors;
    public IReadOnlyDictionary<long, float> Speeds => _speeds;

    /// <summary>The local player's uuid for this freeze (0 = unknown: nobody is excluded).</summary>
    public long Self { get; private set; }

    /// <summary>Starts a freeze: clears everything and records who the local player is.</summary>
    public void Begin(long self)
    {
        Clear();
        Self = self;
    }

    /// <summary>True for the local player — never frozen (no factor, no drawn speed, no hold, no appear re-check).</summary>
    public bool Excludes(long uuid) => Self != 0 && uuid == Self;

    /// <summary>Drops the local player from this freeze's entity list.</summary>
    public void WithoutSelf(List<long> ids)
    {
        for (var i = ids.Count - 1; i >= 0; i--)
            if (Excludes(ids[i])) ids.RemoveAt(i);
    }

    public void TouchEffect(long uid) => _effects.Add(uid);

    /// <summary>Remembers <paramref name="prior"/> for <paramref name="uuid"/>; the first value saved wins. False — keep
    /// nothing, write nothing — for the local player or a second save.</summary>
    public bool SaveFactor(long uuid, float prior) => !Excludes(uuid) && _factors.TryAdd(uuid, prior);

    /// <summary>Remembers a prior drawn <c>AnimComp.Speed</c>; the first value saved wins. False — keep nothing, write
    /// nothing — for the local player or a second save.</summary>
    public bool SaveSpeed(long uuid, float prior) => !Excludes(uuid) && _speeds.TryAdd(uuid, prior);

    public void Clear()
    {
        _effects.Clear();
        _factors.Clear();
        _speeds.Clear();
        Self = 0;
    }

    /// <summary>The value to write back on unfreeze, or null when the game has written its own value since.</summary>
    public static float? RestoreValue(float prior, float current) => current == FrozenFactor ? prior : null;

    /// <summary>The drawn speed to write back on unfreeze, or null when the game has set its own since.</summary>
    public static float? RestoreSpeed(float prior, float current) => current <= SpeedEpsilon ? prior : null;
}
