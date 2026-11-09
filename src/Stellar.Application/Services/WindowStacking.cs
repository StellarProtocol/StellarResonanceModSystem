using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>A window's stacking inputs (the renderer keeps them on its token; pure data so the rule is unit-testable).
/// <paramref name="Activation"/> = the global activation counter at the window's LAST activation (first mount, a real
/// show from hidden, BringToFront, or turning Topmost on). <paramref name="Lifted"/> = a HUD window explicitly fronted
/// and not hidden since.</summary>
internal readonly record struct WindowStackKey(
    bool Popup, bool Topmost, int ZOrder, WindowCategory Category, bool Lifted, int Activation);

/// <summary>
/// The draw-order rule for Stellar windows on the shared window canvas (bottom → top). NON-STICKY: there is no
/// separate "fronted" tier any more — BringToFront just re-activates a window exactly as a fresh open would, so any
/// window opened or fronted LATER goes above it.
/// <list type="number">
/// <item>Click-away popups (<see cref="WindowSpec.DismissOnOutsideClick"/>) above everything.</item>
/// <item>Then <see cref="WindowSpec.Topmost"/> windows (pinned overlays/alerts) above every non-topmost window.</item>
/// <item>Then the plugin's explicit <see cref="WindowSpec.ZOrder"/> (higher on top).</item>
/// <item>Then the layer: Tools/Debug windows (and a HUD window lifted by BringToFront until it is hidden) above
/// plain HUD windows.</item>
/// <item>Then activation: the most recently activated window on top.</item>
/// </list>
/// Windows with an absolute <see cref="WindowSpec.OverrideSortingOrder"/> render on their own canvas and the dropdown
/// popup layer / input blockers are separate root canvases — both sit above this whole order, unaffected by it.
/// </summary>
internal static class WindowStacking
{
    /// <summary>0 = HUD layer, 1 = normal (Tools/Debug, or a lifted HUD).</summary>
    public static int Layer(in WindowStackKey k) => k.Category == WindowCategory.HUD && !k.Lifted ? 0 : 1;

    /// <summary>Negative when <paramref name="a"/> draws BELOW <paramref name="b"/> (sorted ascending → sibling order).</summary>
    public static int Compare(in WindowStackKey a, in WindowStackKey b)
    {
        if (a.Popup != b.Popup) return a.Popup ? 1 : -1;
        if (a.Topmost != b.Topmost) return a.Topmost ? 1 : -1;
        if (a.ZOrder != b.ZOrder) return a.ZOrder < b.ZOrder ? -1 : 1;
        var la = Layer(a); var lb = Layer(b);
        if (la != lb) return la - lb;
        return a.Activation == b.Activation ? 0 : a.Activation < b.Activation ? -1 : 1;
    }
}
