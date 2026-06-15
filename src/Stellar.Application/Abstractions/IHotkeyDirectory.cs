using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Framework-internal hotkey rebinding API consumed by the HotkeysPanel.
/// Plugins use <c>IHotkeys</c> (1 member, declare-only); this interface
/// surfaces the rebind primitives so the Settings UI can edit any action.
/// </summary>
internal interface IHotkeyDirectory
{
    IReadOnlyList<IHotkeyAction> Actions { get; }

    /// <summary>Set or clear the binding for an action; passing <c>null</c> unbinds.</summary>
    void Rebind(string actionId, KeyBinding? newBinding);

    /// <summary>Returns the action's compile-time suggested default, or null if none was declared.</summary>
    KeyBinding? GetSuggestedDefault(string actionId);

    /// <summary>Fired with <c>actionId</c> after <see cref="Rebind"/> mutates a binding.</summary>
    event Action<string> BindingChanged;

    /// <summary>
    /// True while a rebinding cell is actively waiting for a key. The
    /// <c>HotkeyService.Tick</c> dispatcher skips action lookup while this is
    /// set so the press that gets captured doesn't ALSO fire its bound action.
    /// Driven by <see cref="BeginCapture"/> / <see cref="EndCapture"/>.
    /// </summary>
    bool IsCapturing { get; }

    /// <summary>Open a capture window. Idempotent if the same actionId is already capturing.</summary>
    void BeginCapture(string actionId);

    /// <summary>Close any open capture window. Safe to call when not capturing.</summary>
    void EndCapture();
}
