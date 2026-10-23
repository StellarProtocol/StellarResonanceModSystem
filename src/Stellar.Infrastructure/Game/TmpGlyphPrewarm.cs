using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using TMPro;
using HitchProbe = Stellar.Abstractions.Diagnostics.HitchProbe;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Rasterizes the glyphs of text that is ABOUT to be shown, a few per rendered frame, so the window apply that
/// first lays that text out finds them already in the dynamic atlas. Without it every first-seen glyph is
/// rasterized synchronously inside layout (TMP adds missing dynamic glyphs during GetPreferred* /
/// ForceRebuildLayoutImmediate / mesh generation) — the ChatTools login freeze, where a CJK/Thai history batch met
/// ~60 new glyphs in ONE apply (2026-10-23). Fed by <c>IWindowHost.PrewarmText</c> (the plugin knows what it will
/// render: message bodies as they arrive), drained from the window ticker's per-frame Update.
///
/// Per frame: at most <see cref="MaxGlyphsPerFrame"/> glyphs or ~<see cref="MaxMsPerFrame"/> ms, whichever comes
/// first (fixed constants). The face is picked exactly as styled text picks it (<see cref="TmpFontAssets.FaceFor"/>,
/// REGULAR weight — chat bodies are regular; bold titles are short and few), and a character the face lacks is
/// offered to its fallback table and then TMP's global fallbacks — the same order TMP walks at render time.
/// Best-effort only: a glyph not warmed in time is still rasterized by layout exactly as before. Game-only (TMP).
/// </summary>
internal static class TmpGlyphPrewarm
{
    private const int MaxGlyphsPerFrame = 8;
    private const double MaxMsPerFrame = 3.0;
    // Glyphs per TryAddCharacters call. Each call packs + uploads once, so batching is cheaper than one-by-one, but a
    // call cannot be interrupted — keep it small so a slow face (~1 ms/glyph) can't overshoot the time budget much.
    private const int ChunkGlyphs = 4;
    // Texts waiting to be scanned. A login delivers ~120-500 history messages; anything past this is dropped (the
    // glyphs then rasterize in layout as before) rather than growing without bound while no canvas ticks.
    private const int MaxQueuedTexts = 1024;
    // A tag longer than this is treated as literal text (rich-text tags like <sprite=12> are short).
    private const int MaxTagLength = 32;

    private static readonly ConcurrentQueue<string> _texts = new();
    private static int _queued;

    // One per face met so far (≤ 4: regular Latin+Thai / CJK, and their bold siblings if ever asked for).
    private sealed class FaceWork
    {
        public TMP_FontAsset Face = null!;
        public readonly Queue<char> Pending = new();      // still to rasterize, arrival order
        public readonly HashSet<char> Known = new();      // ever queued/attempted for this face → never re-tried
    }

    private static readonly List<FaceWork> _faces = new();
    private static readonly List<TMP_FontAsset> _touched = new();
    private static readonly List<int> _pagesBefore = new();
    private static readonly char[] _chunk = new char[ChunkGlyphs];

