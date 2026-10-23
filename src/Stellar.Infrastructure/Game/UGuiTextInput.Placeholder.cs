using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// InputElement.Placeholder: dimmed hint text shown while the field is empty. Both field kinds already toggle a
// placeholder graphic on their own (InputField / TMP_InputField enable it whenever the text is empty), so this only
// supplies one. The TMP field ships an empty Placeholder child (TmpRichInputField) and just takes the text; the legacy
// field gets a Text child created on the FIRST non-empty hint, so fields without a placeholder build exactly as before.
// Pure UnityEngine.UI, like the rest of UGuiTextInput (sandbox-shared).
internal sealed partial class UGuiTextInput
{
    private Text? _placeholder;                                  // legacy field only; lazily created
    private Color _phTextColor = new(0.92f, 0.94f, 0.97f, 1f);   // last ApplyStyle text colour (hint = half alpha)
    private float _phLeftPad;                                    // last ApplyStyle left pad (hint lines up with text)

    /// <summary>Set the empty-field hint ("" hides it). No-op when not built or in TextArea mode.</summary>
    public void SetPlaceholder(string text)
    {
        text ??= string.Empty;
        if (_rich != null) { (_rich as IRichInputPlaceholder)?.SetPlaceholder(text); return; }
        if (_field == null || _multiLine) return;
        if (_placeholder == null)
        {
            if (text.Length == 0) return;
            _placeholder = CreatePlaceholder();
        }
        _placeholder.text = text;
    }

    // Called from ApplyStyle so a hint built before OR after the theming matches the field text.
    private void RememberPlaceholderStyle(Color textColor, float leftPad)
    {
        _phTextColor = textColor;
        _phLeftPad = leftPad;
        if (_placeholder != null) StylePlaceholder(_placeholder);
    }

    // Same geometry/font as the field's Text (CreateText + SetFont + ApplyStyle) so the hint sits where typing starts.
    // raycastTarget off: clicks must reach the field's bg (its targetGraphic), not the hint.
    private Text CreatePlaceholder()
    {
        var go = NewChild("Placeholder", _field!.transform);
        Stretch(go);
        var ph = go.AddComponent<Text>();
        var src = _field.textComponent;
        ph.font = src != null && src.font != null ? src.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
        ph.fontSize = src != null ? src.fontSize : 13;
        ph.alignment = TextAnchor.MiddleLeft;
        ph.fontStyle = FontStyle.Italic;
        ph.supportRichText = false;
        ph.horizontalOverflow = HorizontalWrapMode.Overflow;   // one line; the single-line box's RectMask2D clips it
        ph.verticalOverflow = VerticalWrapMode.Truncate;
        ph.raycastTarget = false;
        StylePlaceholder(ph);
        _field.placeholder = ph;
        return ph;
    }

    private void StylePlaceholder(Text ph)
    {
        ph.color = new Color(_phTextColor.r, _phTextColor.g, _phTextColor.b, _phTextColor.a * 0.5f);   // = the TMP hint
        var rt = ph.rectTransform;
        rt.offsetMin = new Vector2(_phLeftPad, rt.offsetMin.y);
        rt.offsetMax = new Vector2(-_phLeftPad, rt.offsetMax.y);
    }
}
