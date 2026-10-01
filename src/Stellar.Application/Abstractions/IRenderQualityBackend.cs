using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>Live shadow settings (three members of the game's one shadow-settings object).</summary>
internal readonly record struct ShadowValues(int Resolution, int Cascades, bool Soft);

/// <summary>
/// The game side of render quality: raw reads and writes of each lever, nothing else. A read returns null when
/// the lever cannot be read right now (types not resolved, not in a stable world scene); the arbiter then retries
/// on the next re-assert. Writes never throw. Main thread only.
/// </summary>
internal interface IRenderQualityBackend
{
    RenderQualityCapabilities Capabilities { get; }
    float? ReadRenderScale();
    void WriteRenderScale(float scale);
    bool? ReadTaa();
    /// <summary>Sets the TAA property AND pushes it through the game's apply method (the property alone does nothing).</summary>
    void WriteTaa(bool on);
    ShadowValues? ReadShadows();
    /// <summary>Writes each member of <paramref name="values"/> that differs from the live object.</summary>
    void WriteShadows(ShadowValues values);
}
