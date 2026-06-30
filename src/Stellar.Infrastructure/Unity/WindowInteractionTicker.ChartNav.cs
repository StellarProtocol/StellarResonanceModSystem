using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Line-chart NAVIGATOR-brush gesture handling for <see cref="WindowInteractionTicker"/> (Part 4). A press on
/// a brush edge handle resizes/zooms that edge; a press on the brush body pans the window; a double-click on
/// the navigator resets to the full range. All map through the navigator's full extent (cursor X → time over
/// [0,total]) and clamp via the pure <see cref="ChartWindow"/> helper, writing the plugin-owned visible range
/// — which the renderer reflects back onto the brush each refresh (one-way: range → brush geometry, no
/// feedback). Split out of the main ticker file to keep it under the 500-LoC gate; mirrors the ChartPan
/// claim/tick/release pattern. Registered via <c>WindowBuilder.RegisterChartNav</c>; absent in the Mono
/// sandbox (the brush then renders statically at the current range).
/// </summary>
public sealed partial class WindowInteractionTicker
{
    // Navigator-brush gesture state: window lives in the plugin (Get/Set); mapping goes through the full
    // extent (Total) so the brush spans [0,total]. Mode is decided at mouse-down by which sub-rect was hit.
    internal readonly List<ChartNav> ChartNavs = new();
    internal struct ChartNav
    {
        public RectTransform Nav;                        // navigator hit-rect (cursor X → time over [0,total])
        public RectTransform Left;                       // left resize handle
        public RectTransform Right;                      // right resize handle
        public RectTransform Body;                       // draggable middle (pan)
        public Func<(float Min, float Max)> Get;
        public Action<(float Min, float Max)> Set;
        public Func<float> Total;
        public Func<float> MinSpan;
    }

    private enum NavGrab { None, Left, Right, Body }
    private int _activeChartNav = -1;                    // index into ChartNavs of the dragged brush, or -1
    private NavGrab _navGrab = NavGrab.None;             // which part of that brush was grabbed
    private float _navBodyOffset;                        // (cursor time − window left edge) captured at body grab
    private const float NavDoubleClickSec = 0.3f;        // double-click window for reset-to-full
    private float _navLastClick = -1f;

    private void PruneChartNavs()
    {
        for (var i = ChartNavs.Count - 1; i >= 0; i--) if (ChartNavs[i].Nav == null) ChartNavs.RemoveAt(i);
    }

    // Mouse-down hit-test: handles first (resize), then body (pan), then a bare-navigator press records a
    // potential double-click (handled on the SECOND press). Returns the index, sets _navGrab, or -1.
    internal int HitChartNav(Vector3 mp)
    {
        for (var i = 0; i < ChartNavs.Count; i++)
        {
            var cn = ChartNavs[i];
            if (cn.Nav == null || !cn.Nav.gameObject.activeInHierarchy) continue;
            if (!Contains(cn.Nav, mp)) continue;
            if (FrontWindowBlocks(mp, FindWindowRoot(cn.Nav))) continue;
            if (Contains(cn.Left, mp)) { _navGrab = NavGrab.Left; return i; }
            if (Contains(cn.Right, mp)) { _navGrab = NavGrab.Right; return i; }
            if (Contains(cn.Body, mp)) { return BeginBodyOrReset(i, cn, mp); }
            TryNavReset(cn);   // bare-strip click → maybe double-click reset
            return -1;
        }
        _navGrab = NavGrab.None;
        return -1;
    }

    // A body press both starts a pan AND counts toward a double-click reset (the brush usually covers most of
    // the strip, so the user double-clicks ON the brush). On the 2nd quick click we reset and claim nothing.
    private int BeginBodyOrReset(int i, ChartNav cn, Vector3 mp)
    {
        var now = Time.realtimeSinceStartup;
        if (_navLastClick >= 0f && now - _navLastClick < NavDoubleClickSec)
        {
            _navLastClick = -1f;
            try { cn.Set(ChartWindow.Full(cn.Total())); } catch { }
            _navGrab = NavGrab.None;
            return -1;
        }
        _navLastClick = now;
        _navGrab = NavGrab.Body;
        _navBodyOffset = CursorTime(cn, mp) - cn.Get().Min;   // keep this cursor-to-left-edge offset while panning
        return i;
    }

    private void TryNavReset(ChartNav cn)
    {
        var now = Time.realtimeSinceStartup;
        if (_navLastClick >= 0f && now - _navLastClick < NavDoubleClickSec)
        {
            _navLastClick = -1f;
            try { cn.Set(ChartWindow.Full(cn.Total())); } catch { }
        }
        else _navLastClick = now;
    }

    // Mouse-held: drive the claimed brush gesture (resize an edge, or pan the body).
    private void TickChartNavDrag()
    {
        if (_activeChartNav < 0 || _activeChartNav >= ChartNavs.Count) { _activeChartNav = -1; return; }
        var cn = ChartNavs[_activeChartNav];
        if (cn.Nav == null) { _activeChartNav = -1; return; }
        var t = CursorTime(cn, Input.mousePosition);
        try
        {
            var w = cn.Get();
            var total = cn.Total();
            var minSpan = cn.MinSpan();
            cn.Set(_navGrab switch
            {
                NavGrab.Left => ChartWindow.Clamp((t, w.Max), total, minSpan),
                NavGrab.Right => ChartWindow.Clamp((w.Min, t), total, minSpan),
                // Pan so the left edge tracks (cursor time − the captured grab offset): delta = target − current.
                NavGrab.Body => ChartWindow.Pan(w, (t - _navBodyOffset) - w.Min, total, minSpan),
                _ => w,
            });
        }
        catch { }
    }

    // Cursor's time on [0,total]: its X fraction across the navigator hit-rect, scaled by the full extent.
    private static float CursorTime(ChartNav cn, Vector3 mp)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(cn.Nav, mp, null, out var lp)) return 0f;
        var r = cn.Nav.rect;
        return ChartGeometry.NavXToTime(lp.x, cn.Total(), r.xMin, r.xMax);
    }

    private static bool Contains(RectTransform? rt, Vector3 mp)
        => rt != null && rt.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rt, mp, null);
}
