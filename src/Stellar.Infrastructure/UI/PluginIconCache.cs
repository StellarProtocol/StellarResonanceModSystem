using System.Collections.Generic;
using UnityEngine;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Turns raw PNG bytes (a plugin's embedded icon, carried Unity-free through
/// <c>MenuButtonSpec.IconPng</c> / <c>LauncherEntry.IconPng</c>) into a
/// <see cref="Texture2D"/> for the rail button and launcher menu to draw.
///
/// <para>Keyed by <b>reference identity</b> of the byte array, not its content:
/// callers hand back the same stable <c>byte[]</c> every frame (<c>LauncherIcons</c>
/// caches per name; <c>LauncherEntry.IconPng</c> is a fixed field), so a reference
/// compare is O(1) and avoids re-hashing the whole PNG on every per-frame draw.
/// Textures are flagged <see cref="HideFlags.HideAndDontSave"/> so
/// <c>UnloadUnusedAssets</c> (scene loads) does not collect them; if one is
/// destroyed anyway, the next <see cref="Get"/> reloads it (Unity reports a
/// destroyed texture as <c>== null</c>). Mirrors <c>StatIconAtlas.LoadEmbedded</c>.</para>
/// </summary>
internal sealed class PluginIconCache : System.IDisposable
{
    private readonly Dictionary<byte[], Entry> _byRef = new(ReferenceEqualityComparer.Instance);

    private sealed class Entry
    {
        public Texture2D? Texture;
        public bool Failed;   // bytes genuinely un-decodable — stop retrying
    }

    /// <summary>
    /// Texture for <paramref name="png"/>, or null if the bytes are absent/un-decodable.
    /// Reloads transparently if a previously-loaded texture was destroyed.
    /// </summary>
    public Texture2D? Get(byte[]? png)
    {
        if (png == null || png.Length == 0) return null;

        if (!_byRef.TryGetValue(png, out var entry))
        {
            entry = new Entry();
            _byRef[png] = entry;
        }

        if (entry.Texture != null) return entry.Texture;   // Unity == : false once destroyed → reload below
        if (entry.Failed) return null;

        entry.Texture = Load(png);
        if (entry.Texture == null) entry.Failed = true;
        return entry.Texture;
    }

    private static Texture2D? Load(byte[] png)
    {
        try
        {
            // mipChain:true + Trilinear so a 128px icon drawn at ~22px samples a
            // smooth downscaled mip instead of aliasing (bilinear minification of
            // a full-res texture looks pixelated). LoadImage regenerates mips
            // because the texture was created with a mip chain.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
            {
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,   // survive UnloadUnusedAssets (scene loads)
            };
            if (!ImageConversion.LoadImage(tex, png)) return null;
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            return tex;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Destroys all cached textures (framework reload / window teardown).</summary>
    public void Dispose()
    {
        foreach (var e in _byRef.Values)
        {
            if (e.Texture != null) UnityEngine.Object.Destroy(e.Texture);
            e.Texture = null;
        }
        _byRef.Clear();
    }
}
