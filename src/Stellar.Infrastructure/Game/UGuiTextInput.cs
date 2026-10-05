using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reusable uGUI text field (default / single-line / multi-line) that (1) submits on Enter without the game's
/// chat-open guard (which fires only when no field is focused) ever seeing an unfocused frame — the default mode
/// never loses focus; single-line mode does (uGUI SingleLine deactivates on Enter) but <see cref="IsFocused"/>
/// bridges that gap with a submit grace — and (2) never traps the
/// user: while focused it forces a free, visible cursor (so the user can always click out — replacing the
/// game's Alt-to-free-cursor, which is suppressed during focus) and honours Esc to defocus. Pure
/// UnityEngine.UI (no Il2CppInterop) so it builds identically in the headless UI sandbox and in-game.
/// Keyboard SUPPRESSION stays in KeyboardInputGate, driven by <see cref="IsFocused"/>.
/// NOTE: onValidateInput is intentionally NOT used — its char parameter is truncated to 8 bits across
/// the IL2CPP delegate bridge, breaking non-ASCII input. Newline detection uses onValueChanged instead.
/// </summary>
internal sealed partial class UGuiTextInput
{
    private readonly Action<string>? _onSubmit;
    private readonly Action<bool>? _onFocusChanged;
    private readonly Action<string>? _onChange;   // per-keystroke (live filters); null = submit-only field

    private InputField? _field;
    private bool _wasFocused;
    private bool _savedCursorVisible;
    private CursorLockMode _savedCursorLock;
    private bool _enterLatched;      // one-press-one-submit: blocks key-repeat from firing submit every frame
    private bool _strippingNewline;  // re-entrancy guard for text reset inside onValueChanged
    private bool _multiLine;         // multi-line mode: Enter inserts a real newline + keeps focus (no strip, no submit)
    private bool _singleLine;        // single-line mode: uGUI SingleLine lineType; Enter submits via onSubmit + deactivates
    private bool _submitGrace;       // single-line: a submit just deactivated the field — keep reporting focus (see IsFocused)
    private float _graceReleaseAt = -1f;   // unscaled time Enter was seen released during the grace (-1 = still held)
    private Image? _bg;              // themed bg ApplyStyle tints (= targetGraphic, except TextArea: its outer box)
    private const float MultiLineRowPx = 16f;   // approx line-box height for the 13-px field font; fixes the box height
    private const float SubmitGraceTailSec = 0.15f;   // focus tail after Enter is released (input-update ordering slack)

    public UGuiTextInput(Action<string>? onSubmit = null, Action<bool>? onFocusChanged = null, Action<string>? onChange = null)
    {
        _onSubmit = onSubmit;
        _onFocusChanged = onFocusChanged;
        _onChange = onChange;
    }

    /// <summary>True while the field holds keyboard focus — the signal KeyboardInputGate consumes. In single-line
    /// mode it ALSO stays true through the submit grace (Enter held + a short tail after release), see
    /// <see cref="SubmitGraceActive"/>.</summary>
    public bool IsFocused => _field != null && (_field.isFocused || SubmitGraceActive());

    /// <summary>Current field text (empty when not built).</summary>
    public string Text => _field != null ? _field.text : string.Empty;

