using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>Pure rule: a window/HUD is draw-suppressed purely when its plugin-owned
/// <see cref="IRenderGated.ShouldRender"/> predicate returns false. The framework enacts; the plugin decides.
/// (The <c>MasterHudKill</c> dev override is applied separately in the renderer, outside this policy.)</summary>
internal static class WindowGatingPolicy
{
    public static bool IsDrawSuppressed(IRenderGated gated) => !gated.ShouldRender();
}
