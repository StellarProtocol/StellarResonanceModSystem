namespace Stellar.Application.Abstractions;

/// <summary>
/// Optional capability of an <c>IReShade</c> implementation that scans effect sources: per-effect facts the capture
/// needs but plugins do not (kept off the public <c>ReShadeTechnique</c>).
/// </summary>
internal interface IReShadeEffectTraits
{
    /// <summary>Whether <paramref name="effectFile"/>'s output depends on earlier frames (adaptation, accumulation) —
    /// true when not known (not scanned yet, not found, unreadable). Main thread.</summary>
    bool IsTemporal(string effectFile);
}
