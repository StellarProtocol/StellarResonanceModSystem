using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Edit-mode state machine driving the layout-editor overlay. Holds
/// <see cref="IsEditing"/>, <see cref="SelectedWindowId"/>, and the current
/// drag origin. The overlay calls <see cref="BeginDrag"/>, <see cref="UpdateDrag"/>,
/// and <see cref="EndDrag"/> as the user moves a window; the service returns
/// snapped rects but does not draw anything itself.
///
/// Snap candidates per axis: 5 screen-edge fractions (0/25/50/75/100%) plus
/// every other window's left/right/top/bottom edges. Threshold = 6 px.
/// </summary>
internal sealed class LayoutEditorService
{
    private static readonly float[] ScreenFractions = { 0f, 0.25f, 0.5f, 0.75f, 1f };

    private readonly LayoutStorage _storage;
    private readonly IPluginLog _log;

    private float _dragStartPointerX;
    private float _dragStartPointerY;
    private WindowRect _dragStartRect;

    public LayoutEditorService(LayoutStorage storage, IPluginLog log)
    {
        _storage = storage;
        _log = log;
    }

    public bool    IsEditing        { get; private set; }
    public bool    IsDragging       { get; private set; }
    public string? SelectedWindowId { get; private set; }

    /// <summary>Fired after <see cref="ToggleEditMode"/> mutates <see cref="IsEditing"/>.</summary>
    public event Action? IsEditingChanged;

    public void ToggleEditMode()
    {
        IsEditing = !IsEditing;
        if (!IsEditing)
        {
            SelectedWindowId = null;
            IsDragging = false;
        }
        _log.Info($"[LayoutEditor] edit mode {(IsEditing ? "ON" : "OFF")}");
        IsEditingChanged?.Invoke();
    }

    /// <summary>External writer for the Layout panel — sets selection without entering edit mode.</summary>
    public void SetSelectedWindow(string? windowId) => SelectedWindowId = windowId;

    public void SelectWindow(string windowId)
    {
        if (!IsEditing) return;
        SelectedWindowId = windowId;
    }

    public void BeginDrag(float startPointerX, float startPointerY, WindowRect startRect)
    {
        if (!IsEditing || SelectedWindowId is null) return;
        IsDragging = true;
        _dragStartPointerX = startPointerX;
        _dragStartPointerY = startPointerY;
        _dragStartRect = startRect;
    }

    public WindowRect UpdateDrag(float pointerX, float pointerY, IReadOnlyList<WindowRect> otherWindows,
                                  int screenWidth, int screenHeight)
    {
        if (!IsDragging) return _dragStartRect;

        var dx = pointerX - _dragStartPointerX;
        var dy = pointerY - _dragStartPointerY;
        var moved = new WindowRect(_dragStartRect.X + dx, _dragStartRect.Y + dy,
                             _dragStartRect.Width, _dragStartRect.Height);
        return ApplySnap(moved, otherWindows, screenWidth, screenHeight);
    }

    public void EndDrag(WindowRect finalRect, Resolution currentResolution)
    {
        if (!IsDragging || SelectedWindowId is null) return;
        _storage.Save(_storage.ActiveSlot, SelectedWindowId, currentResolution, finalRect, visible: true);
        IsDragging = false;
    }

    private WindowRect ApplySnap(WindowRect rect, IReadOnlyList<WindowRect> others, int screenW, int screenH)
    {
        if (!_storage.SnapEnabled) return rect;
        var threshold = _storage.SnapThresholdPx;
        var state = new SnapState(rect.X, rect.Y, threshold + 1f, threshold + 1f);
        SnapToScreenEdges(rect, screenW, screenH, ref state);
        SnapToOtherWindows(rect, others, ref state);
        return new WindowRect(state.SnappedX, state.SnappedY, rect.Width, rect.Height);
    }

    private static void SnapToScreenEdges(WindowRect rect, int screenW, int screenH, ref SnapState state)
    {
        foreach (var f in ScreenFractions)
        {
            var targetX = screenW * f;
            ConsiderX(rect.X, targetX, ref state);
            ConsiderX(rect.Right, targetX, refOffset: rect.Width, ref state);
            var targetY = screenH * f;
            ConsiderY(rect.Y, targetY, ref state);
            ConsiderY(rect.Bottom, targetY, refOffset: rect.Height, ref state);
        }
    }

    private static void SnapToOtherWindows(WindowRect rect, IReadOnlyList<WindowRect> others, ref SnapState state)
    {
        foreach (var other in others)
        {
            ConsiderX(rect.X, other.Right, ref state);
            ConsiderX(rect.Right, other.X, refOffset: rect.Width, ref state);
            ConsiderX(rect.X, other.X, ref state);
            ConsiderY(rect.Y, other.Bottom, ref state);
            ConsiderY(rect.Bottom, other.Y, refOffset: rect.Height, ref state);
            ConsiderY(rect.Y, other.Y, ref state);
        }
    }

    private static void ConsiderX(float source, float target, ref SnapState state)
    {
        var d = Math.Abs(source - target);
        if (d < state.BestDxDist) { state.BestDxDist = d; state.SnappedX = target; }
    }

    /// <summary>Variant for "right-edge to target" snaps: snap source X = target - width.</summary>
    private static void ConsiderX(float source, float target, float refOffset, ref SnapState state)
    {
        var d = Math.Abs(source - target);
        if (d < state.BestDxDist) { state.BestDxDist = d; state.SnappedX = target - refOffset; }
    }

    private static void ConsiderY(float source, float target, ref SnapState state)
    {
        var d = Math.Abs(source - target);
        if (d < state.BestDyDist) { state.BestDyDist = d; state.SnappedY = target; }
    }

    private static void ConsiderY(float source, float target, float refOffset, ref SnapState state)
    {
        var d = Math.Abs(source - target);
        if (d < state.BestDyDist) { state.BestDyDist = d; state.SnappedY = target - refOffset; }
    }

    private struct SnapState
    {
        public float SnappedX;
        public float SnappedY;
        public float BestDxDist;
        public float BestDyDist;
        public SnapState(float x, float y, float dx, float dy)
        { SnappedX = x; SnappedY = y; BestDxDist = dx; BestDyDist = dy; }
    }
}
