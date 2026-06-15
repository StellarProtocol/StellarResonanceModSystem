using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reusable single-line uGUI text field that (1) submits on Enter WITHOUT losing focus — so the game's
/// chat-open guard (which fires only when no field is focused) never triggers — and (2) never traps the
/// user: while focused it forces a free, visible cursor (so the user can always click out — replacing the
/// game's Alt-to-free-cursor, which is suppressed during focus) and honours Esc to defocus. Pure
/// UnityEngine.UI (no Il2CppInterop) so it builds identically in the headless UI sandbox and in-game.
/// Keyboard SUPPRESSION stays in KeyboardInputGate, driven by <see cref="IsFocused"/>.
/// </summary>
internal sealed class UGuiTextInput
{
    private readonly Action<string>? _onSubmit;
    private readonly Action<bool>? _onFocusChanged;
    private readonly Action<string>? _onChange;   // per-keystroke (live filters); null = submit-only field

    private InputField? _field;
    private bool _wasFocused;
    private bool _savedCursorVisible;
    private CursorLockMode _savedCursorLock;
    private bool _enterLatched;   // one-press-one-submit: blocks key-repeat from firing submit every frame

    public UGuiTextInput(Action<string>? onSubmit = null, Action<bool>? onFocusChanged = null, Action<string>? onChange = null)
    {
        _onSubmit = onSubmit;
        _onFocusChanged = onFocusChanged;
        _onChange = onChange;
    }

    /// <summary>True while the field holds keyboard focus — the signal KeyboardInputGate consumes.</summary>
    public bool IsFocused => _field != null && _field.isFocused;

    /// <summary>Current field text (empty when not built).</summary>
    public string Text => _field != null ? _field.text : string.Empty;

    /// <summary>Builds the single-line field under <paramref name="parent"/> and returns its root GameObject.
    /// Visuals mirror the prior raw spike InputField (white bg, black 13px MiddleLeft text).</summary>
    public GameObject Build(Transform parent)
    {
        if (_field != null)
            throw new InvalidOperationException("UGuiTextInput.Build called twice; call Destroy first.");
        var go = NewChild("UGuiTextInput", parent);
        go.AddComponent<LayoutElement>().minHeight = 28f;
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.95f, 0.95f, 0.95f, 1f);

        var textGo = NewChild("Text", go.transform);
        Stretch(textGo);
        var txt = textGo.AddComponent<Text>();
        txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        txt.fontSize = 13; txt.color = Color.black; txt.alignment = TextAnchor.MiddleLeft;
        txt.supportRichText = false;

