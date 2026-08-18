using TMPro;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// TMP-backed implementation of the shared <see cref="StyledTextFactory"/> hook: REAL-bold text for
/// window titles and emphasis headers in every script — Latin/Thai from the shipped merged bold face,
/// CJK/kana/Hangul from a system bold family (<see cref="TmpFontAssets"/>). Synthetic bold and its
/// blur are never used. Game-only file: the Mono UI-sandbox has no TextMeshPro package, so this must
/// never be symlinked into it; the sandbox leaves the factory null and renders the legacy crisp fallback.
/// </summary>
internal sealed class TmpStyledText : IStyledTextHandle
{
    private readonly TextMeshProUGUI _t;

    private TmpStyledText(TextMeshProUGUI t) => _t = t;

    public GameObject? Go => _t == null ? null : _t.gameObject;

    public void SetText(string s)
    {
        // Re-pick the face per string so a live language switch (EN↔JA↔TH) lands on the right script's
        // real bold. Latin + Thai → the shipped merged face; CJK/kana/Hangul → the system bold family.
        var face = TextFacePick.For(s) == FaceScript.Cjk && TmpFontAssets.CjkBold != null
            ? TmpFontAssets.CjkBold
            : TmpFontAssets.UiBold;
        if (face != null && _t.font != face) _t.font = face;
        _t.text = s;
    }

    public void SetFontSize(int px) => _t.fontSize = px;

    public void SetColor(Color c) => _t.color = c;

    /// <summary>Install as the shared bold-text factory. Idempotent.</summary>
    public static void Register() => StyledTextFactory.CreateBold ??= TryCreate;

    private static IStyledTextHandle? TryCreate(Transform parent, StyledTextSpec spec)
    {
        if (TmpFontAssets.UiBold == null) return null;
        // A CJK string with no resolvable CJK bold face would tofu on the merged Latin+Thai face — hand
        // it back to the legacy crisp path instead (in practice a candidate always resolves; see assets).
        if (TextFacePick.For(spec.Text) == FaceScript.Cjk && TmpFontAssets.CjkBold == null)
            return null;
        var go = new GameObject("BoldText");
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one;
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = spec.FontSize;
        t.color = spec.Color;
        t.raycastTarget = false;
        t.enableWordWrapping = spec.Wrap;
        t.alignment = TextAlignmentOptions.Left;   // vertical-middle horizontal-left, matches legacy MiddleLeft
        var handle = new TmpStyledText(t);
        handle.SetText(spec.Text);
        return handle;
    }
}
