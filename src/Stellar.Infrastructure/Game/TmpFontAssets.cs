using System.IO;
using TMPro;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Process-shared TextMeshPro font assets for REAL-bold overlay text (i18n P0 JA/TH bold headers).
/// <see cref="UiBold"/> is a merged Noto Sans Bold + Noto Sans Thai Bold face SHIPPED as an embedded
/// resource, extracted beside the framework DLLs and loaded BY FILE PATH. Measured fact (font probe,
/// 2026-08-18, docs/recon/overlay-font-glyph-coverage.md): Unity under Proton enumerates only the Wine
/// prefix's fonts — never the host's — so an OS-name lookup cannot reliably reach a real bold face,
/// while a file-path TMP asset works on every machine. CJK ships no face (size); <see cref="CjkBold"/>
/// resolves a real system bold family instead — Source Han Sans ships in every Proton prefix, and
/// Yu Gothic UI / Meiryo cover real Windows. Assets are created once and never destroyed (mirrors
/// <see cref="WindowThemeAssets.MenuFont"/> — live TMP texts keep referencing them). Game-only file:
/// the Mono UI-sandbox has no TextMeshPro package, so this must never be symlinked into it.
/// </summary>
internal static class TmpFontAssets
{
    private const string ResourceName = "Stellar.Infrastructure.Resources.StellarUIThaiBold.ttf";
    private const string ExtractedName = "StellarUIThaiBold.ttf";

    private static TMP_FontAsset? _uiBold;
    private static TMP_FontAsset? _cjkBold;
    private static bool _tried;

    /// <summary>Real-bold Latin+Thai face (shipped, machine-independent). Null when creation failed.</summary>
    public static TMP_FontAsset? UiBold { get { Ensure(); return _uiBold; } }

    /// <summary>Real-bold CJK/kana/Hangul face from a system family. Null when no candidate resolved.</summary>
    public static TMP_FontAsset? CjkBold { get { Ensure(); return _cjkBold; } }

    // First candidate that resolves wins. Source Han Sans: every GE-Proton prefix (sourcehansans.ttc);
    // Yu Gothic UI: Windows 8.1+; Meiryo: Windows Vista+ AND Wine prefixes — in practice never all-null.
    private static readonly (string Family, string Style)[] CjkCandidates =
    {
        ("Source Han Sans", "Bold"),
        ("Yu Gothic UI", "Bold"),
        ("Meiryo", "Bold"),
    };

    private static void Ensure()
    {
        if (_tried) return;
        _tried = true;
        try { _uiBold = CreateUiBold(); } catch { _uiBold = null; }
        try { _cjkBold = CreateCjkBold(); } catch { _cjkBold = null; }
    }

    private static TMP_FontAsset? CreateUiBold()
    {
        var path = ExtractShippedFont();
        if (path == null) return null;
        // new Font(path) alone renders nothing on legacy Text, but TMP's CreateFontAsset(Font) resolves
        // the path through FontEngine.LoadFontFace(filePath) — measured working in-game (probe row T2/U1).
        var font = new Font(path);
        return font == null ? null : TMP_FontAsset.CreateFontAsset(font);
    }

    private static TMP_FontAsset? CreateCjkBold()
    {
        foreach (var (family, style) in CjkCandidates)
        {
            try
            {
                var asset = TMP_FontAsset.CreateFontAsset(family, style, 90);
                if (asset != null) return asset;
            }
            catch { /* try the next candidate */ }
        }
        return null;
    }

    // Extract the embedded face next to the framework DLLs (recreated after every deploy; skipped when
    // already present with the right size). Returns null when the directory or resource is unavailable.
    private static string? ExtractShippedFont()
    {
        var asm = typeof(TmpFontAssets).Assembly;
        var dir = Path.GetDirectoryName(asm.Location);
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir, ExtractedName);
        using var src = asm.GetManifestResourceStream(ResourceName);
        if (src == null) return null;
        if (!File.Exists(path) || new FileInfo(path).Length != src.Length)
        {
            using var dst = File.Create(path);
            src.CopyTo(dst);
        }
        return path;
    }
}
