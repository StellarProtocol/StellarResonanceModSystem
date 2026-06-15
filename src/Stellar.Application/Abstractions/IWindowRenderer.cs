using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound port implemented in Infrastructure (WindowRenderer; InternalsVisibleTo grants access).
/// Builds uGUI from a window element tree on the Stellar window canvas. Token is opaque to Application.</summary>
internal interface IWindowRenderer
{
    bool IsCanvasAvailable();
    object? Mount(WindowRegistration reg);
    bool IsAlive(object? token);
    /// <summary>Re-pull the tree's display Funcs into the live uGUI, applying only changed values. When
    /// <paramref name="hide"/> the root is deactivated (auto-hide behind a full-screen game menu / hide-until-
    /// in-world) and the value pull is skipped — the service computes <paramref name="hide"/> from the policy.</summary>
    void ApplyValues(object? token, WindowRegistration reg, bool hide);
    void SetRect(object? token, WindowRect rect);
    WindowRect GetRect(object? token);
    /// <summary>True if any InputElement in this window currently holds keyboard focus (Plan 3 wires the
    /// keyboard gate from this; in Plan 1 the renderer always returns false).</summary>
    bool HasFocusedField(object? token);
    void Destroy(object? token);
}
