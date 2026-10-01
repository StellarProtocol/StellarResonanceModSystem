using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>What one freeze touched: effect uids to unfreeze, each entity's prior animation factor (stage 1) and prior
/// drawn speed (stage 2).</summary>
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

    public void TouchEffect(long uid) => _effects.Add(uid);

    /// <summary>Remembers <paramref name="prior"/> for <paramref name="uuid"/>; the first value saved wins.</summary>
    public void SaveFactor(long uuid, float prior) => _factors.TryAdd(uuid, prior);

    /// <summary>Remembers a prior drawn <c>AnimComp.Speed</c>; the first value saved wins.</summary>
    public void SaveSpeed(long uuid, float prior) => _speeds.TryAdd(uuid, prior);

    public void Clear()
    {
        _effects.Clear();
        _factors.Clear();
        _speeds.Clear();
    }

    /// <summary>The value to write back on unfreeze, or null when the game has written its own value since.</summary>
    public static float? RestoreValue(float prior, float current) => current == FrozenFactor ? prior : null;

    /// <summary>The drawn speed to write back on unfreeze, or null when the game has set its own since.</summary>
    public static float? RestoreSpeed(float prior, float current) => current <= SpeedEpsilon ? prior : null;
}
