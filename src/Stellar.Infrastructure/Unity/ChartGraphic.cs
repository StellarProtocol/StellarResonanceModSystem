using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// uGUI mesh renderer for the line chart's plotted lines, axes, and gridlines. A
/// <see cref="MaskableGraphic"/> whose <see cref="OnPopulateMesh"/> emits one thin quad (two triangles)
/// per line segment. The spike (2026-06-16) confirmed native→managed <c>OnPopulateMesh</c> dispatch works
/// for a <c>ClassInjector</c>-injected graphic under Il2CppInterop 1.5.1; <c>WindowRenderer.EnsureCanvas</c>
/// registers the type once before any <c>AddComponent&lt;ChartGraphic&gt;</c> runs. Contains NO
/// <c>ClassInjector</c> reference itself, so it also compiles + renders natively in the Mono UI sandbox.
///
/// Data is pushed in via <see cref="SetData"/> (already mapped to local pixel space by the builder); the
/// graphic only triangulates. uGUI calls <c>OnPopulateMesh</c> on mount and on <c>SetVerticesDirty()</c> —
/// the builder dirties only when the series/visible-range changed, so static archived data is cheap.
/// </summary>
internal sealed class ChartGraphic : MaskableGraphic
{
    // The IntPtr ctor is required by Il2CppInterop for injected managed UnityEngine.Object subclasses in the
    // game build. Stock Mono uGUI (the UI sandbox) has no such base ctor — guard it the same way as the
    // OnPopulateMesh access modifier so the sandbox uses the implicit default ctor.
#if !UNITY_5_3_OR_NEWER
    public ChartGraphic(IntPtr ptr) : base(ptr) { }
#endif

    // One plotted polyline: the local-pixel points (bottom-left origin), colour, and half-thickness in px.
    internal readonly struct Polyline
    {
        public Polyline(IReadOnlyList<Vector2> points, Color32 color, float thickness)
        {
            Points = points; Color = color; Thickness = thickness;
        }

        public IReadOnlyList<Vector2> Points { get; }
        public Color32 Color { get; }
        public float Thickness { get; }
    }

    // One straight segment (axes + gridlines) in local-pixel space.
    internal readonly struct Segment
    {
        public Segment(Vector2 a, Vector2 b, Color32 color, float thickness)
        {
            A = a; B = b; Color = color; Thickness = thickness;
        }

        public Vector2 A { get; }
        public Vector2 B { get; }
        public Color32 Color { get; }
        public float Thickness { get; }
    }

    // One filled area: the polyline's local-pixel points (bottom-left origin) closed down to a baseline Y,
    // filled with a flat colour. Drawn under the line for the navigator's overview shape.
    internal readonly struct Fill
    {
        public Fill(IReadOnlyList<Vector2> points, float baselineY, Color32 color)
        {
            Points = points; BaselineY = baselineY; Color = color;
        }

        public IReadOnlyList<Vector2> Points { get; }
        public float BaselineY { get; }
        public Color32 Color { get; }
    }

    private IReadOnlyList<Polyline> _polylines = Array.Empty<Polyline>();
    private IReadOnlyList<Segment> _segments = Array.Empty<Segment>();
    private IReadOnlyList<Fill> _fills = Array.Empty<Fill>();

    // Push freshly-mapped geometry, then flag the mesh dirty so uGUI re-runs OnPopulateMesh next render.
    internal void SetData(IReadOnlyList<Segment> segments, IReadOnlyList<Polyline> polylines)
        => SetData(segments, polylines, Array.Empty<Fill>());

    // Overload with filled areas (drawn first, under the lines/segments) — used by the navigator overview.
    internal void SetData(IReadOnlyList<Segment> segments, IReadOnlyList<Polyline> polylines, IReadOnlyList<Fill> fills)
    {
        _segments = segments ?? Array.Empty<Segment>();
        _polylines = polylines ?? Array.Empty<Polyline>();
        _fills = fills ?? Array.Empty<Fill>();
        SetVerticesDirty();
    }

