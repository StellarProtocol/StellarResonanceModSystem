using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Stellar.Infrastructure.Game;

// IME composition mode while a Stellar field is focused (Korean 한/영 VK_HANGUL bug, 2026-10-27). Unity only hands the
// OS IME to the app while Input.imeCompositionMode allows it; the game (no imm32 code of its own) holds it at
// Off/Auto unless one of ITS fields (ZTMPInputField via ZTMPInputFieldKeyMgr.updateInputMode) is focused — so with
// our field focused there was no IME context and the 한/영 key did nothing, while it worked in the game's chat box.
// Fix: force IMECompositionMode.On for the focus session, re-asserting each tick (the game may reset it per frame),
// and restore the pre-focus mode on blur/destroy. Split from UGuiTextInput.cs (file-size gate).
// ONE owner at a time: when two Stellar fields hand focus to each other the per-instance edges can land in either
// order within a frame, so the remembered mode is STATIC (the pre-Stellar mode, captured only when nobody owns it)
// and only the current owner restores — the losing field can never stomp the gaining field's On.
internal sealed partial class UGuiTextInput
{
    private static UGuiTextInput? _imeOwner;
    private static IMECompositionMode _imeSaved;
    private bool _imeResetLogged;   // per focus session: "the game reset it" logged once

    // Focus-gained edge (called from Tick).
    private void ImeOnFocusGained()
    {
        try
        {
            var cur = Input.imeCompositionMode;
            // Another Stellar field still owns it (focus hand-off, its lost edge not ticked yet) → inherit ITS saved
            // pre-Stellar mode rather than capturing the On it set.
            if (_imeOwner == null) _imeSaved = cur;
            _imeOwner = this;
            _imeResetLogged = false;
            StyledTextFactory.Log?.Invoke($"[TextInput] IME mode on focus: {cur}");
            SetImeOn();
        }
        catch (Exception) { /* never throw out of Tick */ }
    }

    // Every tick while focused (after the gained edge): re-assert On if the game reset it. Cheap — one static read.
    private void ImeTickFocused()
    {
        try
        {
            if (_imeOwner == null) { ImeOnFocusGained(); return; }   // owner released by a stale hand-off — reclaim
            if (_imeOwner != this) return;
            var cur = Input.imeCompositionMode;
            if (cur == IMECompositionMode.On) return;
            if (!_imeResetLogged)
            {
                _imeResetLogged = true;
                StyledTextFactory.Log?.Invoke($"[TextInput] IME mode reset by game to {cur}, re-asserted");
            }
            SetImeOn();
        }
        catch (Exception) { /* never throw out of Tick */ }
    }

    // Focus-lost edge AND Destroy-while-focused: restore only if we still own it (a field that gained focus in the
    // same frame already took ownership and keeps its On).
    private void ImeRelease()
    {
        try
        {
            if (_imeOwner != this) return;
            _imeOwner = null;
            Input.imeCompositionMode = _imeSaved;
            SetModuleIme(_imeSaved);
        }
        catch (Exception) { /* ownership already dropped above — nothing to unwind */ }
    }

    private static void SetImeOn()
    {
        Input.imeCompositionMode = IMECompositionMode.On;
        SetModuleIme(IMECompositionMode.On);
    }

    // The input module reads through its BaseInput, whose property normally just forwards to Input — but a module
    // with an inputOverride (custom BaseInput) could hold its own value, so set it too. Explicit null checks (Unity
    // objects: no ?. chains — they bypass Unity's destroyed-object null).
    private static void SetModuleIme(IMECompositionMode mode)
    {
        var es = EventSystem.current;
        if (es == null) return;
        var module = es.currentInputModule;
        if (module == null) return;
        var input = module.input;
        if (input == null) return;
        if (input.imeCompositionMode != mode) input.imeCompositionMode = mode;
    }
}
