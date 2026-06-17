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

    // Anti-aliased stroke geometry. Each rib is a 4-rail cross-section (top→bottom: -edge α0, -core full,
    // +core full, +edge α0); the UI shader blends a soft alpha ramp from core→edge so the line reads crisp
    // with just enough AA. A THIN line (the prior 1.75px-per-side feather dominated and read as a ~5px blurry
    // band; here Feather is a hairline so total visual width ≈ 2·core + 2·Feather ≈ 1.5–2px). Feather is per
    // side, in px. The data points are never interpolated — only the stroke rendering is softened.
    private const float Feather = 0.5f;

    // Miter-join limit: when 1/cos(θ/2) at a vertex exceeds this (a sharp angle), the spike would shoot far
    // past the joint, so fall back to that vertex's adjacent SEGMENT normals (a bevel-ish join). Either way the
    // rib at a shared vertex is reused by both incident segments, so consecutive quads meet edge-to-edge with
    // NO gap/notch — the per-segment-independent-quad gaps the old AddLine left at every joint are gone.
    private const float MiterLimit = 4f;

    // Emit a continuous miter-joined stroke for a polyline. Consecutive segments SHARE each interior vertex's
    // ribs, so there is no gap at any joint. Endpoints get a tiny along-line cap (half the core) so segment
    // ends don't look chopped. Straight axis/grid segments still go through AddLine (no joins needed).
    private static void AddPolyline(VertexHelper vh, Vector2 origin, IReadOnlyList<Vector2> pts, float halfWidth, Color32 c)
    {
        var n = pts.Count;
        if (n < 2) return;
        var hc = Mathf.Max(halfWidth, 0.25f);                 // full-opacity core half-width
        var clear = new Color32(c.r, c.g, c.b, 0);            // transparent fringe colour
        var first = vh.currentVertCount;
        for (var i = 0; i < n; i++)
        {
            var p = origin + pts[i];
            var (offCore, offEdge, cap) = RibOffset(pts, i, hc);
            // Tiny end-cap: nudge the endpoint along its segment direction by half the core so the stroke end
            // isn't blunt-chopped (interior vertices get cap=0). Ribs cross the line at the (capped) point.
            var pc = p + cap;
            vh.AddVert(new Vector3(pc.x - offEdge.x, pc.y - offEdge.y), clear, Vector2.zero);   // -edge (α0)
            vh.AddVert(new Vector3(pc.x - offCore.x, pc.y - offCore.y), c, Vector2.zero);       // -core (full)
            vh.AddVert(new Vector3(pc.x + offCore.x, pc.y + offCore.y), c, Vector2.zero);       // +core (full)
            vh.AddVert(new Vector3(pc.x + offEdge.x, pc.y + offEdge.y), clear, Vector2.zero);   // +edge (α0)
        }
        for (var i = 1; i < n; i++)
        {
            var a = first + (i - 1) * 4;
            var b = first + i * 4;
            AddBand(vh, a + 0, a + 1, b + 0, b + 1);   // lower feather: α0 → full
            AddBand(vh, a + 1, a + 2, b + 1, b + 2);   // core: full → full
            AddBand(vh, a + 2, a + 3, b + 2, b + 3);   // upper feather: full → α0
        }
    }

    // The rib offset vectors (core + edge) and along-line end-cap for vertex i. Interior vertices use the
    // miter vector (normalized sum of the two adjacent unit normals, scaled by 1/cos(θ/2)); too-sharp angles
    // past MiterLimit fall back to one segment normal. Endpoints use the single adjacent segment normal and a
    // half-core cap along the line. Reusing one rib per vertex is what closes the joint with no gap.
    private static (Vector2 core, Vector2 edge, Vector2 cap) RibOffset(IReadOnlyList<Vector2> pts, int i, float hc)
    {
        var n = pts.Count;
        var dIn = i > 0 ? UnitDir(pts[i - 1], pts[i]) : Vector2.zero;
        var dOut = i < n - 1 ? UnitDir(pts[i], pts[i + 1]) : Vector2.zero;
        if (i == 0)   return RibFor(Normal(dOut), hc, -dOut * hc * 0.5f);   // start cap: out along -dOut
        if (i == n-1) return RibFor(Normal(dIn), hc, dIn * hc * 0.5f);      // end cap:   out along +dIn
        var nIn = Normal(dIn);
        var nOut = Normal(dOut);
        var mit = nIn + nOut;
        var mlen = mit.magnitude;
        if (mlen < 0.0001f) return RibFor(nIn, hc, Vector2.zero);           // 180° doubling-back → straight
        var m = mit / mlen;
        var scale = 1f / Mathf.Max(Vector2.Dot(m, nOut), 0.0001f);         // 1/cos(θ/2)
        if (scale > MiterLimit) return RibFor(nIn, hc, Vector2.zero);       // sharp → bevel-ish fallback
        return RibFor(m, hc * scale, Vector2.zero);                        // miter join (no cap, shared rib)
    }

    // Build the (core, edge, cap) triple for a unit rib direction scaled to a given core half-width.
    private static (Vector2 core, Vector2 edge, Vector2 cap) RibFor(Vector2 unitRib, float coreLen, Vector2 cap)
        => (unitRib * coreLen, unitRib * (coreLen + Feather), cap);

    private static Vector2 UnitDir(Vector2 from, Vector2 to)
    {
        var d = to - from; var len = d.magnitude;
        return len < 0.0001f ? Vector2.zero : d / len;
    }

    private static Vector2 Normal(Vector2 unitDir) => new(-unitDir.y, unitDir.x);

    // Emit a feathered (alpha-ramped) stroke for the straight segment a→b (axes + gridlines, no joins).
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
