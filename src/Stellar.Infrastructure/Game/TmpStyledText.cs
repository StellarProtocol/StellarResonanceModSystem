using Stellar.Abstractions.Services;
using TMPro;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// TMP-backed implementation of the shared <see cref="StyledTextFactory"/> hook: styled text (real bold /
/// italic / underline / strikethrough) for window titles, emphasis headers, and styled TextElements in
/// every script — Latin/Thai from the shipped merged faces, CJK/kana/Hangul from a system family
/// (<see cref="TmpFontAssets"/>). Synthetic bold and its blur are never used; bold is a face swap.
/// Game-only file: the Mono UI-sandbox has no TextMeshPro package, so this must never be symlinked into
/// it; the sandbox leaves the factory null and renders the legacy crisp fallback.
/// </summary>
internal sealed class TmpStyledText : IStyledTextHandle
{
    private readonly TextMeshProUGUI _t;
    private readonly bool _bold;

    private TmpStyledText(TextMeshProUGUI t, bool bold) { _t = t; _bold = bold; }

    public GameObject? Go => _t == null ? null : _t.gameObject;

    public void SetText(string s)
    {
        // Re-pick the face per string so a live language switch (EN↔JA↔TH) lands on the right script's
        // face at the right weight. Latin + Thai → shipped merged faces; CJK/kana/Hangul → system family.
        var face = PickFace(s, _bold);
        if (face != null && _t.font != face) _t.font = face;
        _t.text = s;
    }

    public void SetFontSize(int px) => _t.fontSize = px;

    public void SetColor(Color c) => _t.color = c;

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
        if (spec.Underline) styles |= FontStyles.Underline;
        if (spec.Strikethrough) styles |= FontStyles.Strikethrough;
        t.fontStyle = styles;
        var handle = new TmpStyledText(t, spec.Bold);
        handle.SetText(spec.Text);
        return handle;
    }
}
