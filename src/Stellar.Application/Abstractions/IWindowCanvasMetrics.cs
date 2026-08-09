namespace Stellar.Application.Abstractions;

/// <summary>Live window-canvas metrics for the layout editor. Separate from IWindowRenderer so that interface
/// stays at the STELLAR0005 member cap. The window overlay canvas may carry a CanvasScaler, so GetRect/SetRect
/// speak CANVAS UNITS; the editor is uniformly SCREEN PIXELS, and converts window rects by this factor.</summary>
internal interface IWindowCanvasMetrics
{
    /// <summary>Window overlay canvas scaleFactor (screen px per canvas unit). 1.0 when unscaled.</summary>
    float CanvasScale { get; }

    /// <summary>The UI-Scale slider value (1.0 = default). scaleFactor = resolutionScale × UiScale; default
    /// window positions divide by this so the slider grows windows in place instead of moving them.</summary>
    float UiScale { get; }

    /// <summary>True once the CanvasScaler has settled a real <c>scaleFactor</c> since the last canvas (re)create.
    /// A freshly-added scaler reports the DEFAULT 1.0 on its create frame (its Handle runs in willRenderCanvases,
    /// end of frame), so callers must NOT clamp against <see cref="CanvasScale"/> until this is true — else a
    /// scene-change remount clamps saved rects against a too-small bound and snaps windows toward the origin.</summary>
    bool CanvasScaleReady { get; }

    /// <summary>Monotonic counter bumped on every window-canvas (re)create (scene-change self-heal). A change tells
    /// the layout host to fire one corrective reapply once <see cref="CanvasScaleReady"/> is true.</summary>
    int CanvasGeneration { get; }
}
