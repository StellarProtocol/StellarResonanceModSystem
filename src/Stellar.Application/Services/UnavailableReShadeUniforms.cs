using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>Null-object <see cref="IReShadeUniforms"/>: every override is refused (false), clearing does nothing. Used
/// when the shared <c>IReShade</c> cannot hold uniform overrides.</summary>
internal sealed class UnavailableReShadeUniforms : IReShadeUniforms
{
    /// <summary>Shared instance — this service holds no state.</summary>
    public static readonly UnavailableReShadeUniforms Instance = new();

    /// <inheritdoc/>
    public bool SetUniformOverride(string? effectFile, string variable, string? value) => false;
    /// <inheritdoc/>
    public void ClearUniformOverrides() { }
}
