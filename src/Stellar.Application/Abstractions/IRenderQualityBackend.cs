using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>Live shadow settings (three members of the game's one shadow-settings object).</summary>
internal readonly record struct ShadowValues(int Resolution, int Cascades, bool Soft);

/// <summary>
/// The game side of render quality: raw reads and writes of each lever, nothing else. A read returns null when
/// the lever cannot be read right now (types not resolved, not in a stable world scene); the arbiter then retries
/// on the next re-assert. Writes never throw; they return false when the game call failed. Main thread only.
/// </summary>
internal interface IRenderQualityBackend
{
    RenderQualityCapabilities Capabilities { get; }
    float? ReadRenderScale();
    bool WriteRenderScale(float scale);
    bool? ReadTaa();
    /// <summary>Sets the TAA property AND pushes it through the game's apply method (the property alone does nothing).</summary>
    bool WriteTaa(bool on);
    ShadowValues? ReadShadows();
    /// <summary>Writes each member of <paramref name="values"/> that differs from the live object.</summary>
    bool WriteShadows(ShadowValues values);
    /// <summary>Installs the game re-assert hooks if not yet installed (lazy: first Request). Idempotent.</summary>
    void EnsureHooks();
}
