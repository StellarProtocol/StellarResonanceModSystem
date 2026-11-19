using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// One shared outline material per TMP face, for styled text that needs the dark readability halo
/// (<see cref="StyledTextSpec.Outline"/> — the Thai twin of an outlined legacy Text such as a Modern bar label). A legacy
/// uGUI <c>Outline</c> effect does nothing on a TMP mesh; TMP draws outlines in its SDF shader from material properties,
/// so the cheap, TMP-correct way is a copy of the face's material with the outline turned on, created ONCE per face and
/// shared by every outlined text on it (never a per-instance material — TMP's <c>fontMaterial</c> getter would clone
/// one per text).
/// <para>Look: a ~1 px black 85 % halo like <c>UGuiPrimitives.AddReadabilityOutline</c>. TMP's outline straddles the
/// glyph edge, so <c>_FaceDilate</c> = <c>_OutlineWidth</c> moves its inner edge back onto the original edge and the
/// glyphs keep their weight; their sum stays under 1, so TMP's scale-ratio clamp never thins it. Faces are SDF with
/// padding 5 at 48 pt (TmpFontAssets.Sizing), which leaves that headroom. The Mobile SDF shader only draws the
/// outline with the OUTLINE_ON keyword; the desktop one reads the properties directly — both are set.</para>
/// Main thread only. Materials live for the process (HideAndDontSave), like the faces.
/// </summary>
internal static class TmpOutlineMaterials
{
    private const float Width = 0.4f;   // share of the SDF spread; with the dilate below ≈ 1 px outward at UI sizes
    private static readonly Color Halo = new(0f, 0f, 0f, 0.85f);
    private static readonly Dictionary<int, Material> ByFace = new();
    private static int _outlineWidth, _outlineColor, _faceDilate, _mainTex;

    /// <summary>The face's outline material (built on first use); null when the face or its material is missing.</summary>
    internal static Material? For(TMP_FontAsset? face)
    {
        if (face == null) return null;
        var src = face.material;
        if (src == null) return null;
        int key = face.GetInstanceID();
        if (ByFace.TryGetValue(key, out var m) && m != null)
        {
            // A dynamic face can swap its atlas texture; keep the copy on the face's current one.
            if (m.GetTexture(_mainTex) != src.GetTexture(_mainTex)) m.SetTexture(_mainTex, src.GetTexture(_mainTex));
            return m;
        }
        if (_mainTex == 0)
        {
            _mainTex = Shader.PropertyToID("_MainTex");
            _outlineWidth = Shader.PropertyToID("_OutlineWidth");
            _outlineColor = Shader.PropertyToID("_OutlineColor");
            _faceDilate = Shader.PropertyToID("_FaceDilate");
        }
        m = new Material(src) { name = src.name + " (Stellar outline)", hideFlags = HideFlags.HideAndDontSave };
        m.EnableKeyword("OUTLINE_ON");
        m.SetFloat(_outlineWidth, Width);
        m.SetColor(_outlineColor, Halo);
        m.SetFloat(_faceDilate, Width);
        ByFace[key] = m;
        return m;
    }
}
