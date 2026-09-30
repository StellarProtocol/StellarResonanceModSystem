using Stellar.Abstractions.Services;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary>Camera → local player distance for "focus on my character" depth of field. Main thread only.</summary>
internal static class LocalPlayerFocus
{
    /// <summary>Distance in metres from the main camera to the local player, or null when either is unknown.</summary>
    public static float? Measure(IEntityTransforms? transforms, ICombatSnapshot? combat)
    {
        if (transforms is null || combat is null) return null;
        var id = combat.LocalEntityId;
        if (id.IsNone || !transforms.TryGetTransform(id, out var p, out _)) return null;
        var cam = Camera.main;
        if (cam == null) return null;
        return Vector3.Distance(cam.transform.position, new Vector3(p.X, p.Y, p.Z));
    }
}
