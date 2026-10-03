namespace Stellar.Abstractions.Domain;

/// <summary>One ReShade technique in the loaded effects, identified by <paramref name="EffectFile"/> (the effect file
/// name as ReShade reports it, e.g. "Clarity.fx") plus <paramref name="Name"/>.</summary>
/// <param name="Name">Technique name; may repeat across effect files.</param>
/// <param name="EffectFile">Effect file name as ReShade reports it.</param>
/// <param name="Enabled">Whether the technique is currently on.</param>
/// <param name="UsesDepth">Whether the effect reads the depth buffer; true when that cannot be determined.</param>
public sealed record ReShadeTechnique(string Name, string EffectFile, bool Enabled, bool UsesDepth);

/// <summary>Where ReShade is in its lifecycle, as seen through the Stellar bridge add-on.</summary>
public enum ReShadeState
{
    /// <summary>ReShade or the Stellar bridge add-on is not loaded in the game (not installed, or a launch without
    /// it). Every <c>IReShade</c> member is then a no-op.</summary>
    NotInstalled,
    /// <summary>The bridge is loaded but ReShade is not ready yet, or it is (re)loading its effects — for example
    /// after a search-path or preset change. <c>Techniques</c> may be empty or stale; requests stay queued.</summary>
    Loading,
    /// <summary>ReShade has its effects loaded: <c>Techniques</c> is current and photos can carry its effects.</summary>
    Ready,
}
