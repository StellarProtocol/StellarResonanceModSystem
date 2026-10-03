using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Application.Abstractions;
using UnityEngine;
namespace Stellar.Infrastructure.Game.Lights;

/// <summary>One person's visible model for a key light / rim (<see cref="LightModelCalls"/>). The model object is held only
/// while the person is lit and is checked live before every use (docs/il2cpp-probing-safety.md). Main thread.</summary>
internal sealed class GameLightModel : ILightModel
{
    private readonly LightModelCalls _calls;
    private readonly Il2CppObjectBase _model;

    public GameLightModel(LightModelCalls calls, Il2CppObjectBase model)
    {
        _calls = calls;
        _model = model;
    }

    public bool IsLive => _calls.IsLive(_model);

    public bool IsSame(ILightModel other) => other is GameLightModel g && g._model.Pointer == _model.Pointer;

    public IMaterialSlot[] Materials()
    {
        if (!IsLive || _calls.RenderComp(_model) is not { } rc) return System.Array.Empty<IMaterialSlot>();
        var mats = _calls.Materials(rc);
        var slots = new IMaterialSlot[mats.Count];
        for (var i = 0; i < mats.Count; i++) slots[i] = new MaterialSlot(mats[i]);
        return slots;
    }

    public void ApplyKey(LightVector cameraSpace)
    {
        if (IsLive && _calls.RenderComp(_model) is { } rc) _calls.SetFixedLight(rc, ToVector(cameraSpace));
    }

    public void ApplyRim(LightVector color, LightVector parms)
    {
        if (IsLive && _calls.RenderComp(_model) is { } rc)
            _calls.SetFresnel(rc, new Color(color.X, color.Y, color.Z, color.W), ToVector(parms));
    }

    private static Vector4 ToVector(LightVector v) => new(v.X, v.Y, v.Z, v.W);
}

/// <summary>One material's key-light / rim properties (property ids resolved once).</summary>
internal sealed class MaterialSlot : IMaterialSlot
{
    private static readonly Dictionary<LightProperty, int> Ids = new()
    {
        [LightProperty.CameraLightParm] = Shader.PropertyToID("_CameraLightParm"),
        [LightProperty.UseFresnel] = Shader.PropertyToID("_UseFresnel"),
        [LightProperty.FresnelColor] = Shader.PropertyToID("_FresnelColor"),
        [LightProperty.FresnelParms] = Shader.PropertyToID("_FresnelParms"),
    };

    private readonly Material _m;

    public MaterialSlot(Material material) => _m = material;

    public bool IsLive => !_m.WasCollected && _m != null;

    public bool IsSame(IMaterialSlot other) => other is MaterialSlot o && o._m.Pointer == _m.Pointer;

    public bool TryRead(LightProperty property, out LightVector value)
    {
        value = default;
        var id = Ids[property];
        if (!_m.HasProperty(id)) return false;
        value = property switch
        {
            LightProperty.UseFresnel => new LightVector(_m.GetFloat(id), 0f, 0f, 0f),
            LightProperty.FresnelColor => FromColor(_m.GetColor(id)),
            _ => FromVector(_m.GetVector(id)),
        };
        return true;
    }

    public void Write(LightProperty property, LightVector value)
    {
        var id = Ids[property];
        switch (property)
        {
            case LightProperty.UseFresnel: _m.SetFloat(id, value.X); break;
            case LightProperty.FresnelColor: _m.SetColor(id, new Color(value.X, value.Y, value.Z, value.W)); break;
            default: _m.SetVector(id, new Vector4(value.X, value.Y, value.Z, value.W)); break;
        }
    }

    private static LightVector FromColor(Color c) => new(c.r, c.g, c.b, c.a);
    private static LightVector FromVector(Vector4 v) => new(v.x, v.y, v.z, v.w);
}
