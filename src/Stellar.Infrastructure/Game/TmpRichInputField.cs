using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// TMP text field for InputElement.RichEmoji: the game's chat input keeps <c>&lt;sprite=N&gt;</c> in
/// <c>input_field.text</c> and its TMP field draws the pictures (chat_input_box_view.lua), so this does the same —
/// a <see cref="TMP_InputField"/> with rich text on, drawing tags from the game's chat sprite asset
/// (<see cref="TmpEmojiSprites"/>) in the shipped/system faces (<see cref="TmpFontAssets"/>). Built onto the root
/// UGuiTextInput already boxed (LayoutElement + bg Image, fixed box in single-line):
/// <code>
/// root (UGuiTextInput box · TMP_InputField, targetGraphic = bg)
///  └ Text Area (= textViewport · stretch, inset leftPad left/right · RectMask2D)
///     ├ Placeholder (TextMeshProUGUI, empty)
///     ├ Caret (TMP_SelectionCaret — created by TMP_InputField.OnEnable; a MaskableGraphic, so the mask clips it)
///     └ Text (TextMeshProUGUI · richText · game emoji sprite asset)
/// </code>
/// UGuiTextInput keeps ALL behaviour (newline strip + submit, single-line submit grace, Esc, cursor, focus
/// reporting for the keyboard gate); this class only adapts the field and adds atomic-tag deletion
/// (<see cref="SpriteTagEdit"/>). Game-only file (TMP types): never symlink it into the Mono UI-sandbox.
/// </summary>
internal sealed class TmpRichInputField : IRichInputField, IRichInputPlaceholder, IRichInputLock, IRichInputCaret
{
    private const int FontPx = 13;   // = the legacy field's Text size

    private readonly TMP_InputField _f;
    private readonly TextMeshProUGUI _t;
    private readonly TextMeshProUGUI _ph;
    private readonly RectTransform _viewport;
    private readonly Action<string> _onChanged;
    private string _prev = "";
    private bool _framework;   // our own SetText — never a user edit, so no tag repair
    private bool _repairing;   // the repair's own text write re-enters onValueChanged
    private bool _cjk;
    private bool _hasTag;
    private Color _textColor = Color.black;   // last ApplyStyle colour (the unlocked text colour)
    private bool _locked;
    private float _lockedAlpha = 1f;

    private TmpRichInputField(TMP_InputField f, TextMeshProUGUI t, TextMeshProUGUI ph, RectTransform viewport,
        Action<string> onChanged)
    {
        _f = f; _t = t; _ph = ph; _viewport = viewport; _onChanged = onChanged;
    }

    public bool IsFocused => _f != null && _f.isFocused;

    public string Text => _f != null ? _f.text ?? string.Empty : string.Empty;

    /// <summary>Install as the shared rich-field factory. Idempotent (called from TmpStyledText.Register).</summary>
    public static void Register() => StyledTextFactory.CreateRichInput ??= TryCreate;

