using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// uGUI mesh renderer for the line chart's plotted lines, axes, and gridlines. A
/// <see cref="MaskableGraphic"/> whose <see cref="OnPopulateMesh"/> emits one thin quad (two triangles)
/// per line segment, plus a feathered round-join disc at every polyline vertex. The spike (2026-06-16)
/// confirmed native→managed <c>OnPopulateMesh</c> dispatch works for a <c>ClassInjector</c>-injected graphic
/// under Il2CppInterop 1.5.1; <c>WindowRenderer.EnsureCanvas</c> registers the type once before any
/// <c>AddComponent&lt;ChartGraphic&gt;</c> runs. Contains NO <c>ClassInjector</c> reference itself, so it also
/// compiles + renders natively in the Mono UI sandbox.
///
/// Everything is emitted as mesh — there is NO child component creation and NO texture bake. The earlier
/// supersampled-AA raster line layer (a child <see cref="RawImage"/> + baked <c>Texture2D</c>) created a
/// child component from within the render/SetData path, which threw under IL2CPP (the generic
/// <c>GameObject.AddComponent&lt;RawImage&gt;</c> failed to resolve via Il2CppInterop) and froze the game.
/// The texture-AA approach is IL2CPP-incompatible; the all-mesh round-join stroke below mounts cleanly.
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
            AddPolyline(vh, origin, pl.Points, pl.Thickness, pl.Color);
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

    // AA ramp width (per side, px) for every stroke — the diagonal plotted polylines (main + navigator) AND the
    // straight axis/grid runs. A moderate feather is the compromise banked on this branch: 0.5px read jagged on
    // shallow diagonals, 1.75px read too thick, so ~0.9px gives a thin core flanked by a soft-but-tight AA band.
    // Per side, in px; the data points are never interpolated — only the stroke rendering is softened. Axes are
    // pixel-aligned straight runs so this same width keeps them crisp enough while sharing one stroke emitter.
    private const float Feather = 0.9f;

    // Round-join disc resolution: each interior vertex gets a feathered disc of this many fan wedges. A
    // ROUND join (SVG stroke-linejoin:round) — the disc both closes the per-segment quad gap at the joint
    // AND rounds the corner to radius = core half-width, so peaks/valleys read smoothly curved, not pointed.
    private const int RoundJoinSegments = 12;

    // Emit a ROUND-joined stroke for a polyline. Each segment is its own feathered quad (segment normals, no
    // miter spike — so no sharp apex); every interior vertex then gets a feathered disc of radius = core
    // half-width, which both fills the per-segment joint gap and rounds the corner. Because the disc radius
    // equals the line's half-width, straight runs gain no extra width (no beading); only true corners round.
    // Endpoints get the same disc so the stroke terminates with a small round cap rather than a blunt chop.
    private static void AddPolyline(VertexHelper vh, Vector2 origin, IReadOnlyList<Vector2> pts, float halfWidth, Color32 c)
    {
        var n = pts.Count;
        if (n < 2) return;
        var hc = Mathf.Max(halfWidth, 0.25f);                 // full-opacity core half-width
        for (var i = 1; i < n; i++)
            AddLine(vh, origin + pts[i - 1], origin + pts[i], hc, c);   // per-segment feathered quad
        for (var i = 0; i < n; i++)
            RoundCap(vh, origin + pts[i], hc, c);             // round every joint + both endpoints
    }

    // A feathered disc centred at p with full-opacity core radius hc, fading to α0 over Feather px. Drawn
    // at every polyline vertex: at interior joints it bridges the two adjacent segment quads with a rounded
    // corner; at endpoints it forms a round cap. Radius equals the line half-width, so on a straight run the
    // disc stays inside the stroke's edges and adds no visible width — corners round without the line beading.
    private static void RoundCap(VertexHelper vh, Vector2 p, float hc, Color32 c)
    {
        var clear = new Color32(c.r, c.g, c.b, 0);
        var feathered = hc + Feather;
        var start = new Vector2(0f, 1f);                      // arbitrary first spoke; the disc is symmetric
        var step = (2f * Mathf.PI) / RoundJoinSegments;
        for (var k = 0; k < RoundJoinSegments; k++)
        {
            var r0 = Rotate(start, step * k);
            var r1 = Rotate(start, step * (k + 1));
            var i0 = vh.currentVertCount;
            vh.AddVert(new Vector3(p.x, p.y), c, Vector2.zero);                                            // hub (full)
            vh.AddVert(new Vector3(p.x + r0.x * hc, p.y + r0.y * hc), c, Vector2.zero);                    // core edge
            vh.AddVert(new Vector3(p.x + r1.x * hc, p.y + r1.y * hc), c, Vector2.zero);
            vh.AddVert(new Vector3(p.x + r0.x * feathered, p.y + r0.y * feathered), clear, Vector2.zero);  // outer feather α0
            vh.AddVert(new Vector3(p.x + r1.x * feathered, p.y + r1.y * feathered), clear, Vector2.zero);
            vh.AddTriangle(i0, i0 + 1, i0 + 2);               // core wedge
            AddBand(vh, i0 + 1, i0 + 3, i0 + 2, i0 + 4);      // feather ring: full → α0
        }
    }

    // Rotate a 2-D vector by the given angle (radians) about the origin.
    private static Vector2 Rotate(Vector2 v, float a)
    {
        var cos = Mathf.Cos(a);
        var sin = Mathf.Sin(a);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }

    // Emit a feathered (alpha-ramped) stroke for the straight segment a→b. Used for both the axis/grid runs
    // and each per-segment quad of a polyline; both share the single Feather width.
    private static void AddLine(VertexHelper vh, Vector2 a, Vector2 b, float halfWidth, Color32 c)
    {
        var dir = b - a;
        var len = dir.magnitude;
        if (len < 0.0001f) return;
        var unit = new Vector2(-dir.y, dir.x) / len;            // unit normal
        var hc = Mathf.Max(halfWidth, 0.25f);                   // full-opacity core half-width
        var core = unit * hc;
        var edge = unit * (hc + Feather);                       // outer (alpha-0) feather rail
        var clear = new Color32(c.r, c.g, c.b, 0);              // transparent fringe colour
        var i0 = vh.currentVertCount;
        // Four rails per endpoint, top→bottom: -edge(α0), -core(full), +core(full), +edge(α0).
        vh.AddVert(new Vector3(a.x - edge.x, a.y - edge.y), clear, Vector2.zero);   // 0
        vh.AddVert(new Vector3(a.x - core.x, a.y - core.y), c, Vector2.zero);       // 1
        vh.AddVert(new Vector3(a.x + core.x, a.y + core.y), c, Vector2.zero);       // 2
        vh.AddVert(new Vector3(a.x + edge.x, a.y + edge.y), clear, Vector2.zero);   // 3
        vh.AddVert(new Vector3(b.x - edge.x, b.y - edge.y), clear, Vector2.zero);   // 4
        vh.AddVert(new Vector3(b.x - core.x, b.y - core.y), c, Vector2.zero);       // 5
        vh.AddVert(new Vector3(b.x + core.x, b.y + core.y), c, Vector2.zero);       // 6
        vh.AddVert(new Vector3(b.x + edge.x, b.y + edge.y), clear, Vector2.zero);   // 7
        AddBand(vh, i0 + 0, i0 + 1, i0 + 4, i0 + 5);   // lower feather: α0 → full
        AddBand(vh, i0 + 1, i0 + 2, i0 + 5, i0 + 6);   // core: full → full
        AddBand(vh, i0 + 2, i0 + 3, i0 + 6, i0 + 7);   // upper feather: full → α0
    }

    // Triangulate one longitudinal band as the quad (aTop, aBot, bBot, bTop) using the four vertex indices.
    private static void AddBand(VertexHelper vh, int aTop, int aBot, int bTop, int bBot)
    {
        vh.AddTriangle(aTop, aBot, bBot);
        vh.AddTriangle(bBot, bTop, aTop);
    }
}
