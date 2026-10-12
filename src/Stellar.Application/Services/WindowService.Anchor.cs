using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>Anchor-relative save / restore (<see cref="AnchoredPlacement"/>, framework 2.22.0): a window anchored to a
/// canvas point saves its offset from that point beside the absolute rect, and every restore re-places it from the
/// offset with the CURRENT canvas and width — centred stays centred at every resolution and after a width change.</summary>
internal sealed partial class WindowService
{
    private bool TryCanvas(out CanvasDims canvas)
    {
        canvas = default;
        var sf = CanvasScale;
        var res = _resolution?.Invoke() ?? default;
        if (sf <= 0f || res.Width <= 0 || res.Height <= 0) return false;
        var u = (_renderer as Stellar.Application.Abstractions.IWindowCanvasMetrics)?.UiScale ?? 1f;
        canvas = new CanvasDims(res.Width / sf, res.Height / sf, u > 0f ? u : 1f);
        return true;
    }

    /// <summary>Every save of a window's own rect goes through here: anchored windows also store their offset.</summary>
    private void SaveRect(WindowSpec spec, WindowRect rect, bool visible)
    {
        if (_storage is null || _resolution is null) return;
        var res = _resolution();
        if (spec.Anchor != WindowAnchor.TopLeft && TryCanvas(out var canvas))
            _storage.Save(_storage.ActiveSlot, spec.Id, res,
                new LayoutStorage.WindowState(rect, visible, AnchoredPlacement.OffsetOf(spec.Anchor, rect, canvas)));
        else
            _storage.Save(_storage.ActiveSlot, spec.Id, res, rect, visible);
    }

    /// <summary>The restored rect for an anchored window: from its saved offset (any resolution), or — for a legacy
    /// exact-resolution save — from the anchor point that legacy rect had, either way at the window's CURRENT size
    /// (a fixed-size window's spec size; a resizable one keeps its saved size). A legacy save reused from another
    /// resolution keeps the old absolute placement.</summary>
    private WindowRect Reanchor(WindowSpec spec, WindowRect saved)
    {
        if (spec.Anchor == WindowAnchor.TopLeft || _storage is null || _resolution is null || !TryCanvas(out var canvas)) return saved;
        var size = CurrentSize(spec, saved);
        if (_storage.TryGetAnchorOffset(_storage.ActiveSlot, spec.Id, _resolution(), out var offset, out var legacyExact))
            return Clamp(AnchoredPlacement.Place(spec.Anchor, offset, size, canvas), canvas);
        if (!legacyExact) return saved;
        return Clamp(AnchoredPlacement.Place(spec.Anchor, AnchoredPlacement.OffsetOf(spec.Anchor, saved, canvas), size, canvas), canvas);
    }

    private static (float Width, float Height) CurrentSize(WindowSpec spec, WindowRect saved)
    {
        if (spec.Resizable) return (saved.Width, saved.Height);
        var d = spec.DefaultRect;
        return (d.Width > 0f ? d.Width : saved.Width, d.Height > 0f ? d.Height : saved.Height);
    }

    private static WindowRect Clamp(WindowRect rect, CanvasDims canvas) =>
        LayoutStorage.ClampVisible(rect, new Resolution((int)System.Math.Round(canvas.Width), (int)System.Math.Round(canvas.Height)));
}
