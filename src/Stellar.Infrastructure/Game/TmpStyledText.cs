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
/// UNDERLINE is drawn by the framework as a 1px Image line, NOT via TMP: this game's TMP build renders
/// the underline segment's quads with correct geometry but samples atlas texels that dynamic-atlas
/// repacking can leave blank or relocate, so TMP underlines appear nondeterministically (root-caused
/// in-game 2026-08-18 — EN/JA drew while TH didn't, varying per run; strikethrough is unaffected).
/// Game-only file: the Mono UI-sandbox has no TextMeshPro package, so this must never be symlinked into
/// it; the sandbox leaves the factory null and renders the legacy crisp fallback.
/// </summary>
internal sealed class TmpStyledText : IStyledTextHandle
{
    private readonly TextMeshProUGUI _t;
    private readonly bool _bold;
    private readonly RectTransform? _ul;
    private readonly Image? _ulImg;

    private TmpStyledText(TextMeshProUGUI t, bool bold, RectTransform? ul, Image? ulImg)
    {
        _t = t; _bold = bold; _ul = ul; _ulImg = ulImg;
    }

    public GameObject? Go => _t == null ? null : _t.gameObject;

    public void SetText(string s)
    {
        // Re-pick the face per string so a live language switch (EN↔JA↔TH) lands on the right script's
        // face at the right weight. Latin + Thai → shipped merged faces; CJK/kana/Hangul → system family.
        var face = PickFace(s, _bold);
        if (face != null && _t.font != face) _t.font = face;
        _t.text = s;
        FitUnderline();
    }

    public void SetFontSize(int px)
    {
        _t.fontSize = px;
        FitUnderline();
    }

    public void SetColor(Color c)
    {
        _t.color = c;
        if (_ulImg != null) _ulImg.color = c;
    }

    public void Refresh()
    {
        // One forced regeneration shortly after first paint irons out TMP first-generation quirks
        // (decoration segments), then re-fit the owned underline to the settled text width.
        try { if (_t != null) { _t.ForceMeshUpdate(); FitUnderline(); } }
        catch { /* mid-destroy — next poll skips via Go */ }
    }

    // The owned underline: width tracks the text's preferred width; the vertical offset and thickness
    // scale with font size (offset measured against TMP's own underline geometry at 14px). Height floor
    // 1.6px: a quad ≤1px tall can sit entirely between pixel CENTERS and rasterize to NOTHING — the
    // root cause of the "underline appears at font scale 0.98 and vanishes at 1.00" bug (owner-observed
    // 2026-08-18; TMP's own 1.09px underline quad had the same fate). An interval >1px always covers a
    // pixel center, so ≥1.6px is always visible.
    private void FitUnderline()
    {
        if (_ul == null) return;
        var size = _t.fontSize;
        _ul.sizeDelta = new Vector2(Mathf.Max(1f, _t.preferredWidth), Mathf.Max(1.6f, size / 9f));
        _ul.anchoredPosition = new Vector2(0f, -0.46f * size);
    }

    // Weight is a REAL-face swap; a missing regular/CJK face degrades to the nearest available shipped
    // face (never null when TryCreate admitted the element).
    private static TMP_FontAsset? PickFace(string s, bool bold)
        => TextFacePick.For(s) == FaceScript.Cjk
            ? (bold ? TmpFontAssets.CjkBold : TmpFontAssets.CjkRegular ?? TmpFontAssets.CjkBold)
            : bold ? TmpFontAssets.UiBold : TmpFontAssets.UiRegular ?? TmpFontAssets.UiBold;

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
        var styles = FontStyles.Normal;
        if (spec.Italic) styles |= FontStyles.Italic;
        if (spec.Strikethrough) styles |= FontStyles.Strikethrough;   // strike renders reliably as a style
        t.fontStyle = styles;
        RectTransform? ul = null;
        Image? ulImg = null;
        if (spec.Underline) (ul, ulImg) = BuildUnderline(go.transform, spec.Color);
        var handle = new TmpStyledText(t, spec.Bold, ul, ulImg);
        handle.SetText(spec.Text);
        return handle;
    }

    // A plain uGUI Image line as a free-positioned child of the text (the text GO has no layout group,
    // so the line never participates in the row/column layout). Left-anchored at the vertical centre —
    // matches TextAlignmentOptions.Left; Center/Right-aligned underlined text is not used by the UI.
    private static (RectTransform, Image) BuildUnderline(Transform textGo, Color color)
    {
        var go = new GameObject("Underline");
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
