using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// "Live canvas": the framework's known hot elements — a CombatMeter row (bar fill, values, share %, rank, HP spine,
// debuff/imagine timers), a BarElement (fill + label), a CooldownTile (foot fill + seconds) — carry their OWN nested
// Canvas, so a value change re-batches only that element instead of the whole window (raid 11-10: ~108 changed
// graphics per frame in a boss fight, each dirtying its window's canvas; CanvasUpdateRegistry.PerformUpdate ≈ 10% of
// the main thread). Same technique as the sheen band (WindowBuilder.MeterRow.Sheen.cs) and IsolateCanvas windows:
//  • overrideSorting stays FALSE — the canvas inherits the parent's sorting and draws in hierarchy order, so stacking,
//    BringToFront and FrontWindowBlocks (all sibling-index based) are untouched;
//  • MaskUtilities only drops a parent RectMask2D across a canvas with overrideSorting=TRUE, so a list viewport's clip
//    still clips and culls the element (validated in game for the sheen 11-09);
//  • pixelPerfect is inherited (no overridePixelPerfect), so the ticker's root flip during a drag/scroll still covers it.
// Granularity: ONE canvas per element (row / bar / tile), never per graphic — each nested canvas is a draw-call batch
// break, so a 20-row meter adds ~20 batch groups. The per-window "[UiPerf.Queues] top windows by changed graphics"
// line is how to judge whether that trade pays for a given window.
internal sealed partial class WindowBuilder
{
    // interactive: the subtree holds EventSystem-driven widgets (Button) → it needs its OWN GraphicRaycaster: the parent
    // raycaster only tests graphics registered to ITS canvas (GraphicRegistry is per canvas), so without one a Button
    // under the nested canvas silently stops receiving clicks. Display-only subtrees skip it (a raycaster per element is
    // a per-frame EventSystem cost).
    //
    // Not inside a KeepAlive branch: a parked tab / soft-parked window hides itself by DISABLING its own Canvas
    // (WindowBuilder.KeepAlive.cs, WindowRenderer.SoftPark.cs) and expects nothing under it to keep drawing; a nested
    // canvas there is left out rather than relying on how a disabled parent canvas treats an enabled child canvas.
    private void AddLiveCanvas(GameObject go, bool interactive)
    {
        if (_alive != null || go == null) return;
        go.AddComponent<Canvas>();   // batching boundary only — overrideSorting left false (see above)
        if (interactive) go.AddComponent<GraphicRaycaster>();
    }
}
