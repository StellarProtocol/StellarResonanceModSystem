using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// One model's key-light and rim material values as they were before the first write (recon Run 13 Q3): every material
/// that carries <c>_CameraLightParm</c>, <c>_UseFresnel</c>, <c>_FresnelColor</c> or <c>_FresnelParms</c>, per property.
/// The game's own "off" (<c>SetFresnelEffect(0, …)</c>) resets only <c>_UseFresnel</c> and leaves the colour and params, so
/// the way back is writing this snapshot. <see cref="Restore"/> writes back only values that differ (the probe's
/// write-back), on live materials, in reverse capture order. A material the model gains later (an equipment change) is
/// added by <see cref="AddNew"/> before the next write (lights review I-1: read before write, every time). A model born
/// from a lit one (a posed copy cloned while its person was lit — review concern 1) is captured with the lit model's
/// ORIGINALS for the values that still equal what we wrote there (<see cref="Capture(IReadOnlyList{IMaterialSlot}, ModelLightSnapshot?, Func{LightProperty, bool}?)"/>).
/// Pure (unit-tested over fake materials). Main thread.
/// </summary>
internal sealed class ModelLightSnapshot
{
    internal static readonly LightProperty[] KeyProperties = { LightProperty.CameraLightParm };
    internal static readonly LightProperty[] RimProperties =
        { LightProperty.UseFresnel, LightProperty.FresnelColor, LightProperty.FresnelParms };

    private static readonly LightProperty[] All =
        { LightProperty.CameraLightParm, LightProperty.UseFresnel, LightProperty.FresnelColor, LightProperty.FresnelParms };

    /// <summary>Two reads of one value written once are the same floats; the slack only absorbs a colour-space round trip.</summary>
    private const float Same = 1e-4f;

    private readonly List<Entry> _saved = new();
    private readonly List<IMaterialSlot> _slots = new();

    private readonly record struct Entry(IMaterialSlot Slot, int Ordinal, LightProperty Property, LightVector Value);

    private ModelLightSnapshot() { }

    /// <summary>How many material values were saved.</summary>
    public int Count => _saved.Count;

    /// <summary>Reads every light property of every material, now.</summary>
    public static ModelLightSnapshot Capture(IReadOnlyList<IMaterialSlot> materials) => Capture(materials, null, null);

    /// <summary>Reads every light property of every material, now. With <paramref name="seed"/> (the snapshot of the lit
    /// model this one replaces, read BEFORE that model is written back) a value of a property we have written
    /// (<paramref name="written"/>) that equals what the lit model holds now is our light copied over, not the model's own:
    /// the lit model's original is saved instead (same material position first, else any material of that property).</summary>
    public static ModelLightSnapshot Capture(IReadOnlyList<IMaterialSlot> materials, ModelLightSnapshot? seed,
        Func<LightProperty, bool>? written)
    {
        var s = new ModelLightSnapshot();
        for (var i = 0; i < materials.Count; i++) s.Take(materials[i], i, seed, written);
        return s;
    }

    /// <summary>Captures the materials not yet in the snapshot (the model gained them since); returns how many were added.
    /// Call before every write.</summary>
    public int AddNew(IReadOnlyList<IMaterialSlot> materials)
    {
        var before = _saved.Count;
        for (var i = 0; i < materials.Count; i++)
            if (!Known(materials[i])) Take(materials[i], i, null, null);
        return _saved.Count - before;
    }

    /// <summary>Writes the saved values of <paramref name="properties"/> back where they differ now; returns how many
    /// writes it made.</summary>
    public int Restore(IReadOnlyCollection<LightProperty> properties)
    {
        var writes = 0;
        for (var i = _saved.Count - 1; i >= 0; i--)
        {
            var e = _saved[i];
            if (!Contains(properties, e.Property) || !e.Slot.IsLive) continue;
            if (e.Slot.TryRead(e.Property, out var now) && now == e.Value) continue;
            e.Slot.Write(e.Property, e.Value);
            writes++;
        }
        return writes;
    }

    /// <summary>Saved values that differ from what the materials hold now (the readback proof: 0 after a restore).</summary>
    public int Residual()
    {
        var n = 0;
        foreach (var e in _saved)
            if (e.Slot.IsLive && e.Slot.TryRead(e.Property, out var now) && now != e.Value) n++;
        return n;
    }

    private void Take(IMaterialSlot m, int ordinal, ModelLightSnapshot? seed, Func<LightProperty, bool>? written)
    {
        if (!m.IsLive) return;
        _slots.Add(m);
        foreach (var p in All)
        {
            if (!m.TryRead(p, out var v)) continue;
            if (seed is not null && written is not null && written(p) && seed.OriginalOfLit(ordinal, p, v) is { } original)
                v = original;
            _saved.Add(new Entry(m, ordinal, p, v));
        }
    }

    /// <summary>The original of a value the lit model holds now that equals <paramref name="now"/> — our write, copied.</summary>
    private LightVector? OriginalOfLit(int ordinal, LightProperty p, LightVector now)
    {
        LightVector? any = null;
        foreach (var e in _saved)
        {
            if (e.Property != p || !e.Slot.IsLive || !e.Slot.TryRead(p, out var lit) || !Near(lit, now)) continue;
            if (e.Ordinal == ordinal) return e.Value;
            any ??= e.Value;
        }
        return any;
    }

    private bool Known(IMaterialSlot m)
    {
        foreach (var s in _slots) if (s.IsSame(m)) return true;
        return false;
    }

    private static bool Near(LightVector a, LightVector b) =>
        MathF.Abs(a.X - b.X) <= Same && MathF.Abs(a.Y - b.Y) <= Same && MathF.Abs(a.Z - b.Z) <= Same && MathF.Abs(a.W - b.W) <= Same;

    private static bool Contains(IReadOnlyCollection<LightProperty> set, LightProperty p)
    {
        foreach (var x in set) if (x == p) return true;
        return false;
    }
}

/// <summary>Key light and rim values in the material's terms (recon Run 13 Q3). Pure.</summary>
internal static class PersonLightMath
{
    /// <summary>The probe's rim parameters (<c>(−0.7, 1, 1, 1)</c>: +4.4 L warm rim on hair / headwear / weapon).</summary>
    internal static readonly LightVector RimParms = new(-0.7f, 1f, 1f, 1f);

    /// <summary><c>_CameraLightParm</c> for a key light: a camera-space direction TOWARD the light (x right, y up, −z toward
    /// the camera — measured: (0,0,−1,1) lights from the camera, (1,0,0,1) from the side), w = 1 turns it on.</summary>
    public static LightVector ToCameraSpace(KeyLight key)
    {
        var az = Rad(Math.Clamp(key.Direction, -180f, 180f));
        var el = Rad(Math.Clamp(key.Height, -89f, 89f));
        var c = MathF.Cos(el);
        return new LightVector(MathF.Sin(az) * c, MathF.Sin(el), -MathF.Cos(az) * c, 1f);
    }

    /// <summary><c>_FresnelColor</c> for a rim: the colour scaled by its strength (clamped 0–max), alpha 1.</summary>
    public static LightVector RimColor(RimLight rim)
    {
        var s = Math.Clamp(rim.Strength, 0f, LightLimits.MaxRimStrength);
        return new LightVector(Clamp01(rim.Color.R) * s, Clamp01(rim.Color.G) * s, Clamp01(rim.Color.B) * s, 1f);
    }

    /// <summary>A rim that would show (strength above 0).</summary>
    public static bool Shows(RimLight? rim) => rim is { } r && r.Strength > 0f;

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);
    private static float Rad(float deg) => deg * (MathF.PI / 180f);
}
