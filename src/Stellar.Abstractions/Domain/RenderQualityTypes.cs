namespace Stellar.Abstractions.Domain;

/// <summary>What a plugin asks <see cref="Services.IRenderQuality"/> to raise while its token is held.</summary>
public sealed record RenderQualityRequest
{
    /// <summary>Render at 2.0× internal resolution (the pipeline downsamples to the screen) with temporal
    /// anti-aliasing on. Costs a lot of GPU time.</summary>
    public bool Supersample { get; init; }
    /// <summary>High shadow quality: a 4096 shadow map (the game's highest preset is 2048), 3 cascades and soft
    /// shadows.</summary>
    public bool HighShadows { get; init; }
}

/// <summary>Live read-back of the game's render-quality values (what the pipeline actually holds right now).
/// Values that cannot be read on this client read as 0 / false.</summary>
/// <param name="RenderScale">Internal render scale (1.0 = native; 2.0 = supersampled).</param>
/// <param name="TaaOn">Whether temporal anti-aliasing is on.</param>
/// <param name="ShadowResolution">Shadow-map resolution in pixels.</param>
/// <param name="Cascades">Shadow cascade count.</param>
/// <param name="SoftShadows">Whether soft shadows are on.</param>
public readonly record struct RenderQualityState(
    float RenderScale, bool TaaOn, int ShadowResolution, int Cascades, bool SoftShadows);

/// <summary>Which render-quality levers resolved on this client. A lever that is false cannot be driven (grey its
/// control out). Reported false until the game's render types have loaded.</summary>
/// <param name="RenderScale">The render-scale lever behind <see cref="RenderQualityRequest.Supersample"/>.</param>
/// <param name="Taa">The temporal anti-aliasing lever that rides along with supersampling.</param>
/// <param name="Shadows">The shadow-settings lever behind <see cref="RenderQualityRequest.HighShadows"/>.</param>
public readonly record struct RenderQualityCapabilities(bool RenderScale, bool Taa, bool Shadows);
