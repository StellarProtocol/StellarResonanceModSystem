using UnityEngine;

namespace Stellar.Infrastructure.Game;

// InputElement.FocusRequest — on-demand focus with the caret at the END (e.g. after an emoji-picker click took focus
// away). Split from UGuiTextInput.cs (file-size gate).
//
// Timing. The request arrives from FieldBinding.Apply (window apply, ~10 Hz) — always AFTER the click that caused
// it has finished: the window onClick runs the plugin handler and then ClearSelectionAfterClick in the SAME
// listener, and the pointer-down that selected the picker button (deselecting → deactivating this field) is long
// past. So focus is not raced by that click; it is only DEFERRED to the next per-frame Tick (the ticker's Update)
// so it runs on the normal UI cadence, never mid-apply.
// Both field kinds defer the real activation: ActivateInputField only arms m_ShouldActivateNextUpdate, which the
// field's own LateUpdate turns into ActivateInputFieldInternal (EventSystem select + editing on). So focus can only
// be CONFIRMED on the following frame's Tick; if it didn't take (some other code cleared the selection in between),
// activate once more, then give up — a bounded retry, never a loop that fights a user's click-away.
// Caret: legacy InputField.OnFocus SELECT-ALLS on activation (no opt-out on legacy) and TMP resets the caret to 0
// on the deactivation the picker click caused (resetOnDeActivation) — so the caret is put at the END only once focus
// is confirmed, i.e. AFTER activation has done its own selection work. Residual (legacy only): the select-all is live
// for the one frame between the field's LateUpdate and our next Update — a highlight flash, and a keystroke landing
// in exactly that frame could replace the text if EventSystem.Update (OnUpdateSelected key handling) runs before the
// ticker's Update. The RichEmoji (TMP) field has select-all-on-focus off, so it has no such window.
internal sealed partial class UGuiTextInput
{
    private const int FocusReqWaitFrames = 30;   // a request on a not-yet-active field waits ≤ ~0.5 s, then drops
    private const int FocusReqMaxActivations = 2; // first try + one re-assert

    private int _focusReqState;        // 0 idle · 1 pending activation · 2 activated, confirm next frame
    private int _focusReqUntilFrame;   // pending deadline (Time.frameCount)
    private int _focusReqCheckFrame;   // frame on/after which state 2 checks isFocused
    private int _focusReqActivations;

    /// <summary>Ask for keyboard focus with the caret at the end of the text (InputElement.FocusRequest). Applied
    /// on the next <see cref="Tick"/>; a newer request restarts it. No-op for a not-built field.</summary>
    public void RequestFocus()
    {
        if (_field == null && _rich == null) return;
        _focusReqState = 1;
        _focusReqUntilFrame = Time.frameCount + FocusReqWaitFrames;
        _focusReqActivations = 0;
    }

    // Called at the top of Tick, before the focus edge is sampled, so a confirmed focus is reported the same frame.
    private void TickFocusRequest()
    {
        if (_focusReqState == 0) return;
        var frame = Time.frameCount;
        if (_focusReqState == 1)
        {
            // Not active in the hierarchy (collapsed / hidden window): keep retrying until the deadline, then drop
            // — a stale request must not grab the keyboard whenever the field happens to show up later.
            if (TryActivateFocus()) { _focusReqActivations++; _focusReqState = 2; _focusReqCheckFrame = frame + 1; }
            else if (frame > _focusReqUntilFrame) _focusReqState = 0;
            return;
        }
        if (frame < _focusReqCheckFrame) return;   // the field's LateUpdate hasn't run the activation yet
        if (FieldFocused) { CaretToEnd(); _focusReqState = 0; return; }
        _focusReqState = _focusReqActivations < FocusReqMaxActivations ? 1 : 0;   // re-assert once, then give up
    }

    // Collapse any selection and put the caret after the last character (after a trailing <sprite=N> tag for the
    // rich field — it moves by STRING index, see IRichInputField.CaretToEnd).
    private void CaretToEnd()
    {
        if (_field != null) _field.MoveTextEnd(false);
        else _rich?.CaretToEnd();
    }

    private void ResetFocusRequest() { _focusReqState = 0; _focusReqActivations = 0; }
}
