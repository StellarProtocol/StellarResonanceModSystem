namespace Stellar.Abstractions.Domain;

/// <summary>One ReShade technique in the loaded effects.</summary>
public sealed record ReShadeTechnique(string Name, string EffectFile, bool Enabled, bool UsesDepth);
