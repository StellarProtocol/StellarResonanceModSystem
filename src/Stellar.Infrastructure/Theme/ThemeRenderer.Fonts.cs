using System;
using System.IO;
using UnityEngine;
using UnityApp = UnityEngine.Application;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Font-loading partial for <see cref="ThemeRenderer"/>. Owns the OS-font
/// resolution strategy + the persisted-to-disk TTF cache so the main
/// <c>ThemeRenderer.cs</c> stays under the 500-LoC standards gate. The font
/// object itself (<c>_font</c>) is declared in the main partial and consumed
/// by <c>BuildGuiStyles</c> / <c>ApplyGuiSkinDefaults</c>.
/// </summary>
/// <remarks>
/// Under Proton/Wine the bundled NotoSans embedded TTF cannot be loaded
/// directly — Unity's runtime <c>Font.CreateDynamicFontFromOSFont</c> only
/// accepts OS-installed typeface names, not raw byte buffers. We persist
/// the embedded TTF to <see cref="UnityApp.temporaryCachePath"/> so future
/// AssetBundle-based loaders (Phase 9b open question) have a stable path,
/// and resolve against a fallback chain of family names that work on both
/// native Linux installs (Noto Sans) and the typical Proton box
/// (DejaVu Sans / Liberation Sans).
/// </remarks>
internal sealed partial class ThemeRenderer
{
    // Multi-name OSFont lookup list. Unity walks the array and returns the
    // first family that resolves on the host; the .name of the returned Font
    // is the family Unity actually bound to. We try the Noto variants first
    // so a native Linux install with Noto installed gets the design target;
    // the DejaVu/Liberation tail covers the typical Proton box, and Arial is
    // the last-ditch fallback that Unity always synthesises.
    private static readonly string[] FontFamilyFallbacks =
    {
        "Noto Sans",
        "NotoSans",
        "Noto Sans CJK SC",
        "DejaVu Sans",
        "Liberation Sans",
        "Arial",
    };

    private void LoadFont()
    {
        // Idempotent: the Unity Font object is process-scope (it represents the
        // OS typeface binding; there is nothing per-theme about it). Reloading
        // it on every Initialise pass would orphan the previous Font instance,
        // which is still referenced by GUI.skin.* and by every constructed
        // GUIStyle whose .font is null (i.e. inherits at draw time). Unity
        // then destroys the orphaned Font on the next GC cycle and every
        // dependent text-draw silently fails — observed as "all panel text
        // disappears on FontScale slider drag" (Phase 9a regression).
        if (_font != null) return;

        try
        {
            PersistEmbeddedFontToCache();
            _font = Font.CreateDynamicFontFromOSFont(FontFamilyFallbacks, 14);
            if (_font is null || !_font)
            {
                _log.Warning("[Theme] Font.CreateDynamicFontFromOSFont(families[]) returned null; falling back to Unity default Arial.");
                _font = Font.CreateDynamicFontFromOSFont("Arial", 14);
            }
            _log.Info($"[Theme] font load: {(_font == null ? "FAILED" : "OK")}, name={_font?.name ?? "(null)"}");
        }
        catch (Exception ex)
        {
            _log.Warning($"[Theme] font load failed ({ex.GetType().Name}); using Unity default. {ex.Message}");
            _font = null;
        }
    }

    /// <summary>
    /// Persist the embedded TTF to a stable cache path so future asset-bundle
    /// or AssetImporter approaches can pick it up. We don't currently load
    /// the bytes directly because Unity's runtime TTF loader only accepts
    /// OS-installed typeface names, not raw byte buffers. Diagnostic tooling
    /// (e.g. fontconfig probes) can also pick up the cached TTF without
    /// re-touching the embedded resource pipeline.
    /// </summary>
    private void PersistEmbeddedFontToCache()
    {
        var bytes = _assets.LoadFontBytes();
        var tempPath = Path.Combine(UnityApp.temporaryCachePath, "Stellar-NotoSans.ttf");
        if (!File.Exists(tempPath))
        {
            File.WriteAllBytes(tempPath, bytes);
        }
    }
}
