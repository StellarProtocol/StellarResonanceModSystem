using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// The raised character-lamp gate (recon Run 13b, the measured recipe): <see cref="Raise"/> snapshots every override flag
/// of the weather volume's <c>parameterList</c>, the gate parameter's value and the component's <c>active</c> flag (every
/// read before the first write), then — on an inactive volume — turns every flag off, overrides only the gate parameter and
/// activates the component; on a volume the game already has active it only adds the gate override (the game's own
/// overrides stay — review I-4). A throw part-way rolls every write back from the snapshot before it propagates (review
/// I-2). <see cref="Restore"/> puts the value, every flag and <c>active</c> back exactly (in that order — the probe's
/// verified restore), attempting every write even when one fails — but ONLY while the volume still holds exactly what we
/// left there (our level, our flags, active): if the game has written it since (a cutscene's <c>FixedLightTrack</c>, its
/// own weather), the game owns it and it is left alone (<see cref="LeftToGame"/>). The game's own "off" restores nothing,
/// so this snapshot is the only way back. Pure (unit-tested over a fake volume). Main thread.
/// </summary>
internal sealed class LightGate
{
    /// <summary>Our level read back is the float we wrote; the slack only absorbs a boxing round trip.</summary>
    private const float Same = 1e-5f;

    private readonly IGateVolume _volume;
    private readonly bool[] _flags;
    private readonly bool[] _written;
    private readonly float _value;
    private readonly bool _active;
    private bool _restored;

    private LightGate(IGateVolume volume, bool[] flags, bool[] written, float value, bool active)
    {
        _volume = volume;
        _flags = flags;
        _written = written;
        _value = value;
        _active = active;
    }

    /// <summary>The gate level written now.</summary>
    public float Level { get; private set; }

    /// <summary>True when <see cref="Restore"/> found the volume changed by the game since our write and left it alone.</summary>
    public bool LeftToGame { get; private set; }

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

    /// <summary>Raises the gate to <paramref name="level"/>; null (nothing written) when the volume has no gate parameter.
    /// A throw part-way is rolled back (every write attempted) and rethrown.</summary>
    public static LightGate? Raise(IGateVolume volume, float level)
    {
        var index = volume.GateIndex;
        var count = volume.Count;
        if (index < 0 || index >= count) return null;
        var flags = new bool[count];
        for (var i = 0; i < count; i++) flags[i] = volume.GetOverride(i);
        var value = volume.Value;
        var active = volume.Active;
        var written = active ? (bool[])flags.Clone() : new bool[count];
        written[index] = true;
        var gate = new LightGate(volume, flags, written, value, active) { Level = level };
        try { gate.Write(index, level); }
        catch
        {
            gate._restored = true;
            gate.WriteBack(rethrow: false);
            throw;
        }
        return gate;
    }

    /// <summary>Moves the raised gate to <paramref name="level"/>.</summary>
    public void SetLevel(float level)
    {
        if (_restored || !_volume.IsLive) return;
        _volume.Value = level;
        Level = level;
    }

    /// <summary>Puts the game's value, flags and <c>active</c> back — once, only on a live volume that still holds exactly
    /// what we wrote. Every write is attempted; the first failure is rethrown after the rest.</summary>
    public void Restore()
    {
        if (_restored) return;
        _restored = true;
        if (!_volume.IsLive) return;
        if (!StillOurs())
        {
            LeftToGame = true;
            return;
        }
        WriteBack(rethrow: true);
    }

    private void Write(int index, float level)
    {
        if (_active) _volume.SetOverride(index, true);   // the game's active volume: add ours, keep its overrides
        else
        {
            for (var i = 0; i < _written.Length; i++) _volume.SetOverride(i, false);
            _volume.SetOverride(index, true);
        }
        _volume.Value = level;
        if (!_active) _volume.Active = true;   // last: the component turns on fully set up
    }

    private bool StillOurs()
    {
        if (!_volume.Active || _volume.Count != _written.Length || MathF.Abs(_volume.Value - Level) > Same) return false;
        for (var i = 0; i < _written.Length; i++)
            if (_volume.GetOverride(i) != _written[i]) return false;
        return true;
    }

    private void WriteBack(bool rethrow)
    {
        Exception? first = null;
        try { _volume.Value = _value; }
        catch (Exception ex) { first ??= ex; }
        var n = Math.Min(_flags.Length, _volume.Count);
        for (var i = 0; i < n; i++)
        {
            try { _volume.SetOverride(i, _flags[i]); }
            catch (Exception ex) { first ??= ex; }
        }
        try { _volume.Active = _active; }
        catch (Exception ex) { first ??= ex; }
        if (rethrow && first is not null) ExceptionDispatchInfo.Capture(first).Throw();
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
