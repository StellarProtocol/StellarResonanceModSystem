namespace Stellar.Abstractions.Domain;

/// <summary>One ReShade technique in the loaded effects, identified by <paramref name="EffectFile"/> (the effect file
/// name as ReShade reports it, e.g. "Clarity.fx") plus <paramref name="Name"/>.</summary>
/// <param name="Name">Technique name; may repeat across effect files.</param>
/// <param name="EffectFile">Effect file name as ReShade reports it.</param>
/// <param name="Enabled">Whether the technique is currently on.</param>
/// <param name="UsesDepth">Whether the effect reads the depth buffer; true when that cannot be determined.</param>
public sealed record ReShadeTechnique(string Name, string EffectFile, bool Enabled, bool UsesDepth);
