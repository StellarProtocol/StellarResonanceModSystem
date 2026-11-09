using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Loads the embedded launcher icon set (raw PNG bytes). Used by Host to
/// populate <c>LauncherEntry.IconPng</c> / <c>MenuButtonSpec.IconPng</c> for
/// the framework's own entries + rail button, and by the framework windows for
/// their glyphs (settings rail, launcher tiles, pin star, mode toggle…). Bytes are cached, so every call hands back
/// the SAME array (the image caches key textures by array reference).
///
/// <para>Names (no extension): the brand <c>stellar, stellar-glow</c> (one flat bitmap each, <c>icons/&lt;name&gt;.png</c>);
/// the Settings rail / entry glyphs — <c>general, plugins, layout, theme, hotkeys, gameui, performance, developer, about,
/// settings</c>; and the chrome glyphs — <c>edit_layout, star, star_outline, mode_grid, mode_list, rotate, search, reset,
/// discord</c>. All but the brand are icon set v2 "A" (pure-white line glyphs on transparent, drawn for a live accent
/// tile or a tint), PRE-DRAWN and pixel-hinted per size: <c>icons/&lt;size&gt;/&lt;name&gt;.png</c> for
/// <see cref="PreDrawnSizes"/> (icon art rule 2026-11-09 — a 128 px master scaled to 15 px put the 2/24 stroke at ~1.3 px
/// = soft grey AA). Re-drawn art is a plain file replace at the same path. <c>star</c>/<c>star_outline</c> share one
/// outer outline, so pinned/unpinned swap without a jump. <c>search</c> and <c>reset</c> have no icon slot yet.</para>
///
/// <para>Two lookups: <see cref="Glyph"/> / <see cref="Get(string, int)"/> pick the bitmap for an on-screen size
/// (<see cref="IconSizePicker"/>); <see cref="Get(string)"/> (no size) is the 128 px art for callers that scale it
/// themselves (mipmapped). An exact pick returns an array flagged <see cref="IsPixelExact"/>, which the loaders decode
/// WITHOUT mips (drawn 1:1); a non-exact pick of the same size returns a separate copy, so it gets its own mipmapped
/// texture.</para>
/// </summary>
internal static class LauncherIcons
{
    private const string Prefix = "Stellar.Infrastructure.icons.";

    /// <summary>The sizes every v2 glyph is pre-drawn at, ascending. Must match the csproj's per-size globs.</summary>
    internal static readonly int[] PreDrawnSizes = { 12, 16, 24, 32, 64, 128 };
    private const int MasterSize = 128;

    private static readonly Dictionary<string, byte[]?> _flat = new();
    private static readonly Dictionary<string, Sized> _sized = new();
    private static readonly HashSet<byte[]> _exact = new(ReferenceEqualityComparer.Instance);
    private static readonly object _gate = new();

    /// <summary>Screen px per canvas unit of the window canvas (the CanvasScaler scaleFactor = resolution scale × UI
    /// scale). Wired by the window renderer; null (sandbox/tests) = 1.</summary>
    internal static Func<float>? ScaleSource;

    // One glyph's pre-drawn bitmaps. Sizes = the ones actually embedded (a missing size is skipped by the picker);
    // Exact[i] is the as-loaded array (flagged pixel-exact), Fallback[i] a lazily-made copy for scaled use.
    private sealed class Sized
    {
        public readonly List<int> Sizes = new();
        public readonly List<byte[]> Exact = new();
        public readonly List<byte[]?> Fallback = new();
    }

    /// <summary>The bitmap of <paramref name="name"/> for a glyph <paramref name="glyphUnits"/> canvas units square on the
    /// live window canvas. Call it inside the image's Func (with <c>ImageElement.Live</c>) so a resolution / UI-scale
    /// change re-picks on the next refresh.</summary>
    public static byte[]? Glyph(string name, float glyphUnits)
    {
        var scale = 1f;
        try { scale = ScaleSource?.Invoke() ?? 1f; } catch { /* canvas gone mid-teardown → unscaled */ }
        return Pick(name, IconSizePicker.DisplayPx(glyphUnits, scale));
    }

