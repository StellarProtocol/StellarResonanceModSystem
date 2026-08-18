using Stellar.Abstractions.Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// TMP-backed implementation of the shared <see cref="StyledTextFactory"/> hook: styled text (real bold /
/// italic / underline / strikethrough) for window titles, emphasis headers, and styled TextElements in
/// every script — Latin/Thai from the shipped merged faces, CJK/kana/Hangul from a system family
/// (<see cref="TmpFontAssets"/>). Synthetic bold and its blur are never used; bold is a face swap.
/// UNDERLINE and STRIKETHROUGH are drawn by the framework as Image lines, NOT via TMP: this game's TMP
/// build renders decoration quads whose atlas texels can be blank/relocated by dynamic-atlas repacking,
/// and a ~1px quad can miss every pixel centre ("underline appears at font scale 0.98, vanishes at
/// 1.00" — root-caused in-game 2026-08-18). Owned lines are ≥1.6px (always cover a pixel centre) and
/// script-aware: CJK ideographs fill the em box lower than Latin, so their underline sits lower, and
/// the strike line rides at the glyph-box middle in every script (owner corrections 2026-08-18).
/// Game-only file: the Mono UI-sandbox has no TextMeshPro package, so this must never be symlinked into
/// it; the sandbox leaves the factory null and renders the legacy crisp fallback.
/// </summary>
internal sealed class TmpStyledText : IStyledTextHandle
{
    private readonly TextMeshProUGUI _t;
    private readonly bool _bold;
    private readonly RectTransform? _ul;
    private readonly Image? _ulImg;
    private readonly RectTransform? _st;
    private readonly Image? _stImg;
    private bool _cjk;

    private TmpStyledText(TextMeshProUGUI t, bool bold,
        (RectTransform? Rt, Image? Img) underline, (RectTransform? Rt, Image? Img) strike)
    {
        _t = t; _bold = bold;
        _ul = underline.Rt; _ulImg = underline.Img;
        _st = strike.Rt; _stImg = strike.Img;
    }

    public GameObject? Go => _t == null ? null : _t.gameObject;

    public void SetText(string s)
    {
        // Re-pick the face per string so a live language switch (EN↔JA↔TH) lands on the right script's
        // face at the right weight. Latin + Thai → shipped merged faces; CJK/kana/Hangul → system family.
        _cjk = TextFacePick.For(s) == FaceScript.Cjk;
        var face = _cjk && TmpFontAssets.CjkBold != null
            ? (_bold ? TmpFontAssets.CjkBold : TmpFontAssets.CjkRegular ?? TmpFontAssets.CjkBold)
            : _bold ? TmpFontAssets.UiBold : TmpFontAssets.UiRegular ?? TmpFontAssets.UiBold;
        if (face != null && _t.font != face) _t.font = face;
        _t.text = s;
        FitDecorations();
    }

    public void SetFontSize(int px)
    {
        _t.fontSize = px;
        FitDecorations();
    }

    public void SetColor(Color c)
    {
        _t.color = c;
        if (_ulImg != null) _ulImg.color = c;
        if (_stImg != null) _stImg.color = c;
    }

    public void Refresh()
    {
        // One forced regeneration shortly after first paint irons out TMP first-generation quirks,
        // then re-fit the owned decoration lines to the settled text width.
        try { if (_t != null) { _t.ForceMeshUpdate(); FitDecorations(); } }
        catch { /* mid-destroy — next poll skips via Go */ }
    }

    // Owned decoration lines: width tracks the text's preferred width; offsets/thickness scale with the
    // font size (y is relative to the line-box centre; constants measured in-game at 14px). Height floor
    // 1.6px: a quad ≤1px tall can sit entirely between pixel CENTRES and rasterize to NOTHING. Underline
    // sits lower for CJK (ideographs fill the em box to ~0.48em below centre; Latin/Thai leave descender
    // space); the strike line rides at the glyph-box middle ("higher" than TMP's own — owner correction).
    private void FitDecorations()
    {
        var size = _t.fontSize;
        var w = Mathf.Max(1f, _t.preferredWidth);
        var h = Mathf.Max(1.6f, size / 9f);
        if (_ul != null)
        {
            _ul.sizeDelta = new Vector2(w, h);
            _ul.anchoredPosition = new Vector2(0f, (_cjk ? -0.62f : -0.46f) * size);
        }
        if (_st != null)
        {
            // CJK ideographs centre higher in the line box than Latin/Thai lowercase bodies — the strike
            // rides each script's visual middle (owner-tuned 2026-08-18: "vertical center of the word").
            _st.sizeDelta = new Vector2(w, h);
            _st.anchoredPosition = new Vector2(0f, (_cjk ? 0.04f : -0.08f) * size);
        }
    }

    /// <summary>Install as the shared styled-text factory. Idempotent.</summary>
    public static void Register() => StyledTextFactory.CreateBold ??= TryCreate;

    private static IStyledTextHandle? TryCreate(Transform parent, StyledTextSpec spec)
    {
        if (TmpFontAssets.UiBold == null) return null;
        // A CJK string with no resolvable CJK face would tofu on the merged Latin+Thai face — hand it
        // back to the legacy crisp path instead (in practice a candidate always resolves; see assets).
        if (TextFacePick.For(spec.Text) == FaceScript.Cjk && TmpFontAssets.CjkBold == null) return null;
        var go = new GameObject("StyledText");
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one;
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = spec.FontSize;
        t.color = spec.Color;
        t.raycastTarget = false;
        t.enableWordWrapping = spec.Wrap;
        t.alignment = spec.Align switch
        {
            TextAlign.Center => TextAlignmentOptions.Center,
            TextAlign.Right => TextAlignmentOptions.Right,
            _ => TextAlignmentOptions.Left,
        };
        if (spec.Italic) t.fontStyle = FontStyles.Italic;
        var ul = spec.Underline ? BuildLine(go.transform, "Underline", spec.Color) : (null, null);
        var st = spec.Strikethrough ? BuildLine(go.transform, "Strike", spec.Color) : (null, null);
        var handle = new TmpStyledText(t, spec.Bold, ul, st);
        handle.SetText(spec.Text);
        return handle;
    }

    // A plain uGUI Image line as a free-positioned child of the text (the text GO has no layout group,
    // so the line never participates in the row/column layout). Left-anchored at the vertical centre —
    // matches TextAlignmentOptions.Left; Center/Right-aligned decorated text is not used by the UI.
    private static (RectTransform?, Image?) BuildLine(Transform textGo, string name, Color color)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(textGo, false);
        go.transform.localScale = Vector3.one;
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
        return (rt, img);
    }
}
