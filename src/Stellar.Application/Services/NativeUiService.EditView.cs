using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Whether a native element is on screen right now — drives the edit-mode live outline vs placeholder.</summary>
internal enum NativeUiPresence
{
    /// <summary>Resolved and drawn by the game: live outline at its real rect.</summary>
    Showing,
    /// <summary>Resolved but the game isn't drawing it (inactive / collapsed / CanvasGroup alpha 0): placeholder.</summary>
    HiddenByGame,
    /// <summary>Not built (boss bar outside a fight, …): placeholder.</summary>
    Absent,
}

/// <summary>What the layout editor draws for one native element: its rect and whether it is live or a placeholder.</summary>
internal readonly record struct NativeUiEditView(WindowRect Rect, NativeUiPresence Presence);

/// <summary>
/// Edit-mode half of <see cref="NativeUiService"/>: the live-or-placeholder view of each element and the pure
/// offset ⇄ screen math. A placeholder sits at the element's last-seen DEFAULT rect (persisted per resolution) plus
/// the saved offset, so it shows exactly where the element will appear; never seen → the allowlist's built-in
/// screen-fraction rect.
/// </summary>
internal sealed partial class NativeUiService
{
    private const float MeasureCacheSeconds = 0.1f;   // edit-mode outlines refresh at ≤10 Hz
    private const float ReferenceCanvasHeight = 1080f; // fallback px-per-unit guess before an element was ever measured
    private const float SeenTolerancePx = 2f;          // last-seen changes smaller than this aren't re-persisted

    /// <summary>True when the editor should offer this element. Placeholders only appear once the HUD exists (any
    /// entry resolved) — so the title screen never shows a screenful of empty boxes.</summary>
    public bool IsEditable(EntryState e) => e.IsResolved || (_anyResolved && e.Loaded);

    /// <summary>The element's live-or-placeholder view for the editor; false when it isn't editable now.</summary>
    public bool TryGetEditView(EntryState e, out NativeUiEditView view)
    {
        view = default;
        if (!IsEditable(e)) return false;
        if (e.IsResolved)
        {
            var m = MeasureCached(e);
            if (m.Showing) { view = new NativeUiEditView(m.Rect, NativeUiPresence.Showing); return true; }
            view = new NativeUiEditView(PlaceholderRect(e), NativeUiPresence.HiddenByGame);
            return true;
        }
        view = new NativeUiEditView(PlaceholderRect(e), NativeUiPresence.Absent);
        return true;
    }

    /// <summary>The entry's current editor rect (live curated rect, or the placeholder rect) — outline + hit-test.</summary>
    public WindowRect GetLiveRect(EntryState e) => TryGetEditView(e, out var v) ? v.Rect : e.Rect;

    /// <summary>Every editable native element — live AND placeholders, hidden ones included (dimmed re-enable
    /// outline). CanHide mirrors the allowlist SafeToHide flag.</summary>
    public IEnumerable<EditableElement> EditableElements()
    {
        foreach (var e in _order)
            if (TryGetEditView(e, out var v))
                yield return new EditableElement(e.Descriptor.Id, v.Rect, e.Visible, e.Descriptor.SafeToHide, Resizable: false);
    }

    private NativeUiMeasure MeasureCached(EntryState e)
    {
        if (e.HasMeasure && _clock - e.LastMeasureAt < MeasureCacheSeconds) return e.LastMeasure;
        return MeasureNow(e);
    }

    private NativeUiMeasure MeasureNow(EntryState e)
    {
        var m = _adapter.Measure(e.Handle);
        e.LastMeasure = m;
        e.LastMeasureAt = _clock;
        e.HasMeasure = true;
        if (m.Showing) { e.Rect = m.Rect; RecordSeen(e, m); }
        return m;
    }