    private static IRichInputField? TryCreate(GameObject root, RichInputSpec spec)
    {
        var face = TmpFontAssets.UiRegular ?? TmpFontAssets.UiBold;
        if (face == null) return null;   // no TMP face → caller falls back to the legacy field
        var viewport = NewChild("Text Area", root.transform);
        viewport.AddComponent<RectMask2D>();
        var ph = NewText("Placeholder", viewport.transform, face);
        ph.text = string.Empty; ph.fontStyle = FontStyles.Italic;
        var t = NewText("Text", viewport.transform, face);
        t.richText = true;
        var asset = TmpEmojiSprites.Get(probe: true);   // throttled; the default asset resolves at once in-game
        if (asset != null) t.spriteAsset = asset;

        var f = root.AddComponent<TMP_InputField>();
        // Transition.None for the same reason as the legacy field (AttachField): the default ColorTint would drive
        // the themed bg to WHITE. Set before the enable cycle below so OnEnable's state snap is a no-op.
        f.transition = Selectable.Transition.None;
        f.targetGraphic = spec.Target;
        f.textViewport = viewport.GetComponent<RectTransform>();
        f.textComponent = t;
        f.placeholder = ph;
        f.richText = true;                    // tags render, and the caret maps over visible characters…
        f.isRichTextEditingAllowed = false;   // …so it can never land INSIDE a tag (TMP default, made explicit)
        // No select-all on focus: the composer's draft (often a just-appended emoji) would otherwise be replaced
        // by the next keystroke; a click places the caret, AutoFocus/external sets leave it at the end.
        f.onFocusSelectAll = false;
        // TMP's default caretWidth of 1 scales by the font size (≈0.05 × line height), so at our 13 px it is a
        // sub-pixel quad that rasterizes to nothing: focused, typing works, but no caret shows. 2 draws ~1.5 px.
        f.caretWidth = 2;
        // Same line-type split as the legacy field (UGuiTextInput.ConfigureLineMode): MultiLineNewline never
        // deactivates on Enter (the '\n' is stripped + submitted in onValueChanged); SingleLine finishes + submits.
        // The setter also sets the text's word wrapping (SingleLine = no wrap → TMP scrolls the viewport sideways).
        f.lineType = spec.SingleLine ? TMP_InputField.LineType.SingleLine : TMP_InputField.LineType.MultiLineNewline;
        f.text = string.Empty;
        var h = new TmpRichInputField(f, t, ph, f.textViewport, spec.OnValueChanged);
        f.onValueChanged.AddListener((UnityEngine.Events.UnityAction<string>)(h.OnFieldChanged));
        if (spec.SingleLine) f.onSubmit.AddListener((UnityEngine.Events.UnityAction<string>)(spec.OnSubmit));
        // TMP_InputField creates its caret (and registers its label callbacks) in OnEnable ONLY when a
        // textComponent is already assigned — AddComponent ran OnEnable with none, so cycle it once now.
        f.enabled = false;
        f.enabled = true;
        return h;
    }

    public void SetText(string value, bool caretToEnd)
    {
        value ??= string.Empty;
        _framework = true;
        try
        {
            if (_f.text != value) _f.text = value;   // fires onValueChanged (legacy-setter parity) → owner sees it
            else Restyle(value);
            // stringPosition = STRING index (caretPosition is the visible-character index): end of the raw text,
            // after any trailing tag. Recomputed into the caret by TMP on its next label update.
            if (caretToEnd) _f.stringPosition = value.Length;
        }
        finally { _framework = false; }
        _prev = value;
    }

    public void ApplyStyle(Color textColor, float leftPad)
    {
        _textColor = textColor;
        _t.color = _locked ? Dim(textColor, _lockedAlpha) : textColor;
        _ph.color = new Color(textColor.r, textColor.g, textColor.b, textColor.a * 0.5f);
        // Pad the VIEWPORT (not the text, as the legacy field does) so the RectMask2D clips inside the padding and
        // TMP's sideways scroll keeps the caret clear of the rounded border.
        _viewport.offsetMin = new Vector2(leftPad, _viewport.offsetMin.y);
        _viewport.offsetMax = new Vector2(-leftPad, _viewport.offsetMax.y);
        _f.customCaretColor = true; _f.caretColor = textColor;
    }

    // TMP_InputField already toggles the Placeholder graphic itself (shown while the text is empty). Its face is picked
    // per string like the field text (Restyle): the Placeholder was built on the UI face, which has no CJK glyphs, so a
    // ja/ko hint would draw as boxes.
    public void SetPlaceholder(string text)
    {
        text ??= string.Empty;
        if (_ph.text == text) return;
        var cjk = TextFacePick.For(text) == FaceScript.Cjk;
        var face = cjk ? TmpFontAssets.CjkRegular ?? TmpFontAssets.CjkBold : null;
        face ??= TmpFontAssets.UiRegular ?? TmpFontAssets.UiBold;
        if (face != null && _ph.font != face) { _ph.font = face; CentreLine(_ph); }
        _ph.text = text;
    }

