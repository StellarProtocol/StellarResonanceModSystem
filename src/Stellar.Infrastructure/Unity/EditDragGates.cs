namespace Stellar.Infrastructure.Unity;

/// <summary>Shared flag: true while layout edit-mode (Shift+`) is active. Set by LayoutEditorOverlay,
/// read by WindowInteractionTicker to gate edit-only window drag (overlay/status windows move only in
/// edit mode; popup dialogs drag freely).</summary>
internal static class LayoutEditGate { public static volatile bool IsEditing; }

/// <summary>
/// Mutual-exclusion between the TWO independent pointer-drag pollers active in edit mode — the layout editor
/// (LayoutEditorOverlay, on the framework tick, drags native game-UI + mod HUDs) and the per-frame
/// WindowInteractionTicker MonoBehaviour (drags mod windows). Without it, a single press over overlapping
/// elements could be claimed by BOTH, moving two things at once (the "dragging moves two" bug). Whoever claims
/// a press first sets its flag; the other yields. Both flags reset on pointer release.
/// </summary>
internal static class EditDragArbiter
{
    public static volatile bool WindowDragActive;   // the uGUI window ticker owns this press
    public static volatile bool EditorDragActive;   // the layout editor owns this press (native/HUD grab)
}
