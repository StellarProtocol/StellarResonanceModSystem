using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>Game side of <c>ILights</c> (devkit recon <c>docs/recon/free-camera-recon.md</c> § Run 13, probe
/// <c>probe/lights@5d31428</c>): MultiLight cluster lamps, the weather volume that gates lamp light on characters, and
/// the visible model of a person. Plain reflected calls only — no hook. Main thread.</summary>
internal interface ILightsBackend
{
    /// <summary>Makes a lamp (a Unity <c>Light</c> + the game's <c>MultiLight</c>, set up inactive then activated so it
    /// joins the cluster). Null when the game's lamp type is missing; game exceptions propagate.</summary>
    object? CreateLamp(LampSettings settings);

    /// <summary>Moves / recolours / switches a lamp made by <see cref="CreateLamp"/>; a destroyed lamp is a no-op.</summary>
    void UpdateLamp(object lamp, LampSettings settings);

    /// <summary>Takes the lamp out of the cluster at once and destroys it; a destroyed lamp is a no-op.</summary>
    void DestroyLamp(object lamp);

    /// <summary>The volume whose <c>creaturePointlightColorIntensity</c> gates lamp light on characters
    /// (<c>CameraManager.weatherParamsVolume_</c>); null when the game has none right now.</summary>
    IGateVolume? GateVolume();

    /// <summary>The model <paramref name="uuid"/> is seen as now: their posed copy / NPC stand-in while there is one, else
    /// their own live model. Null when gone, destroying or modelless.</summary>
    ILightModel? ResolveModel(long uuid);
}

/// <summary>The weather volume's override flags, the gate parameter's value and the component's <c>active</c> flag — all
/// a gate raise touches and a restore puts back.</summary>
internal interface IGateVolume
{
    /// <summary>How many parameters the volume's <c>parameterList</c> holds (101 in release_3.7).</summary>
    int Count { get; }

    /// <summary>The index of <c>creaturePointlightColorIntensity</c> in that list; −1 when it is not there.</summary>
    int GateIndex { get; }

    /// <summary>Parameter <paramref name="index"/>'s <c>overrideState</c>.</summary>
    bool GetOverride(int index);

    /// <summary>Sets parameter <paramref name="index"/>'s <c>overrideState</c>.</summary>
    void SetOverride(int index, bool value);

    /// <summary>The gate parameter's own value (not the blended stack value).</summary>
    float Value { get; set; }

    /// <summary>The component's <c>active</c> flag.</summary>
    bool Active { get; set; }

    /// <summary>True while it is the same live game object it was when resolved.</summary>
    bool IsLive { get; }
}

/// <summary>One person's visible model: the materials a key light / rim writes, and the game calls that write them.</summary>
internal interface ILightModel
{
    /// <summary>Not collected and not destroying.</summary>
    bool IsLive { get; }

    /// <summary>True when <paramref name="other"/> wraps the same game model.</summary>
    bool IsSame(ILightModel other);

    /// <summary>The model's materials now (both the rendered and the origin sets, each once).</summary>
    IMaterialSlot[] Materials();

    /// <summary><c>RenderCompBase.SetFixedLight(ref ModelFixedLightData{LightParms}, all)</c> → <c>_CameraLightParm</c>.</summary>
    void ApplyKey(LightVector cameraSpace);

    /// <summary><c>RenderCompBase.SetFresnelEffect(1, colour, parms, All)</c> → <c>_UseFresnel</c> / <c>_FresnelColor</c> /
    /// <c>_FresnelParms</c>.</summary>
    void ApplyRim(LightVector color, LightVector parms);
}

/// <summary>One material of a model.</summary>
internal interface IMaterialSlot
{
    /// <summary>Not destroyed.</summary>
    bool IsLive { get; }

    /// <summary>True when <paramref name="other"/> wraps the same game material (a fresh wrapper per
    /// <see cref="ILightModel.Materials"/> call — never compare by reference).</summary>
    bool IsSame(IMaterialSlot other);

    /// <summary>Reads <paramref name="property"/> (floats in X); false when this material's shader lacks it.</summary>
    bool TryRead(LightProperty property, out LightVector value);

    /// <summary>Writes <paramref name="property"/> back (floats from X).</summary>
    void Write(LightProperty property, LightVector value);
}

/// <summary>The material properties a key light and a rim write.</summary>
internal enum LightProperty
{
    /// <summary><c>_CameraLightParm</c> (vector; w = 1 turns the key on). Key light.</summary>
    CameraLightParm,
    /// <summary><c>_UseFresnel</c> (float). Rim.</summary>
    UseFresnel,
    /// <summary><c>_FresnelColor</c> (colour). Rim.</summary>
    FresnelColor,
    /// <summary><c>_FresnelParms</c> (vector). Rim.</summary>
    FresnelParms,
}

/// <summary>A four-float value (vector, colour, or a float in X) — BCL-only stand-in for Unity's Vector4/Color.</summary>
internal readonly record struct LightVector(float X, float Y, float Z, float W);
