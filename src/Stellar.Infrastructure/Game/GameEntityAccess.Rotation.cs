using System;
using System.Reflection;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>A model's LOGICAL rotation (<c>EntityAttrExtensions.GetAttrGoRotation(ZModel)</c> — the same extension
/// family as <see cref="AttrPosition"/>; posing reads it too), for the freeze hold's release snap (combat-freeze fix
/// 2026-10-02: the hold now holds rotation, so the release must hand it back). Null when unreadable.</summary>
internal sealed partial class GameEntityAccess
{
    private MethodInfo? _attrRot;
    private bool _attrRotTried;

    public Quaternion? AttrRotation(object model)
    {
        if (!_attrRotTried)
        {
            if (!Resolve() || _types.FindType(AttrExtType) is not { } ext || _types.FindType(ModelType) is not { } type) return null;
            _attrRotTried = true;
            _attrRot = ext.GetMethod("GetAttrGoRotation", BindingFlags.Public | BindingFlags.Static, null, new[] { type }, null);
        }
        if (_attrRot is null) return null;
        _arg1[0] = model;
        try { return _attrRot.Invoke(null, _arg1) is Quaternion q ? q : null; }
        catch { return null; }
    }
}
