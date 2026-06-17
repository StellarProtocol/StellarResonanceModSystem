using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// uGUI renderer for the line chart. Splits into two layers by what each does best:
///
/// <list type="bullet">
/// <item><b>Mesh layer</b> (<see cref="OnPopulateMesh"/>): the AXES + GRIDLINES (straight runs) and the
/// navigator area-FILLS. A <see cref="MaskableGraphic"/> whose <c>OnPopulateMesh</c> emits one thin quad per
/// straight segment / one quad-strip per fill. Straight axis/grid lines are pixel-aligned, so the simple
/// feathered quad is fine and cheap.</item>
/// <item><b>Raster layer</b> (<see cref="RebakeLines"/>): the plotted-series POLYLINES (main chart + navigator
/// overview lines). Meshed feathered quads could not be thin AND smooth at once (0.5px feather → jagged,
/// 1.75px → too thick). So the polylines are now baked into a <b>supersampled</b> <see cref="Texture2D"/>
/// (RGBA32, rendered at <see cref="Supersample"/>× then box-downsampled) → true sub-pixel coverage AA: a thin
/// (~1px) crisp smooth line, no feather band. The texture is shown via a child <see cref="RawImage"/> stretched
/// over the plot rect — the <c>WindowBuilder.ColorPicker.cs</c> <c>Texture2D</c>+<c>RawImage</c> pattern, which
/// is IL2CPP-safe (no <c>ClassInjector</c> needed for a stock uGUI component).</item>
/// </list>
///
/// The spike (2026-06-16) confirmed native→managed <c>OnPopulateMesh</c> dispatch works for a
/// <c>ClassInjector</c>-injected graphic under Il2CppInterop 1.5.1; <c>WindowRenderer.EnsureCanvas</c> registers
/// the type once before any <c>AddComponent&lt;ChartGraphic&gt;</c> runs. Contains NO <c>ClassInjector</c>
/// reference itself, so it also compiles + renders natively in the Mono UI sandbox.
///
/// Data is pushed in via <see cref="SetData"/> (already mapped to local pixel space by the builder); the
/// graphic triangulates the mesh layer and re-bakes the raster layer. Both happen ONLY on <see cref="SetData"/>
/// — the builder dirties only when series/visible-range/plot-size/theme changed, so static archived data pays
/// the bake cost on session-select / zoom / resize / theme switch, never per-frame.
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

    // The raster layer: a child RawImage showing the supersampled-AA polyline texture, lazily created on the
    // first bake so a chart with no plotted series never spawns one. Stretched to cover the graphic rect.
    private RawImage? _lineRaw;
    private Texture2D? _lineTex;

    // Push freshly-mapped geometry, then dirty the mesh (axes/grid/fill) AND re-bake the polyline raster.
    internal void SetData(IReadOnlyList<Segment> segments, IReadOnlyList<Polyline> polylines)
        => SetData(segments, polylines, Array.Empty<Fill>());

    // Overload with filled areas (drawn first, under the lines/segments) — used by the navigator overview.
    internal void SetData(IReadOnlyList<Segment> segments, IReadOnlyList<Polyline> polylines, IReadOnlyList<Fill> fills)
    {
        _segments = segments ?? Array.Empty<Segment>();
        _polylines = polylines ?? Array.Empty<Polyline>();
        _fills = fills ?? Array.Empty<Fill>();
        SetVerticesDirty();   // mesh layer: axes + gridlines + nav fills
        RebakeLines();        // raster layer: supersampled-AA plotted polylines
    }

    // Access-modifier divergence: the IL2CPP UnityEngine.UI.Graphic declares OnPopulateMesh PUBLIC (so the
    // game build needs `public override` → CS0507 if protected), but stock Mono uGUI (the UI sandbox player)
    // declares it PROTECTED (→ CS0507 if public). UNITY_5_3_OR_NEWER is defined by every Unity compiler and
    // never by the plain-dotnet game DLL build, so it cleanly selects the sandbox's `protected` variant.
    // Origin is bottom-left of the pixel-adjusted rect; the builder maps points into that same space. The
    // plotted polylines are NOT meshed here — they are baked into the raster layer (see RebakeLines).
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
    }

    // -------------------------------------------------------------------------------------------------------
    // Raster (supersampled-AA) polyline layer.
    // -------------------------------------------------------------------------------------------------------

    // Supersample factor: the line texture is rasterised at this multiple of the plot pixel size, then box-
    // downsampled to 1×. A pixel's final alpha is the fraction of its SS×SS sub-samples covered by the stroke,
    // i.e. true sub-pixel coverage AA. 3× (9 sub-samples) gives a smooth gradient on diagonals at a modest
    // memory cost; the texture is rebaked only on data/range/size/theme change, never per-frame.
    private const int Supersample = 3;

    // Stroke half-width (px, in FINAL plot space) for the supersampled rasteriser. Tuned so the rendered line
    // reads as a thin ~1px solid core: at 0.6px half-width the fully-covered band is ~1.2px wide, flanked by
    // coverage-graded partial pixels from the SS downsample (≈2px overall, NOT a 5-6px feather band). The
    // builder's per-series Polyline.Thickness scales this so the emphasis (team-total) line reads a touch
    // heavier than the rest, preserving the existing visual hierarchy.
    private const float StrokeHalf = 0.6f;

    // Hard cap on the baked texture's FINAL dimension (px). A FillWidth chart in a wide window could ask for a
    // very large plot; the SS buffer is width·height·SS² floats, so cap the long edge to keep the bake bounded
    // (the RawImage stretches the capped texture over the real rect — a 1px-wide line tolerates the tiny
    // horizontal stretch without visible thickening because the AA gradient stretches with it).
    private const int MaxTexDim = 2048;

    // Re-bake the plotted polylines into the supersampled-AA texture and (re)assign it to the child RawImage.
    // No-op (and the RawImage is hidden) when there are no polylines or the rect hasn't been laid out yet.
    private void RebakeLines()
    {
        var r = GetPixelAdjustedRect();
        var w = Mathf.RoundToInt(r.width);
        var h = Mathf.RoundToInt(r.height);
        if (_polylines.Count == 0 || w < 2 || h < 2)
        {
            if (_lineRaw != null) _lineRaw.enabled = false;
            return;
        }

        // Final-resolution dimensions, capped; the scale maps plot-px → final-texel for the bake.
        var fw = Mathf.Min(w, MaxTexDim);
        var fh = Mathf.Min(h, MaxTexDim);
        var sx = fw / (float)w;
        var sy = fh / (float)h;

        var pixels = RasterizeSupersampled(fw, fh, sx, sy);
        EnsureLineTexture(fw, fh);
        _lineTex!.SetPixels32(pixels);
        _lineTex.Apply(updateMipmaps: false);
        EnsureLineRaw();
        _lineRaw!.enabled = true;
        _lineRaw.texture = _lineTex;
        _lineRaw.color = Color.white;   // texture carries per-pixel colour+alpha; tint must be neutral
    }

    // Box-downsampled coverage AA: for each FINAL texel, average its Supersample×Supersample sub-samples. Each
    // sub-sample is "inside" a polyline if its distance to any of that polyline's segments is ≤ the stroke
    // half-width (in SS space). Inside sub-samples contribute the polyline colour; the texel's alpha is the
    // covered fraction (anti-aliased edge), its RGB the coverage-weighted colour blend. Later polylines drawn
    // over earlier ones (painter's order, matching the old mesh emit order).
    private Color32[] RasterizeSupersampled(int fw, int fh, float sx, float sy)
    {
        var ss = Supersample;
        var samples = ss * ss;
        var pixels = new Color32[fw * fh];
        for (var y = 0; y < fh; y++)
        {
            for (var x = 0; x < fw; x++)
            {
                var acc = default(SampleAccum);   // coverage-weighted colour accumulators for this texel
                for (var syi = 0; syi < ss; syi++)
                {
                    for (var sxi = 0; sxi < ss; sxi++)
                    {
                        // Sub-sample centre in FINAL-texel space, then to PLOT-pixel space (the polyline coords).
                        var px = (x + (sxi + 0.5f) / ss) / sx;
                        var py = (y + (syi + 0.5f) / ss) / sy;
                        AccumulateSample(px, py, ref acc);
                    }
                }
                pixels[y * fw + x] = acc.Resolve(samples);
            }
        }
        return pixels;
    }

    // Coverage-weighted colour accumulator for one downsampled texel: A is the summed alpha-coverage across the
    // SS×SS sub-samples; R/G/B are the coverage-weighted colour sums. Resolve normalises to a premultiplied-free
    // Color32 (RGB = mean colour, alpha = mean coverage).
    private struct SampleAccum
    {
        public float A, R, G, B;

        public readonly Color32 Resolve(int samples)
        {
            if (A <= 0f) return default;
            var inv = 1f / A;
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(R * inv), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(G * inv), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(B * inv), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(A / samples * 255f), 0, 255));
        }
    }

    // The series-weight baseline: WindowBuilder's ChartLineWidth (ordinary series). A polyline's stroke
    // half-width scales linearly with its Thickness relative to this baseline, so the emphasis (team-total)
    // line stays proportionally heavier than the rest, exactly as the old mesh path did.
    private const float WeightBaseline = 0.45f;

    // Mark a single sub-sample at PLOT-pixel (px, py) covered if it lies within the stroke half-width of any
    // segment of any polyline, accumulating the polyline's alpha-weighted colour + coverage into acc.
    private void AccumulateSample(float px, float py, ref SampleAccum acc)
    {
        for (var i = 0; i < _polylines.Count; i++)
        {
            var pl = _polylines[i];
            var pts = pl.Points;
            if (pts.Count < 1) continue;
            // Scale the thin StrokeHalf by the series weight: ordinary (0.45)→1×, emphasis (0.7)→~1.55×.
            var half = StrokeHalf * Mathf.Max(pl.Thickness / WeightBaseline, 1f);
            if (CoveredByPolyline(px, py, pts, half))
            {
                // Weight coverage by the polyline's own alpha so faint navigator lines (alpha 110) stay faint;
                // the colour is accumulated at full intensity and normalised by the alpha-weighted coverage.
                var w = pl.Color.a / 255f;
                acc.A += w; acc.R += pl.Color.r * w; acc.G += pl.Color.g * w; acc.B += pl.Color.b * w;
            }
        }
    }

    // True if (px, py) is within half of any segment of the polyline (or its single point, as a dot).
    private static bool CoveredByPolyline(float px, float py, IReadOnlyList<Vector2> pts, float half)
    {
        var h2 = half * half;
        if (pts.Count == 1)
        {
            var dx0 = px - pts[0].x; var dy0 = py - pts[0].y;
            return dx0 * dx0 + dy0 * dy0 <= h2;
        }
        for (var j = 1; j < pts.Count; j++)
        {
            if (DistSqToSegment(px, py, pts[j - 1], pts[j]) <= h2) return true;
        }
        return false;
    }

    // Squared distance from point (px, py) to the segment [a, b].
    private static float DistSqToSegment(float px, float py, Vector2 a, Vector2 b)
    {
        var abx = b.x - a.x; var aby = b.y - a.y;
        var apx = px - a.x; var apy = py - a.y;
        var len2 = abx * abx + aby * aby;
        var t = len2 > 0.0001f ? Mathf.Clamp01((apx * abx + apy * aby) / len2) : 0f;
        var dx = apx - t * abx; var dy = apy - t * aby;
        return dx * dx + dy * dy;
    }

    private void EnsureLineTexture(int w, int h)
    {
        if (_lineTex != null && _lineTex.width == w && _lineTex.height == h) return;
        if (_lineTex != null) UnityEngine.Object.Destroy(_lineTex);
        _lineTex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
    }

    // Lazily spawn the child RawImage that displays the baked line texture, stretched over the graphic rect.
    private void EnsureLineRaw()
    {
        if (_lineRaw != null) return;
        var go = new GameObject("LineRaster");
        go.transform.SetParent(transform, worldPositionStays: false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        _lineRaw = go.AddComponent<RawImage>();
        _lineRaw.raycastTarget = false;
    }

    // The baked line texture uses HideFlags.HideAndDontSave, so GameObject destruction won't reclaim it (same
    // as the ColorPicker bakes). Destroy it explicitly when the graphic is torn down with its window. Graphic
    // declares OnDestroy PUBLIC in the IL2CPP build but PROTECTED in stock Mono uGUI — the same access-modifier
    // divergence as OnPopulateMesh, so reuse UNITY_5_3_OR_NEWER to pick the right modifier per build.
#if UNITY_5_3_OR_NEWER
    protected override void OnDestroy()
#else
    public override void OnDestroy()
#endif
    {
        if (_lineTex != null) { UnityEngine.Object.Destroy(_lineTex); _lineTex = null; }
        base.OnDestroy();
    }

    // -------------------------------------------------------------------------------------------------------
    // Mesh layer (axes + gridlines + navigator fills).
    // -------------------------------------------------------------------------------------------------------

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

    // AA ramp width (per side, px) for the straight axis/grid segments. These are pixel-aligned straight runs,
    // so a thin feather is plenty — the smoothness problem was only ever the DIAGONAL plotted lines (now
    // baked). Kept small so axes/grid stay crisp.
    private const float Feather = 0.6f;

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
