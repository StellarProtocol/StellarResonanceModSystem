using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Loads the embedded launcher icon set (raw PNG bytes). Used by Host to
/// populate <c>LauncherEntry.IconPng</c> / <c>MenuButtonSpec.IconPng</c> for
/// the framework's own entries + rail button, and by the launcher window for
/// its chrome glyphs (pin star, mode toggle). Bytes are cached per name.
///
/// <para>Names (no extension): the brand <c>stellar, stellar-glow</c>; the Settings rail / entry glyphs —
/// <c>general, plugins, layout, theme, hotkeys, gameui, performance, about, settings</c>; and the chrome glyphs —
/// <c>edit_layout, star, star_outline, mode_grid, mode_list, rotate, search, reset</c>. All but the brand are icon
/// set v2 "A" (pure-white 2 px rounded line glyphs on transparent 128×128, drawn for a live accent tile or a tint;
/// 2026-11-09). <c>star</c>/<c>star_outline</c> share one outer outline, so pinned/unpinned swap without a jump.
/// <c>search</c> and <c>reset</c> ship for future icon slots — nothing uses them yet.</para>
/// </summary>
internal static class LauncherIcons
{
    private const string Prefix = "Stellar.Infrastructure.icons.";
    private static readonly Dictionary<string, byte[]?> _cache = new();
    private static readonly object _gate = new();

    /// <summary>Raw PNG bytes for <paramref name="name"/> (no extension), or null if absent.</summary>
    public static byte[]? Get(string name)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;
            var bytes = Load(name);
            _cache[name] = bytes;
            return bytes;
        }
    }

    private static byte[]? Load(string name)
    {
        try
        {
            var asm = typeof(LauncherIcons).Assembly;
            var resource = Prefix + name + ".png";
            using var s = asm.GetManifestResourceStream(resource) ?? FindBySuffix(asm, name);
            if (s != null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            return LoadFromDisk(name);   // sandbox/test fallback (resources aren't embedded in that assembly)
        }
        catch
        {
            return null;
        }
    }

    // Disk fallback for the UI sandbox: the icon PNGs are embedded resources of THIS assembly, which the
    // sandbox Unity project doesn't carry. run-ui-sandbox.sh sets STELLAR_ICONS_DIR so the real LauncherView
    // renders true icons there (no divergent hand-built copy). Unset in-game → no-op, embedded resources win.
    private static byte[]? LoadFromDisk(string name)
    {
        var dir = System.Environment.GetEnvironmentVariable("STELLAR_ICONS_DIR");
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir, name + ".png");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // Fallback if the logical-name convention differs from the build's default.
    private static Stream? FindBySuffix(Assembly asm, string name)
    {
        var suffix = "icons." + name + ".png";
        foreach (var res in asm.GetManifestResourceNames())
            if (res.EndsWith(suffix, System.StringComparison.Ordinal))
                return asm.GetManifestResourceStream(res);
        return null;
    }
}
