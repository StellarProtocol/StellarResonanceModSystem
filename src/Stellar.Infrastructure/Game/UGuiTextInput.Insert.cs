namespace Stellar.Infrastructure.Game;

// InputElement.InsertRequest — insert text at the caret (an emoji picker), replacing a selection, then focus with the
// caret right after it. Split from UGuiTextInput.cs (file-size gate).
//
// The caret has to be REMEMBERED: by the time the request arrives the picker click has already taken focus away, and
// TMP_InputField resets its caret to 0 on deactivation (legacy InputField select-alls on the next focus). So Tick
// records the string selection every frame the field is focused (the deactivation frame already reads unfocused, so
// the last focused frame's value survives). An external text replace (SetText from Get(), e.g. the draft cleared after
// a send) forgets it — the old indices would point into different text — and a never-focused field has none: both
// insert at the END. The text is written through the field's own setter, so onValueChanged → OnChange reports it
// like a typed edit (the owner's bound value follows and FieldBinding sees no external change). The caret is placed
// once focus is confirmed (UGuiTextInput.FocusRequest.cs), for the same reason FocusRequest moves it only then.
internal sealed partial class UGuiTextInput
{
    private int _selAnchor = -1, _selFocus = -1;   // last string selection while focused (-1 = unknown → end)
    private int _pendingCaret = -1;                // caret to place once the insert's focus is confirmed (-1 = none)

    // From Tick, only while focused.
    private void TrackSelection()
    {
        if (_field != null) { _selAnchor = _field.selectionAnchorPosition; _selFocus = _field.selectionFocusPosition; }
        else if (_rich is IRichInputCaret c) (_selAnchor, _selFocus) = c.StringSelection;
    }

    private void ForgetSelection() { _selAnchor = _selFocus = -1; }

    /// <summary>Insert <paramref name="text"/> at the remembered caret / over the remembered selection (never inside a
    /// sprite tag; unknown → end), then focus with the caret after it. No-op for a not-built field.</summary>
    public void InsertAtCaret(string text)
    {
        if (_field == null && _rich == null) return;
        var next = SpriteTagEdit.InsertAt(Text, _selAnchor, _selFocus, text, out var caret);
        if (_field != null) _field.text = next;
        else _rich!.SetText(next, caretToEnd: false);
        _selAnchor = _selFocus = caret;   // a second pick before refocusing lands after the first
        _pendingCaret = caret;
        RequestFocus();
    }

    // Focus confirmed (TickFocusRequest): the insert's caret if one is pending, else the plain FocusRequest end.
    private void PlaceCaretAfterFocus()
    {
        if (_pendingCaret < 0) { CaretToEnd(); return; }
        if (_field != null) { _field.selectionAnchorPosition = _pendingCaret; _field.selectionFocusPosition = _pendingCaret; }
        else if (_rich is IRichInputCaret c) c.SetStringCaret(_pendingCaret);
        else _rich?.CaretToEnd();
        _pendingCaret = -1;
    }
}
