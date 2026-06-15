using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>Resolves a slot key to its effective colour for the active theme
/// (override → owner per-preset default → terminal). Read-only; the registry
/// implements it.</summary>
internal interface IColorResolver
{
    ColorRgba Resolve(string slotKey);
}