    /// <summary>The bitmap of <paramref name="name"/> for an image <paramref name="displayPx"/> SCREEN pixels square.
    /// Falls back to the un-sized <see cref="Get(string)"/> for a name with no per-size art (the brand).</summary>
    public static byte[]? Get(string name, int displayPx) => Pick(name, displayPx);

    /// <summary>True for an array returned by an EXACT size pick: decode it without mips, it is drawn 1:1.</summary>
    public static bool IsPixelExact(byte[]? png)
    {
        if (png == null) return false;
        lock (_gate) return _exact.Contains(png);
    }

    /// <summary>Raw PNG bytes for <paramref name="name"/> (no extension), or null if absent: the flat file, else the
    /// 128 px pre-drawn art (never the pixel-exact array — callers of this overload scale it with mips).</summary>
    public static byte[]? Get(string name)
    {
        lock (_gate)
        {
            if (_flat.TryGetValue(name, out var cached)) return cached;
            var bytes = Load(name, size: 0);
            if (bytes == null)
            {
                var s = SizedLocked(name);
                var i = s.Sizes.IndexOf(MasterSize);
                if (i < 0 && s.Sizes.Count > 0) i = s.Sizes.Count - 1;
                if (i >= 0) bytes = FallbackLocked(s, i);
            }
            _flat[name] = bytes;
            return bytes;
        }
    }

    private static byte[]? Pick(string name, float targetPx)
    {
        lock (_gate)
        {
            var s = SizedLocked(name);
            var (size, exact) = IconSizePicker.Pick(targetPx, s.Sizes);
            if (size >= 0)
            {
                var i = s.Sizes.IndexOf(size);
                return exact ? s.Exact[i] : FallbackLocked(s, i);
            }
        }
        return Get(name);   // no per-size art (brand) → the flat bitmap
    }

    private static Sized SizedLocked(string name)
    {
        if (_sized.TryGetValue(name, out var s)) return s;
        s = new Sized();
        foreach (var size in PreDrawnSizes)
        {
            if (Load(name, size) is not { Length: > 0 } bytes) continue;
            s.Sizes.Add(size);
            s.Exact.Add(bytes);
            s.Fallback.Add(null);
            _exact.Add(bytes);
        }
        _sized[name] = s;
        return s;
    }

    private static byte[] FallbackLocked(Sized s, int i)
        => s.Fallback[i] ??= (byte[])s.Exact[i].Clone();   // distinct reference → its own mipmapped texture

    // size 0 = the flat icons/<name>.png; otherwise icons/<size>/<name>.png (logical name icons.<size>.<name>.png).
    private static byte[]? Load(string name, int size)
    {
        try
        {
            var asm = typeof(LauncherIcons).Assembly;
            var leaf = size > 0 ? size + "." + name + ".png" : name + ".png";
            using var s = asm.GetManifestResourceStream(Prefix + leaf) ?? FindBySuffix(asm, "icons." + leaf);
            if (s != null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            return LoadFromDisk(name, size);   // sandbox/test fallback (resources aren't embedded in that assembly)
        }
        catch
        {
            return null;
        }
    }

    // Disk fallback for the UI sandbox: the icon PNGs are embedded resources of THIS assembly, which the
    // sandbox Unity project doesn't carry. run-ui-sandbox.sh sets STELLAR_ICONS_DIR (the Resources/icons folder, with
    // its per-size subfolders) so the real LauncherView renders true icons there. Unset in-game → no-op.
    private static byte[]? LoadFromDisk(string name, int size)
    {
        var dir = Environment.GetEnvironmentVariable("STELLAR_ICONS_DIR");
        if (string.IsNullOrEmpty(dir)) return null;
        var path = size > 0 ? Path.Combine(dir, size.ToString(), name + ".png") : Path.Combine(dir, name + ".png");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // Fallback if the logical-name convention differs from the build's default.
    private static Stream? FindBySuffix(Assembly asm, string suffix)
    {
        foreach (var res in asm.GetManifestResourceNames())
            if (res.EndsWith(suffix, StringComparison.Ordinal))
                return asm.GetManifestResourceStream(res);
        return null;
    }
}