    /// <summary>Queue a string for glyph prewarm. Thread-safe; null/empty is ignored.</summary>
    internal static void Enqueue(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Interlocked.Increment(ref _queued) > MaxQueuedTexts) { Interlocked.Decrement(ref _queued); return; }
        _texts.Enqueue(text!);
    }

    /// <summary>Per rendered frame (main thread): scan queued texts, then rasterize up to the frame budget.</summary>
    internal static void Tick()
    {
        if (_texts.IsEmpty && !AnyPending()) return;
        if (StyledTextFactory.CreateBold == null) return;   // TMP styled text not registered → nothing renders through it
        var start = Stopwatch.GetTimestamp();
        var budget = (long)(MaxMsPerFrame * Stopwatch.Frequency / 1000.0);
        var hT = HitchProbe.Begin();   // diag: [Hitch] tmp:prewarm (should absorb what ap:bubbles/ap:styled used to pay)

        while (Stopwatch.GetTimestamp() - start < budget && _texts.TryDequeue(out var s))
        {
            Interlocked.Decrement(ref _queued);
            Scan(s);
        }

        var added = 0;
        _touched.Clear(); _pagesBefore.Clear();
        while (added < MaxGlyphsPerFrame && Stopwatch.GetTimestamp() - start < budget)
        {
            var w = NextPending();
            if (w == null) break;
            var n = TakeChunk(w, System.Math.Min(ChunkGlyphs, MaxGlyphsPerFrame - added));
            if (n == 0) continue;   // everything taken was already present (layout beat us to it)
            AddWithFallbacks(w.Face, new string(_chunk, 0, n));
            added += n;
        }
        UploadTouched();
        HitchProbe.End("tmp:prewarm", hT);
        HitchProbe.Count("tmp:prewarmGlyphs", added);
    }

    // Distinct, drawable, not-yet-present characters of one string → its face's pending queue. Rich-text tags are
    // skipped (their letters are not drawn); whitespace/control/surrogate halves are skipped (TMP draws no glyph for
    // the former, and a lone UTF-16 half is not a code point — emoji arrive as <sprite=N> tags anyway).
    private static void Scan(string s)
    {
        TMP_FontAsset? face;
        try { face = TmpFontAssets.FaceFor(s, bold: false); } catch { return; }
        if (face == null) return;
        var w = WorkFor(face);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '<')
            {
                var close = s.IndexOf('>', i + 1);
                if (close > i && close - i <= MaxTagLength) { i = close; continue; }
            }
            if (char.IsWhiteSpace(c) || char.IsControl(c) || char.IsSurrogate(c)) continue;
            if (!w.Known.Add(c)) continue;
            try { if (face.HasCharacter((int)c)) continue; } catch { continue; }
            w.Pending.Enqueue(c);
        }
    }

    // Pop up to max still-missing characters into _chunk (re-checked: layout may have added one since the scan).
    private static int TakeChunk(FaceWork w, int max)
    {
        var n = 0;
        while (n < max && w.Pending.Count > 0)
        {
            var c = w.Pending.Dequeue();
            try { if (w.Face.HasCharacter((int)c)) continue; } catch { continue; }
            _chunk[n++] = c;
        }
        return n;
    }

    // Primary first; what it lacks goes to its own fallback table, then TMP's global fallbacks (the render-time walk).
    // A Static asset is skipped (it can't grow); TryAddCharacters reports what is still missing after each step.
    private static void AddWithFallbacks(TMP_FontAsset face, string chars)
    {
        var missing = TryAdd(face, chars);
        if (string.IsNullOrEmpty(missing)) return;
        try
        {
            var fb = face.fallbackFontAssetTable;
            if (fb != null)
                for (var i = 0; i < fb.Count && !string.IsNullOrEmpty(missing); i++) missing = TryAdd(fb[i], missing!);
            var g = TMP_Settings.fallbackFontAssets;
            if (g != null)
                for (var i = 0; i < g.Count && !string.IsNullOrEmpty(missing); i++) missing = TryAdd(g[i], missing!);
        }
        catch { /* fallback tables unavailable — the primary's share is still warmed */ }
    }

    private static string? TryAdd(TMP_FontAsset? a, string chars)
    {
        if (a == null) return chars;
        try
        {
            if (a.atlasPopulationMode == AtlasPopulationMode.Static) return chars;
            Touch(a);
            a.TryAddCharacters(chars, out string missing);
            return missing;
        }
        catch { return chars; }
    }

    private static void Touch(TMP_FontAsset a)
    {
        for (var i = 0; i < _touched.Count; i++) if (_touched[i] == a) return;
        _touched.Add(a);
        var pages = 0;
        try { pages = a.atlasTextureCount; } catch { }
        _pagesBefore.Add(pages);
    }

    // Flush the atlas pages the new glyphs landed on: in this game's TMP build a glyph added via TryAddCharacters can
    // sit in the lookup table with its texels NOT yet uploaded (the U+005F underline root cause — see
    // TmpFontAssets.SeedDecorationGlyph), so upload explicitly. New glyphs go to the current (last) page; if a call
    // opened a new page, the previously-last one was filled too.
    private static void UploadTouched()
    {
        for (var i = 0; i < _touched.Count; i++)
        {
            try
            {
                var a = _touched[i];
                var pages = a.atlasTextures;
                var count = a.atlasTextureCount;
                if (pages == null) continue;
                for (var p = System.Math.Max(0, _pagesBefore[i] - 1); p < count && p < pages.Length; p++)
                    if (pages[p] != null) pages[p].Apply(false, false);
            }
            catch { /* non-readable atlas — best effort, same as the decoration seed */ }
        }
        _touched.Clear(); _pagesBefore.Clear();
    }

    private static FaceWork WorkFor(TMP_FontAsset face)
    {
        for (var i = 0; i < _faces.Count; i++) if (_faces[i].Face == face) return _faces[i];
        var w = new FaceWork { Face = face };
        _faces.Add(w);
        return w;
    }

    private static FaceWork? NextPending()
    {
        for (var i = 0; i < _faces.Count; i++) if (_faces[i].Pending.Count > 0) return _faces[i];
        return null;
    }

    private static bool AnyPending() => NextPending() != null;
}