    // Access-modifier divergence: the IL2CPP UnityEngine.UI.Graphic declares OnPopulateMesh PUBLIC (so the
    // game build needs `public override` → CS0507 if protected), but stock Mono uGUI (the UI sandbox player)
    // declares it PROTECTED (→ CS0507 if public). UNITY_5_3_OR_NEWER is defined by every Unity compiler and
    // never by the plain-dotnet game DLL build, so it cleanly selects the sandbox's `protected` variant.
    // Origin is bottom-left of the pixel-adjusted rect; the builder maps points into that same space.
#if UNITY_5_3_OR_NEWER
    protected override void OnPopulateMesh(VertexHelper vh)
#else
    public override void OnPopulateMesh(VertexHelper vh)
#endif
    {
        vh.Clear();
        var r = GetPixelAdjustedRect();
        var origin = new Vector2(r.xMin, r.yMin);
        for (var i = 0; i < _fills.Count; i++)
        {
            var f = _fills[i];
            AddFill(vh, origin, f.Points, origin.y + f.BaselineY, f.Color);
        }
        for (var i = 0; i < _segments.Count; i++)
        {
            var s = _segments[i];
            AddLine(vh, origin + s.A, origin + s.B, s.Thickness, s.Color);
        }
        for (var i = 0; i < _polylines.Count; i++)
        {
            var pl = _polylines[i];
            for (var j = 1; j < pl.Points.Count; j++)
                AddLine(vh, origin + pl.Points[j - 1], origin + pl.Points[j], pl.Thickness, pl.Color);
        }
    }

    // Emit a filled area between the polyline and a flat baseline Y: one quad per point-pair, each spanning
    // [pt[j-1], pt[j]] across the top and the baseline across the bottom. Points are already origin-relative;
    // baselineY is already absolute (origin-shifted by the caller). Degenerate (<2 pts) draws nothing.
    private static void AddFill(VertexHelper vh, Vector2 origin, IReadOnlyList<Vector2> pts, float baselineY, Color32 c)
    {
        for (var j = 1; j < pts.Count; j++)
        {
            var a = origin + pts[j - 1];
            var b = origin + pts[j];
            var i0 = vh.currentVertCount;
            vh.AddVert(new Vector3(a.x, baselineY), c, Vector2.zero);   // bottom-left
            vh.AddVert(new Vector3(a.x, a.y), c, Vector2.zero);        // top-left
            vh.AddVert(new Vector3(b.x, b.y), c, Vector2.zero);        // top-right
            vh.AddVert(new Vector3(b.x, baselineY), c, Vector2.zero);   // bottom-right
            vh.AddTriangle(i0, i0 + 1, i0 + 2);
            vh.AddTriangle(i0 + 2, i0 + 3, i0);
        }
    }

    // Emit a thickness-wide quad (two triangles) for the segment a→b, offsetting by the unit normal × half.
    private static void AddLine(VertexHelper vh, Vector2 a, Vector2 b, float halfWidth, Color32 c)
    {
        var dir = b - a;
        var len = dir.magnitude;
        if (len < 0.0001f) return;
        var n = new Vector2(-dir.y, dir.x) / len * Mathf.Max(halfWidth, 0.5f);
        var i0 = vh.currentVertCount;
        vh.AddVert(new Vector3(a.x - n.x, a.y - n.y), c, Vector2.zero);
        vh.AddVert(new Vector3(a.x + n.x, a.y + n.y), c, Vector2.zero);
        vh.AddVert(new Vector3(b.x + n.x, b.y + n.y), c, Vector2.zero);
        vh.AddVert(new Vector3(b.x - n.x, b.y - n.y), c, Vector2.zero);
        vh.AddTriangle(i0, i0 + 1, i0 + 2);
        vh.AddTriangle(i0 + 2, i0 + 3, i0);
    }
}
