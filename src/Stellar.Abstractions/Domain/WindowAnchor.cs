namespace Stellar.Abstractions.Domain;

/// <summary>Which point of the window overlay a <see cref="WindowSpec.DefaultRect"/> is anchored to on first
/// placement. TopLeft (default) = DefaultRect.X/Y is the absolute top-left position (legacy behaviour). Any other
/// value places the window's matching point at that point of the canvas, with DefaultRect.X/Y as a canvas-unit
/// OFFSET from it (Center + offset (0,0) = dead centre). The framework resolves this in canvas units, so plugins
/// never need the CanvasScaler scaleFactor.</summary>
public enum WindowAnchor
{
    /// <summary>Top-left corner (default). <see cref="WindowSpec.DefaultRect"/>.X/Y is the absolute top-left position (legacy behaviour).</summary>
    TopLeft = 0,
    /// <summary>Top edge, horizontally centred. DefaultRect.X/Y is a canvas-unit offset from that point.</summary>
    Top,
    /// <summary>Top-right corner. DefaultRect.X/Y is a canvas-unit offset from that point.</summary>
    TopRight,
    /// <summary>Left edge, vertically centred. DefaultRect.X/Y is a canvas-unit offset from that point.</summary>
    Left,
    /// <summary>Dead centre of the canvas. DefaultRect.X/Y is a canvas-unit offset from that point (offset (0,0) = dead centre).</summary>
    Center,
    /// <summary>Right edge, vertically centred. DefaultRect.X/Y is a canvas-unit offset from that point.</summary>
    Right,
    /// <summary>Bottom-left corner. DefaultRect.X/Y is a canvas-unit offset from that point.</summary>
    BottomLeft,
    /// <summary>Bottom edge, horizontally centred. DefaultRect.X/Y is a canvas-unit offset from that point.</summary>
    Bottom,
    /// <summary>Bottom-right corner. DefaultRect.X/Y is a canvas-unit offset from that point.</summary>
    BottomRight,
}
