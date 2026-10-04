namespace Stellar.Infrastructure.Rendering;

/// <summary>The bridge add-on's optional uniform-override exports (1.1.0+), separate from <see cref="IReShadeNative"/>
/// so a 1.0.0 add-on binds without them. Every member returns a default instead of throwing.</summary>
internal interface IReShadeUniformNative
{
    /// <summary>True when the bound add-on exports <c>rsb_set_uniform_override</c> and <c>rsb_clear_uniform_overrides</c>.</summary>
    bool UniformOverridesSupported { get; }
    /// <summary><c>rsb_set_uniform_override</c>: 1 ok, 0 bad input (also: unsupported / not bound).</summary>
    int SetUniformOverride(string? effectFile, string variable, string? value);
    /// <summary><c>rsb_clear_uniform_overrides</c>.</summary>
    void ClearUniformOverrides();
}
