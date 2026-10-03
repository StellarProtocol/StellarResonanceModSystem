using System;
using System.Collections.Generic;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// The raised character-lamp gate (recon Run 13b, the measured recipe): <see cref="Raise"/> snapshots every override flag
/// of the weather volume's <c>parameterList</c>, the gate parameter's value and the component's <c>active</c> flag, then
/// turns every flag off, overrides only the gate parameter and activates the component; <see cref="Restore"/> puts the
/// value, every flag and <c>active</c> back exactly (in that order — the probe's verified restore). The game's own "off"
/// restores nothing, so this snapshot is the only way back. Pure (unit-tested over a fake volume). Main thread.
/// </summary>
internal sealed class LightGate
{
    private readonly IGateVolume _volume;
    private readonly bool[] _flags;
    private readonly float _value;
    private readonly bool _active;
    private bool _restored;

    private LightGate(IGateVolume volume, bool[] flags, float value, bool active)
    {
        _volume = volume;
        _flags = flags;
        _value = value;
        _active = active;
    }

    /// <summary>The gate level written now.</summary>
    public float Level { get; private set; }

    /// <summary>What was saved: the gate parameter's own value, the <c>active</c> flag and how many flags were overriding
    /// (diagnostics).</summary>
    public (float Value, bool Active, int Overriding, int Flags) Saved
    {
        get
        {
            var on = 0;
            foreach (var f in _flags) if (f) on++;
            return (_value, _active, on, _flags.Length);
        }
    }

    /// <summary>Snapshots, then raises the gate to <paramref name="level"/>. Null (nothing written) when the volume has no
    /// gate parameter.</summary>
    public static LightGate? Raise(IGateVolume volume, float level)
    {
        var index = volume.GateIndex;
        var count = volume.Count;
        if (index < 0 || index >= count) return null;
        var flags = new bool[count];
        for (var i = 0; i < count; i++) flags[i] = volume.GetOverride(i);
        var gate = new LightGate(volume, flags, volume.Value, volume.Active);
        for (var i = 0; i < count; i++) volume.SetOverride(i, false);
        volume.SetOverride(index, true);
        volume.Value = level;
        volume.Active = true;
        gate.Level = level;
        return gate;
    }

    /// <summary>Changes the raised level (no-op once restored or when the volume is gone).</summary>
    public void SetLevel(float level)
    {
        if (_restored || !_volume.IsLive) return;
        _volume.Value = level;
        Level = level;
    }

    /// <summary>Puts the value, every override flag and <c>active</c> back. Once only; skipped for a volume that is gone
    /// (the game destroyed it — nothing of ours remains on it).</summary>
    public void Restore()
    {
        if (_restored) return;
        _restored = true;
        if (!_volume.IsLive) return;
        _volume.Value = _value;
        var n = Math.Min(_flags.Length, _volume.Count);
        for (var i = 0; i < n; i++) _volume.SetOverride(i, _flags[i]);
        _volume.Active = _active;
    }
}

/// <summary>The gate policy (lights spec § 6): the level to raise the gate to, or 0 to leave the game's value. Each owner
/// counts only while it has at least one lamp ON; the highest such owner's level wins. Pure.</summary>
internal static class LightGatePolicy
{
    /// <param name="owners">Per owner: whether it has a lamp on, and its people level.</param>
    public static float EffectiveLevel(IEnumerable<(bool AnyLampOn, float Level)> owners)
    {
        var level = 0f;
        foreach (var (on, l) in owners)
            if (on && l > level) level = l;
        return level;
    }
}
