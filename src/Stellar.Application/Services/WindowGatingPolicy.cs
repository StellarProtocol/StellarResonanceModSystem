using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>Pure rule: a window/HUD is draw-suppressed purely when its plugin-owned
/// <see cref="IRenderGated.ShouldRender"/> predicate returns false. The framework enacts; the plugin decides.
/// (The <c>MasterHudKill</c> dev override is applied separately in the renderer, outside this policy.)</summary>
internal static class WindowGatingPolicy
{
    /// <summary>Fail-safe: a <c>null</c> predicate — a plugin binary built before <see cref="IRenderGated.ShouldRender"/>
    /// existed, where the CLR does not enforce the compile-time <c>required</c> — suppresses the draw (hidden)
    /// rather than invoking a null delegate. A predicate that <em>throws</em> is not caught here; the caller
    /// (<c>SafeApply</c>) does that and also fails safe. New plugins must still set <c>ShouldRender</c> to compile.</summary>
    public static bool IsDrawSuppressed(IRenderGated gated) => gated.ShouldRender is null || !gated.ShouldRender();
}
