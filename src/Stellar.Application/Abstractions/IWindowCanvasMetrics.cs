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
}