    // InputElement.Locked. TMP's readOnly gates the edit paths only (typing, paste, cut, delete — and so the '\n' the
    // default mode turns into a submit); focus, caret and the framework's own text setter keep working.
    public void SetLocked(bool locked, float textAlpha)
    {
        _locked = locked;
        _lockedAlpha = textAlpha;
        if (_f != null) _f.readOnly = locked;
        if (_t != null) _t.color = locked ? Dim(_textColor, textAlpha) : _textColor;
    }

    private static Color Dim(Color c, float alpha) => new(c.r, c.g, c.b, c.a * alpha);

    public bool TryActivateFocus()
    {
        if (_f == null || !_f.gameObject.activeInHierarchy) return false;
        _f.Select();
        _f.ActivateInputField();
        return true;
    }

    public void Deactivate() => _f?.DeactivateInputField(false);

    // Same STRING-index write SetText(caretToEnd) uses (validated): stringPosition sets both ends of the selection,
    // so it also collapses one. Not MoveTextEnd — with rich text TMP maps it through visible-character indices.
    public void CaretToEnd()
    {
        if (_f == null) return;
        _f.stringPosition = Text.Length;
    }

    // InputElement.InsertRequest (IRichInputCaret): the STRING-index selection (not caretPosition, which counts visible
    // characters — a <sprite=N> tag is one), and the same stringPosition write CaretToEnd uses to collapse it.
    public (int Anchor, int Focus) StringSelection
        => _f == null ? (-1, -1) : (_f.selectionStringAnchorPosition, _f.selectionStringFocusPosition);

    public void SetStringCaret(int index)
    {
        if (_f == null) return;
        _f.stringPosition = Math.Clamp(index, 0, Text.Length);
    }

    // A tag shown before the sprite asset resolved prints literally; once it resolves (throttled probe inside,
    // only while a tag is present) assign it and re-label so the pictures appear without an edit.
    public void Tick()
    {
        PinVerticalScroll();
        if (!_hasTag || _t == null || _t.spriteAsset != null) return;
        var asset = TmpEmojiSprites.Get(probe: true);
        if (asset == null) return;
        _t.spriteAsset = asset;
        _f.ForceLabelUpdate();
    }

    // PageUp/PageDown (2026-10-24: bound as ChatTools tab hotkeys) shifted the composer text up/down. In any mode but
    // SingleLine, TMP_InputField's MovePageUp/MovePageDown ALSO scroll the text: they add
    // (viewport top − text-bounds top) / (… bottom) to textComponent.rectTransform.anchoredPosition.y. Our CentreLine
    // margin puts the glyph bounds `pad` px inside the rect, so each press moved the line by that margin, and it stayed
    // put. The field grows to fit its draft (no vertical scrolling is ever needed while the whole text fits the
    // viewport), so pin y back to 0 whenever it fits. That keeps caret/selection/IME and Left/Right/Home/End untouched,
    // and also covers Up/Down or any other key that nudges it. A field capped shorter than its text (needs a real
    // scroll to keep the caret visible) is left to TMP. Per frame (UGuiTextInput.Tick): the 0 check is one property read; the
    // fit test runs only when a shift actually happened. The order against the EventSystem update is undefined, so a
    // press may show for at most one frame before the pin.
    private void PinVerticalScroll()
    {
        if (_t == null || _viewport == null) return;
        var rt = _t.rectTransform;
        var pos = rt.anchoredPosition;
        if (Mathf.Abs(pos.y) < 0.01f) return;
        if (_t.preferredHeight > _viewport.rect.height + 0.5f) return;   // taller than the box: TMP's caret scroll owns it
        rt.anchoredPosition = new Vector2(pos.x, 0f);
    }

