using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

// Effect placeholder tiles (IGameAssets.LoadEffectPlaceholder / GetEffectPlaceholderPng), moved from CombatMeter 3.0:
// a white category glyph on a green (buff) / red (debuff) tile for an effect with no icon of its own. Art =
// Resources/effects/<stem>-<buff|debuff>_<size>.png, drawn by tools/icons/gen-effect-placeholders.py — PRE-DRAWN and
// pixel-hinted per size (house icon rule), so the getter picks a size, never scales one master. Bytes and textures are
// cached per (category, kind, size): the steady-state call is two array reads, no allocation.
internal sealed partial class GameAssetsService
{
    // Ascending; must match the generator's SIZES. CombatMeter's meter tile art (18/24/30 units), tooltip 28 and
    // Settings row 20, each at 0.75 / 1 / 1.5 px per unit, plus 16 / 32 / 64.
    private static readonly int[] PlaceholderSizes = { 14, 15, 16, 18, 20, 21, 23, 24, 27, 28, 30, 32, 36, 42, 45, 64 };
    // Index = (int)EffectCategory; must match the generator's GLYPHS stems.
    private static readonly string[] PlaceholderStems = { "default", "cc", "immunity", "shield", "heal", "dot", "up", "down" };
    private static readonly UvRect PlaceholderUv = new(0f, 0f, 1f, 1f);

    // [category * 2 + (debuff ? 1 : 0)][size index]. Bytes: read once (any thread; a race only reads the resource
    // twice). Textures: decoded once on the main thread; a destroyed one (Unity fake-null) is re-decoded.
    private readonly byte[]?[][] _placeholderPng = new byte[]?[PlaceholderStems.Length * 2][];
    private readonly Texture2D?[][] _placeholderTex = new Texture2D?[PlaceholderStems.Length * 2][];

    public object? LoadEffectPlaceholder(EffectCategory category, bool debuff, int sizePx, out UvRect uv)
    {
        uv = PlaceholderUv;
        int set = PlaceholderSet(category, debuff), size = PlaceholderSizeIndex(sizePx);
        var bySize = _placeholderTex[set] ??= new Texture2D?[PlaceholderSizes.Length];
        var tex = bySize[size];
        if (tex != null) return tex;   // Unity == also catches a destroyed texture → re-decode below
        var png = PlaceholderPng(set, size);
        if (png == null) return null;
        // Pixel-hinted art picked for its on-screen size draws ~1:1: no mip chain + Bilinear (WindowBuilder.LoadIcon's
        // exact-size rule). HideAndDontSave: survives UnloadUnusedAssets on scene changes, like the loaded game icons.
        tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
        {
            filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
        };
        if (!ImageConversion.LoadImage(tex, png)) { UnityEngine.Object.Destroy(tex); return null; }
        bySize[size] = tex;
        return tex;
    }

    public byte[]? GetEffectPlaceholderPng(EffectCategory category, bool debuff, int sizePx)
        => PlaceholderPng(PlaceholderSet(category, debuff), PlaceholderSizeIndex(sizePx));

    private static int PlaceholderSet(EffectCategory category, bool debuff)
    {
        int c = (int)category;
        if (c < 0 || c >= PlaceholderStems.Length) c = (int)EffectCategory.Default;
        return c * 2 + (debuff ? 1 : 0);
    }

    // Smallest pre-drawn size ≥ the requested on-screen px; the largest when it is bigger than every size.
    internal static int PlaceholderSizeIndex(int sizePx)
    {
        for (int i = 0; i < PlaceholderSizes.Length; i++)
            if (PlaceholderSizes[i] >= sizePx) return i;
        return PlaceholderSizes.Length - 1;
    }

    private byte[]? PlaceholderPng(int set, int size)
    {
        var bySize = _placeholderPng[set] ??= new byte[]?[PlaceholderSizes.Length];
        var png = bySize[size];
        if (png != null) return png;
        string name = "Stellar.Infrastructure.effects." + PlaceholderStems[set / 2] + ((set & 1) != 0 ? "-debuff_" : "-buff_")
            + PlaceholderSizes[size].ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png";
        try
        {
            using var s = typeof(GameAssetsService).Assembly.GetManifestResourceStream(name);
            if (s == null) { _log.Warning($"[GameAssets] effect placeholder resource missing: {name}"); return null; }
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return bySize[size] = ms.ToArray();
        }
        catch (Exception ex)
        {
            _log.Warning($"[GameAssets] effect placeholder {name} failed to load: {ex.Message}");
            return null;
        }
    }
}
