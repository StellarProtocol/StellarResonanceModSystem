using System.Collections.Generic;
using TMPro;

namespace Stellar.Infrastructure.Game;

// diag ([Hitch] attribution, revert with the "diag:" commit): a glyph/page census of every TMP font asset our
// styled + RichEmoji text can rasterize into — our four faces, their fallback tables (Malgun Gothic), and the
// GAME's global TMP_Settings fallbacks/default (TMP walks those for a character our face lacks, and a dynamic one
// there gets the glyph added too). Fed to StyledTextFactory.GlyphCount so each apply phase can report how many
// glyphs it added (TMP adds dynamic glyphs synchronously during layout/mesh generation). Logs each asset's atlas
// settings once, and every atlas-page growth (a multi-atlas page = a fresh 1024² texture). Game-only (TMP types).
internal static partial class TmpFontAssets
{
    private static List<TMP_FontAsset>? _diagAssets;
    private static int _diagPages = -1;

    internal static int GlyphTotal()
    {
        if (!_tried) return 0;   // never trigger Ensure (the font creation) from the probe itself
        var set = _diagAssets ??= CollectDiagAssets();
        int glyphs = 0, pages = 0;
        for (var i = 0; i < set.Count; i++)
        {
            try { glyphs += set[i].characterTable?.Count ?? 0; pages += set[i].atlasTextures?.Length ?? 0; }
            catch { /* destroyed asset — skip */ }
        }
        if (pages != _diagPages)
        {
            if (_diagPages >= 0) StyledTextFactory.Log?.Invoke($"[Window][tmp-diag] TMP atlas pages {_diagPages}->{pages} (glyphs={glyphs})");
            _diagPages = pages;
        }
        return glyphs;
    }

    private static List<TMP_FontAsset> CollectDiagAssets()
    {
        var set = new List<TMP_FontAsset>();
        void Add(TMP_FontAsset? a, string role)
        {
            if (a == null) return;
            for (var i = 0; i < set.Count; i++) if (set[i] == a) return;
            set.Add(a);
            LogAsset(a, role);
            try
            {
                var fb = a.fallbackFontAssetTable;
                if (fb != null) for (var i = 0; i < fb.Count; i++) Add(fb[i], role + ".fallback");
            }
            catch { }
        }
        Add(_uiRegular, "uiRegular"); Add(_uiBold, "uiBold"); Add(_cjkRegular, "cjkRegular"); Add(_cjkBold, "cjkBold");
        try
        {
            var g = TMP_Settings.fallbackFontAssets;
            if (g != null) for (var i = 0; i < g.Count; i++) Add(g[i], "gameFallback");
            Add(TMP_Settings.defaultFontAsset, "gameDefault");
        }
        catch { /* TMP_Settings not loaded */ }
        return set;
    }

    private static void LogAsset(TMP_FontAsset a, string role)
    {
        try
        {
            StyledTextFactory.Log?.Invoke($"[Window][tmp-diag] font role={role} name='{a.name}' pop={a.atlasPopulationMode} " +
                $"mode={Prop(a, "atlasRenderMode")} face={Prop(a, "faceInfo")} pad={a.atlasPadding} atlas={a.atlasWidth}x{a.atlasHeight} " +
                $"pages={a.atlasTextures?.Length ?? 0} multi={a.isMultiAtlasTexturesEnabled} glyphs={a.characterTable?.Count ?? 0}");
        }
        catch { }
    }

    // GlyphRenderMode / FaceInfo live in UnityEngine.TextCoreFontEngineModule, which Infrastructure doesn't reference —
    // read them by reflection (diag only) rather than adding a reference. faceInfo's pointSize via a nested read.
    private static string Prop(object o, string name)
    {
        try
        {
            var v = o.GetType().GetProperty(name)?.GetValue(o);
            if (v == null) return "?";
            var pt = v.GetType().GetProperty("pointSize")?.GetValue(v);
            return pt != null ? "pt" + pt : v.ToString() ?? "?";
        }
        catch { return "?"; }
    }
}
