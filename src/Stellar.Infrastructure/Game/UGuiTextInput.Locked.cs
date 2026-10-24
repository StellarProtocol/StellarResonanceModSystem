using UnityEngine;

namespace Stellar.Infrastructure.Game;

// InputElement.Locked: a live read-only + dimmed state (e.g. a chat composer while its draft is being translated).
// Stock readOnly (legacy InputField and TMP_InputField alike) gates only the EDIT paths — typing, paste, cut, delete
// and IME — so the default mode's Enter never appends the '\n' that OnFieldValueChanged turns into a submit, while
// focus, caret, selection and the text setter (the FieldBinding re-seed from Get()) keep working. The box keeps its
// size and bg; only the text alpha drops. Split from UGuiTextInput.cs (file-size gate). Pure UnityEngine.UI on the
// legacy path; the TMP field implements it behind IRichInputLock (sandbox-shared code stays TMP-free).
internal sealed partial class UGuiTextInput
{
    private const float LockedTextAlpha = 0.45f;
    private bool _locked;

    /// <summary>Lock (read-only, dimmed text) or unlock the field. Diffed — an unchanged state writes nothing. No-op
    /// when not built.</summary>
    public void SetLocked(bool locked)
    {
        if (_field == null && _rich == null) return;
        if (locked == _locked) return;
        _locked = locked;
        if (_rich != null) { (_rich as IRichInputLock)?.SetLocked(locked, LockedTextAlpha); return; }
        _field!.readOnly = locked;
        if (_field.textComponent == null) return;
        var c = _phTextColor;   // the last ApplyStyle text colour (.Placeholder.cs keeps it for the hint)
        _field.textComponent.color = locked ? new Color(c.r, c.g, c.b, c.a * LockedTextAlpha) : c;
    }
}