    // Remember the element's DEFAULT geometry (live rect minus our offset) for placeholders; flag it for persisting
    // only when it moved/resized past the tolerance (FlushSeen writes at most once per measure pass).
    private void RecordSeen(EntryState e, NativeUiMeasure m)
    {
        var px = m.PxPerUnit > 0f ? m.PxPerUnit : PxPerUnit(e);
        var d = DefaultRectOf(e, m);
        var changed = !e.HasSeen
            || Math.Abs(e.Seen.X - d.X) > SeenTolerancePx || Math.Abs(e.Seen.Y - d.Y) > SeenTolerancePx
            || Math.Abs(e.Seen.W - d.Width) > SeenTolerancePx || Math.Abs(e.Seen.H - d.Height) > SeenTolerancePx;
        if (changed)
        {
            e.Seen = new SeenGeometry { X = d.X, Y = d.Y, W = d.Width, H = d.Height, PxPerUnit = px };
            e.SeenDirty = true;
        }
        e.HasSeen = true;
        e.SeenFresh = true;
    }

    // The element's default (offset-0) rect, from a live measurement at the current offset.
    private WindowRect DefaultRectOf(EntryState e, NativeUiMeasure m)
    {
        var px = m.PxPerUnit > 0f ? m.PxPerUnit : PxPerUnit(e);
        var (sx, sy) = OffsetToScreenDelta(e.OffsetX, e.OffsetY, px);
        return new WindowRect(m.Rect.X - sx, m.Rect.Y - sy, m.Rect.Width, m.Rect.Height);
    }

    private WindowRect PlaceholderRect(EntryState e)
    {
        var res = _lastResolution;
        WindowRect d;
        if (e.HasSeen) d = new WindowRect(e.Seen.X, e.Seen.Y, e.Seen.W, e.Seen.H);
        else
        {
            var f = e.Descriptor.DefaultFraction;
            d = f.Width > 0f && f.Height > 0f
                ? new WindowRect(f.X * res.Width, f.Y * res.Height, f.Width * res.Width, f.Height * res.Height)
                : new WindowRect(res.Width * 0.45f, res.Height * 0.45f, res.Width * 0.1f, res.Height * 0.1f);
        }
        var (sx, sy) = OffsetToScreenDelta(e.OffsetX, e.OffsetY, PxPerUnit(e));
        return new WindowRect(d.X + sx, d.Y + sy, d.Width, d.Height);
    }

    // Screen px per wrapper-local unit: last-seen value, else a 1080-reference-canvas guess (the game HUD canvas
    // scales with screen height; only used until the element was measured once at this resolution).
    private float PxPerUnit(EntryState e)
    {
        if (e.HasSeen && e.Seen.PxPerUnit > 0f) return e.Seen.PxPerUnit;
        return _lastResolution.Height > 0 ? _lastResolution.Height / ReferenceCanvasHeight : 1f;
    }

    /// <summary>Screen delta (px, y-DOWN) → wrapper offset delta (local units, y-UP).</summary>
    internal static (float X, float Y) ScreenDeltaToOffset(float dxPx, float dyPx, float pxPerUnit)
        => pxPerUnit > 0f ? (dxPx / pxPerUnit, -dyPx / pxPerUnit) : (0f, 0f);

    /// <summary>Wrapper offset (local units, y-UP) → screen delta (px, y-DOWN).</summary>
    internal static (float X, float Y) OffsetToScreenDelta(float ox, float oy, float pxPerUnit)
        => (ox * pxPerUnit, -oy * pxPerUnit);

    /// <summary>Legacy migration: a pre-wrapper saved curated top-left (screen px) → the wrapper offset that puts
    /// the element's DEFAULT rect (<paramref name="defaultRect"/>, measured at offset 0) there.</summary>
    internal static (float X, float Y) MigrateLegacyOffset(float savedX, float savedY, WindowRect defaultRect, float pxPerUnit)
        => ScreenDeltaToOffset(savedX - defaultRect.X, savedY - defaultRect.Y, pxPerUnit);

    // Pull a rect fully on-screen (top-left in [0, screen − size]) when it fits; larger-than-screen keeps top-left 0.
    // Same feel as the old adapter clamp — keeps a dragged element from clipping off an edge.
    private static WindowRect ClampFullyOnScreen(WindowRect r, Resolution res)
    {
        if (res.Width <= 0 || res.Height <= 0) return r;
        var x = Math.Clamp(r.X, 0f, Math.Max(0f, res.Width - r.Width));
        var y = Math.Clamp(r.Y, 0f, Math.Max(0f, res.Height - r.Height));
        return new WindowRect(x, y, r.Width, r.Height);
    }
}