        _field = go.AddComponent<InputField>();
        _field.textComponent = txt;
        _field.targetGraphic = bg;
        // MultiLineNewline is the ONLY mode uGUI does NOT deactivate on Enter. SingleLine returns
        // EditState.Finish on Enter -> DeactivateInputField() runs right after onEndEdit -> the field
        // loses focus for a frame -> the game (chat opens only when no field is focused) flashes chat
        // open then closed. We keep focus by staying in MultiLineNewline and swallowing the newline in
        // OnValidateInput, firing submit there — so the field never deactivates and chat never opens,
        // while remaining visually + behaviourally single-line (no newline is ever insertable).
        _field.lineType = InputField.LineType.MultiLineNewline;
        _field.text = string.Empty;
        _field.onValidateInput = (InputField.OnValidateInput)OnValidateInput;
        if (_onChange != null) _field.onValueChanged.AddListener((UnityEngine.Events.UnityAction<string>)(s => _onChange(s)));
        return go;
    }

    /// <summary>Override the field's font (the builtin Arial set in Build is absent from IL2CPP player
    /// builds — see WindowThemeAssets.MenuFont). No-op when null or not built.</summary>
    public void SetFont(Font? font)
    {
        if (_field?.textComponent != null && font != null) _field.textComponent.font = font;
    }

    /// <summary>Seed the field text (e.g. from a window InputElement's Get()). No-op if not built.</summary>
    public void SetText(string value)
    {
        if (_field != null) _field.text = value ?? string.Empty;
    }

    /// <summary>Re-theme the field after Build (the default is the spike's white box). Window fields call
    /// this with a dark rounded sprite + light text + left padding so the field matches the chrome.</summary>
    public void ApplyStyle(Sprite? bgSprite, Color bgColor, Color textColor, float leftPad)
    {
        if (_field == null) return;
        if (_field.targetGraphic is Image img)
        {
            img.color = bgColor;
            if (bgSprite != null) { img.sprite = bgSprite; img.type = Image.Type.Sliced; }
        }
        if (_field.textComponent != null)
        {
            _field.textComponent.color = textColor;
            var rt = _field.textComponent.rectTransform;
            rt.offsetMin = new Vector2(leftPad, rt.offsetMin.y);
            rt.offsetMax = new Vector2(-leftPad, rt.offsetMax.y);
        }
        _field.customCaretColor = true; _field.caretColor = textColor;
    }

    /// <summary>Per-frame: while focused, force a free/visible cursor (escape hatch) and honour Esc to
    /// defocus; restore the prior cursor state on blur. Safe to call every frame; no-op if not built.</summary>
    public void Tick()
    {
        if (_field == null) return;
        // Release the submit latch once Enter is no longer held, so the NEXT press submits again
        // (held Enter / OS key-repeat fires one submit, not one per frame).
        if (!Input.GetKey(KeyCode.Return) && !Input.GetKey(KeyCode.KeypadEnter)) _enterLatched = false;
        var focused = _field.isFocused;

        if (focused && !_wasFocused)
        {
            _savedCursorVisible = Cursor.visible;
            _savedCursorLock = Cursor.lockState;
            _onFocusChanged?.Invoke(true);
        }
        else if (!focused && _wasFocused)
        {
            Cursor.visible = _savedCursorVisible;
            Cursor.lockState = _savedCursorLock;
            _onFocusChanged?.Invoke(false);
        }
        _wasFocused = focused;

        if (!focused) return;
        // Replace the (suppressed) Alt-to-free-cursor: keep the cursor free so the user can always click out.
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        if (Input.GetKeyDown(KeyCode.Escape)) Defocus();
    }

    /// <summary>Restores the cursor if we were forcing it free, then drops the field ref. Call before the
    /// owning canvas is destroyed so we never strand a forced-free cursor.</summary>
    public void Destroy()
    {
        if (_wasFocused)
        {
            Cursor.visible = _savedCursorVisible;
            Cursor.lockState = _savedCursorLock;
        }
        _wasFocused = false;   // always reset so a later Build() on a reused instance is clean
        _enterLatched = false;
        _field = null;
    }

    // Per-character validation hook uGUI calls before committing a typed char. In MultiLineNewline mode,
    // Enter routes a '\n' through here (it would otherwise be inserted as a newline). We fire submit on
    // that Enter and return '\0' to swallow it — nothing is inserted, the field stays single-line, and
    // because no submit/Finish occurs the field never deactivates (so the game never opens chat). All
    // other characters pass through unchanged. Blur (click-away / Esc) still works via the deselect path.
    private char OnValidateInput(string text, int charIndex, char addedChar)
    {
        if (addedChar == '\n' || addedChar == '\r')
        {
            // `text` is the pre-commit field contents (the '\n' isn't inserted yet) — the value to submit.
            if (!_enterLatched) { _enterLatched = true; _onSubmit?.Invoke(text); }   // once per press; Tick releases on key-up
            return '\0';
        }
        return addedChar;
    }

    // DeactivateInputField fires onEndEdit — no listener is registered (submit is handled in
    // OnValidateInput), so this is safe; note the coupling if an onEndEdit listener is ever added.
    private void Defocus()
    {
        _field?.DeactivateInputField();
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
    }

    private static GameObject NewChild(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.localScale = Vector3.one;
        return go;
    }

    private static void Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }
}
