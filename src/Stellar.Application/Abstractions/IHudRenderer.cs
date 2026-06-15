using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound port implemented in Infrastructure (HudRenderer; InternalsVisibleTo grants access).
/// Builds uGUI from a HUD element tree on the Stellar HUD canvas. Token is opaque to Application.</summary>
internal interface IHudRenderer
{
    bool IsAnchorAvailable(HudAnchor anchor);
    object? Mount(HudSpec spec);
    bool IsAlive(object? token);
    /// <summary>Re-pull the tree's Funcs into the live uGUI, applying only changed values (in place). When
    /// <paramref name="hide"/> the root is deactivated (auto-hide behind a full-screen game menu) and the value
    /// pull is skipped — the service computes <paramref name="hide"/> from the policy.</summary>
    void ApplyValues(object? token, HudSpec spec, bool hide);
    void SetRect(object? token, WindowRect rect);
    WindowRect GetRect(object? token);
    void Destroy(object? token);
    /// <summary>Advance bar smoothing one tick (dt = seconds since last tick). Driven from
    /// HudService.Tick on the throttled framework ticker, so the HUD has no per-frame
    /// injected-MonoBehaviour Update (which would reinstate the ~12-18 fps managed-crossing tax).</summary>
    void TickAnimations(float dt);
}