    // User edits first get atomic-tag repair (a partial tag deletion widened to the whole tag, caret placed after
    // the edit), then the owner's handler runs on the final text. The repair's own write re-enters this listener
    // (TMP's text setter notifies) and is swallowed by _repairing — the outer call forwards the repaired text once.
    private void OnFieldChanged(string value)
    {
        if (_repairing) return;
        value ??= string.Empty;
        if (!_framework && SpriteTagEdit.TryRepair(_prev, value, out var repaired, out var caret))
        {
            _repairing = true;
            try { _f.text = repaired; _f.stringPosition = caret; }
            finally { _repairing = false; }
            value = repaired;
        }
        _prev = value;
        Restyle(value);
        _onChanged(value);
    }

    // Per-string face pick, like TmpStyledText.SetText: TMP draws ONE face per string (no cross-script fallback),
    // so Latin/Thai → shipped merged face, CJK/kana/Hangul → system family. Swapped only when the script changes.
    private void Restyle(string value)
    {
        _hasTag = value.IndexOf("<sprite", StringComparison.Ordinal) >= 0;
        var cjk = TextFacePick.For(value) == FaceScript.Cjk && (TmpFontAssets.CjkRegular ?? TmpFontAssets.CjkBold) != null;
        if (cjk == _cjk) return;
        _cjk = cjk;
        var face = cjk ? TmpFontAssets.CjkRegular ?? TmpFontAssets.CjkBold : TmpFontAssets.UiRegular ?? TmpFontAssets.UiBold;
        if (face != null && _t.font != face) { _t.font = face; CentreLine(_t); }
    }

    private static TextMeshProUGUI NewText(string name, Transform parent, TMP_FontAsset face)
    {
        var go = NewChild(name, parent);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.font = face;
        t.fontSize = FontPx;
        t.color = Color.black;
        // TOP-left + an explicit top/bottom margin (CentreLine), NOT TMP's middle-left. Under TMP_InputField the typed
        // text drew with its line box against the TOP of the 28 box (measured off an in-game screenshot 2026-10-24:
        // baseline = box top + the face's ascender, i.e. top-aligned, ~4 px above centre) while the composer pills'
        // labels sat centred. Placing the line ourselves makes the position independent of how TMP_InputField treats
        // the vertical alignment, and the caret (drawn over the line's ascender..descender) is centred with it.
        t.alignment = TextAlignmentOptions.TopLeft;
        t.raycastTarget = false;                   // clicks land on the root bg (the field's targetGraphic)
        CentreLine(t);
        return t;
    }

    // = UGuiTextInput.BuildBox's boxHeight: the one-line box the text area fills (stretch, no vertical inset).
    private const float BoxPx = 28f;

    // Centre ONE line of t's current face in the 28 box: margin top = bottom = (box − line box) / 2, where the line box
    // is the face's ascender..descender at t's size — the same extent TMP lays a text line (and the caret) out on. Per
    // text and re-run on every face swap (the Latin/Thai face and the system CJK face have different metrics). Bottom
    // gets the same margin so a wrapped draft (default mode grows downward from TMP's preferred height, which includes
    // the margins) keeps equal padding above and below. A sprite taller than the face's ascender grows its line a
    // little, which pushes that line's baseline down by the excess.
    private static void CentreLine(TextMeshProUGUI t)
    {
        var face = t.font;
        if (face == null) return;
        var fi = face.faceInfo;
        if (fi.pointSize <= 0f) return;
        float k = t.fontSize / fi.pointSize * (fi.scale > 0f ? fi.scale : 1f);
        float pad = Mathf.Max(0f, (BoxPx - (fi.ascentLine - fi.descentLine) * k) * 0.5f);
        var m = t.margin;
        if (Mathf.Abs(m.y - pad) < 0.01f && Mathf.Abs(m.w - pad) < 0.01f) return;
        t.margin = new Vector4(m.x, pad, m.z, pad);
    }

    private static GameObject NewChild(string name, Transform parent)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        return go;
    }
}
