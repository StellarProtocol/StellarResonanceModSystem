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
/// write-back), on live materials, in reverse capture order. Pure (unit-tested over fake materials). Main thread.
/// </summary>
internal sealed class ModelLightSnapshot
{
    internal static readonly LightProperty[] KeyProperties = { LightProperty.CameraLightParm };
    internal static readonly LightProperty[] RimProperties =
        { LightProperty.UseFresnel, LightProperty.FresnelColor, LightProperty.FresnelParms };

    private static readonly LightProperty[] All =
        { LightProperty.CameraLightParm, LightProperty.UseFresnel, LightProperty.FresnelColor, LightProperty.FresnelParms };

    private readonly List<(IMaterialSlot Slot, LightProperty Property, LightVector Value)> _saved = new();

    private ModelLightSnapshot() { }

    /// <summary>How many material values were saved.</summary>
    public int Count => _saved.Count;

    /// <summary>Reads every light property of every material, now.</summary>
    public static ModelLightSnapshot Capture(IReadOnlyList<IMaterialSlot> materials)
    {
        var s = new ModelLightSnapshot();
        foreach (var m in materials)
        {
            if (!m.IsLive) continue;
            foreach (var p in All)
                if (m.TryRead(p, out var v)) s._saved.Add((m, p, v));
        }
        return s;
    }

    /// <summary>Writes the saved values of <paramref name="properties"/> back where they differ now; returns how many
    /// writes it made.</summary>
    public int Restore(IReadOnlyCollection<LightProperty> properties)
    {
        var writes = 0;
        for (var i = _saved.Count - 1; i >= 0; i--)
        {
            var (slot, p, v) = _saved[i];
            if (!Contains(properties, p) || !slot.IsLive) continue;
            if (slot.TryRead(p, out var now) && now == v) continue;
            slot.Write(p, v);
            writes++;
        }
        return writes;
    }

    /// <summary>Saved values that differ from what the materials hold now (the readback proof: 0 after a restore).</summary>
    public int Residual()
    {
        var n = 0;
        foreach (var (slot, p, v) in _saved)
            if (slot.IsLive && slot.TryRead(p, out var now) && now != v) n++;
        return n;
    }

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