    /// <summary>Builds the field under <paramref name="parent"/> and returns its root GameObject.
    /// Visuals mirror the prior raw spike InputField (white bg, black 13px MiddleLeft text).
    /// <para><paramref name="singleLine"/> (opt-in, default false → unchanged) is a TRUE uGUI single line
    /// (<c>lineType = SingleLine</c>): the text never wraps and the caret scrolls the visible window sideways on
    /// long/pasted text, inside a fixed box whose LayoutElement out-ranks InputField's own ILayoutElement
    /// (<c>layoutPriority</c> 2, height pinned) so it can never grow the field — or an auto-height window.
    /// MultiLineNewline can NOT do this (uGUI's EnforceTextHOverflow forces Wrap whenever multiLine). Enter submits
    /// via <c>onSubmit</c>; uGUI then deactivates the field, and the resulting unfocused gap that would flash the
    /// game's chat open is bridged by the submit grace in <see cref="IsFocused"/>.</para>
    /// <para><paramref name="multiLine"/> (opt-in, default false → unchanged; mutually exclusive with
    /// <paramref name="singleLine"/>) is a true multi-line editable box of a FIXED height (<paramref name="lines"/>
    /// visible rows + inner padding) that NEVER grows the window: text WRAPS within the box width, and the field
    /// itself grows inside a ScrollRect (themed scrollbar, mouse wheel, caret-follow) — see UGuiTextInput.TextArea.cs.
    /// Unlike single-line-submit mode, Enter here inserts a REAL newline and keeps focus (no strip, no submit) —
    /// the owner submits via its own button reading <see cref="Text"/> / the onChange buffer.</para></summary>
    public GameObject Build(Transform parent, bool singleLine = false, bool multiLine = false, int lines = 4)
    {
        if (_field != null)
            throw new InvalidOperationException("UGuiTextInput.Build called twice; call Destroy first.");
        _multiLine = multiLine;
        _singleLine = singleLine && !multiLine;   // mutually exclusive (no caller passes both); multiLine wins
        if (multiLine) return BuildTextArea(parent, lines);   // ScrollRect-wrapped growing field (TextArea partial)
        const float boxHeight = 28f;
        var go = NewChild("UGuiTextInput", parent);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = boxHeight;
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.95f, 0.95f, 0.95f, 1f);
        // Single line pins the field to a fixed, clipped box (see helper); default mode is unchanged.
        if (_singleLine) ConfigureFixedBox(go, le, boxHeight);
        var txt = CreateText(go.transform);
        AttachField(go, txt, bg);
        return go;
    }

    // The field's Text child: stretched over its parent, 13-px MiddleLeft, wrap/overflow per mode.
    private Text CreateText(Transform parent)
    {
        var textGo = NewChild("Text", parent);
        Stretch(textGo);
        var txt = textGo.AddComponent<Text>();
        txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        txt.fontSize = 13; txt.color = Color.black; txt.alignment = TextAnchor.MiddleLeft;
        txt.supportRichText = false;
        ConfigureTextOverflow(txt, _singleLine, _multiLine);
        return txt;
    }

    // Adds the InputField to <paramref name="go"/>. <paramref name="target"/> is its targetGraphic AND (default /
    // single-line) the themed bg ApplyStyle tints. TextArea mode sets _bg to its OUTER box before this, and target
    // is then a transparent click/wheel catcher on the growing field.
    private void AttachField(GameObject go, Text txt, Image target)
    {
        _field = go.AddComponent<InputField>();
        _field.textComponent = txt;
        _field.targetGraphic = target;
        _bg ??= target;
        // The field is a Selectable: its DEFAULT ColorTint transition drives targetGraphic.color from the
        // state ColorBlock (normalColor is WHITE), which OVERWRITES the dark themed bg ApplyStyle sets and
        // leaves the light themed text on a white field — unreadable (owner report 2026-08-16: "white bg,
        // white text"; the "sometimes black" is the pre-transition frame). Transition.None keeps the themed
        // colours in every state; the blinking caret is the focus affordance.
        _field.transition = Selectable.Transition.None;
        ConfigureLineMode(_field);
    }

    // Line type + listeners.
    // DEFAULT + MULTI-LINE: MultiLineNewline is the ONLY mode uGUI does NOT deactivate on Enter. SingleLine returns
    // EditState.Finish on Enter -> DeactivateInputField() -> the field loses focus for a frame -> the game (chat
    // opens only when no field is focused) flashes chat open. The default mode therefore stays in MultiLineNewline,
    // strips the '\n' in onValueChanged (full UTF-16 string — no char-level IL2CPP truncation) and submits there;
    // multi-line keeps the '\n'. onValidateInput is NOT used: its char parameter is truncated to 8 bits across the
    // IL2CPP delegate bridge, breaking non-ASCII input (e.g. Thai).
    // SINGLE-LINE: MultiLineNewline can never be a non-wrapping line — uGUI's EnforceTextHOverflow (run on the
    // lineType setter) forces textComponent.horizontalOverflow = Wrap whenever multiLine, overriding our Overflow,
    // and its caret scrolling is line-based (never sideways). So single-line uses the real SingleLine lineType
    // (the InputField default — the setter is a no-op, our Overflow stands), submits from onSubmit (Enter only:
    // Esc sets m_WasCanceled, click-away goes through OnDeselect — neither fires onSubmit), and accepts the
    // Enter deactivation; the chat flash it would cause is neutralised by the submit grace in IsFocused.
    private void ConfigureLineMode(InputField field)
    {
        field.lineType = _singleLine ? InputField.LineType.SingleLine : InputField.LineType.MultiLineNewline;
        field.text = string.Empty;
        field.onValueChanged.AddListener((UnityEngine.Events.UnityAction<string>)(OnFieldValueChanged));
        if (_singleLine) field.onSubmit.AddListener((UnityEngine.Events.UnityAction<string>)(OnFieldSubmit));
    }

    // Single line: pin the field to a FIXED box so it can NEVER grow vertically no matter how much text it holds (a
    // pasted multi-paragraph code would otherwise grow it by wrapping). Pin preferred == min and kill flexibleHeight
    // so layout can't expand it, then clip the text to the box with a RectMask2D — this both stops long/pasted text
    // spilling OUTSIDE the box and gives Unity's InputField a masked viewport so its caret-follow scrolling keeps the
    // caret visible past the edge (sideways). RectMask2D clips descendants (the Text child); the bg Image sits on this
    // same GO (fills the rect exactly) so clipping it to itself is a no-op. Not called in the default mode, so that
    // path is byte-for-byte unchanged. (TextArea pins its OUTER box the same way — see BuildTextArea — but scrolls
    // with a ScrollRect instead of InputField's own draw window.)
    // layoutPriority 2: InputField is ITSELF an ILayoutElement (priority 1) reporting the text's preferred height AND
    // width, and on a priority TIE LayoutUtility takes the MAX — so the text's size beat our pinned box: a long
    // single-line paste grew the field (wider, with Overflow) and the window (validated fix in-game 2026-10-05).
    // Priority 2 makes every value we set win outright — minHeight (Build), preferredHeight/flexibleHeight (here),
    // and preferredWidth/flexibleWidth (BuildInput sets them on this same LayoutElement after Build); unset (-1)
    // values still fall through to InputField. Default mode keeps priority 1 (untouched).
    private static void ConfigureFixedBox(GameObject go, LayoutElement le, float boxHeight)
    {
        le.preferredHeight = boxHeight; le.flexibleHeight = 0f;
        le.layoutPriority = 2;
        go.AddComponent<RectMask2D>();
    }

    // Single/multi line: set the Text wrap/overflow modes so the fixed box holds the text correctly. No-op in the
    // default (neither flag) mode, leaving the MiddleLeft single-line visuals of the prior raw spike unchanged.
    private static void ConfigureTextOverflow(Text txt, bool singleLine, bool multiLine)
    {
        if (singleLine)
        {
            // Overflow horizontally (text runs off the edge instead of wrapping to a new line) and truncate
            // vertically (never add lines). Height is already pinned, so the field holds one line.
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Truncate;
        }
        else if (multiLine)
        {
            // Wrap within the box width (so lines break at the right edge, no sideways scroll) and overflow
            // DOWNWARD — the field grows to its text height inside a ScrollRect (UGuiTextInput.TextArea.cs).
            // Top-left so content starts at the top like a normal text area (MiddleLeft would vertically centre
            // short content, which reads oddly for a multi-line box).
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.alignment = TextAnchor.UpperLeft;
        }
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
        if (_bg is { } img)
        {
            img.color = bgColor;
            if (bgSprite != null) { img.sprite = bgSprite; img.type = Image.Type.Sliced; }
        }
        if (_field.textComponent != null)
        {
            _field.textComponent.color = textColor;
            var rt = _field.textComponent.rectTransform;
            rt.offsetMin = new Vector2(leftPad, rt.offsetMin.y);
            rt.offsetMax = new Vector2(-(leftPad + _textRightReserve), rt.offsetMax.y);   // reserve: TextArea bar lane
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
        if (!EnterHeld()) _enterLatched = false;
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
        if (_multiLine) TickTextArea(focused);

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
        _submitGrace = false;
        _field = null;
        _bg = null; _scroll = null; _fieldRect = null; _textRightReserve = 0f;   // TextArea state (.TextArea.cs)
        _lastFieldSize = new Vector2(-1f, -1f); _lastCaret = -1; _followUntilFrame = -1;
    }

    // onValueChanged fires AFTER a char is committed to the field (full UTF-16 string — no IL2CPP char
    // truncation). When Enter is pressed in MultiLineNewline mode, '\n' is appended before this fires.
    // SINGLE-LINE-SUBMIT mode: we strip the newline, reset the field text, and submit — so Enter acts as submit
    // WITHOUT deactivating the field (chat never flashes open). The _strippingNewline guard prevents processing
    // the re-entrant onValueChanged that fires when we assign _field.text = clean.
    // MULTI-LINE mode: Enter is a real newline, so we must NOT strip it and must NOT submit — the strip+submit
    // branch is skipped and the '\n' flows straight through to _onChange (full buffer, newlines kept).
    // SINGLE-LINE mode: Enter never reaches here (SingleLine finishes the edit before appending), so a newline can
    // only come from a PASTE — strip it (a multi-line code collapses to one line; done here rather than trusting
    // the uGUI version's own paste filtering) and pass the clean text on as a CHANGE, never a submit.
    private void OnFieldValueChanged(string value)
    {
        if (_strippingNewline) return;
        if (!_multiLine && (value.Contains('\n') || value.Contains('\r')))
        {
            var clean = value.Replace("\r", "").Replace("\n", "");
            _strippingNewline = true;
            try { if (_field != null) _field.text = clean; }
            finally { _strippingNewline = false; }
            if (_singleLine) _onChange?.Invoke(clean);
            else if (!_enterLatched) { _enterLatched = true; _onSubmit?.Invoke(clean); }
            return;
        }
        if (_multiLine) OnTextAreaChanged();
        _onChange?.Invoke(value);
    }

    // SINGLE-LINE submit: onSubmit fires on Enter only. Same one-press-one-submit latch as the default mode (Tick
    // releases it once Enter is up). uGUI deactivates the field right after this returns, so arm the submit grace:
    // IsFocused keeps reporting focus while Enter is held, so KeyboardInputGate (sampled on the THROTTLED framework
    // tick, which can land in the unfocused gap) never un-suppresses the game keyboard with Enter still down.
    private void OnFieldSubmit(string value)
    {
        _submitGrace = true; _graceReleaseAt = -1f;
        if (_enterLatched) return;
        _enterLatched = true;
        _onSubmit?.Invoke(value);
    }

    // Submit grace: true from a single-line submit until Enter has been released for SubmitGraceTailSec (the tail
    // covers Rewired-vs-framework-tick update ordering around the key-up). Polled live from IsFocused, so it ends on
    // its own even if Tick stops; it is armed ONLY by a submit, so Esc / click-away blurs report unfocused at once.
    private bool SubmitGraceActive()
    {
        if (!_submitGrace) return false;
        if (EnterHeld()) { _graceReleaseAt = -1f; return true; }
        var now = Time.unscaledTime;
        if (_graceReleaseAt < 0f) _graceReleaseAt = now;
        if (now - _graceReleaseAt < SubmitGraceTailSec) return true;
        _submitGrace = false;
        return false;
    }

    private static bool EnterHeld() => Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter);

    // DeactivateInputField fires onEndEdit — no listener is registered (submit is handled in onValueChanged /
    // onSubmit, and onSubmit does NOT fire on Esc), so this is safe; note the coupling if an onEndEdit listener is
    // ever added.
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
