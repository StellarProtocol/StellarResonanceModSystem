using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Application's gateway to Unity input + screen state. Polled once per
/// IFramework.Update tick by the HotkeyService and LayoutEditorService.
/// </summary>
internal interface IInputGateway
{
    /// <summary>Keys that transitioned to "down" this frame.</summary>
    IReadOnlyList<StellarKeyCode> PressedKeysThisFrame { get; }

    /// <summary>Modifier flags currently held.</summary>
    ModifierKeys CurrentModifiers { get; }

    /// <summary>Current screen dimensions (Screen.width × Screen.height).</summary>
    Resolution CurrentResolution { get; }

    /// <summary>Pointer position in screen pixels (origin top-left).</summary>
    (float X, float Y) PointerPosition { get; }

    /// <summary>Is the left mouse button currently held?</summary>
    bool LeftMouseDown { get; }

    /// <summary>Did the left mouse button transition to down since the previous <see cref="TickPoll"/>?
    /// Latched in TickPoll off the button LEVEL, so it survives the framework's throttled tick rate where a
    /// single-frame <c>GetMouseButtonDown</c> edge would be missed between ticks. Use this for grab/press
    /// detection driven from the framework tick (e.g. layout edit-mode).</summary>
    bool LeftMousePressedSinceTick { get; }

    /// <summary>
    /// Monotonically-increasing frame counter. Used to dedupe work that should
    /// run at most once per Unity frame — OnGUI fires multiple times per frame
    /// (one per Event: Layout, Repaint, KeyDown, ...) and Input.GetKeyDown
    /// returns true for the whole frame, so without a frame-counter check
    /// hotkey callbacks fire once per OnGUI event instead of once per press.
    /// </summary>
    int CurrentFrame { get; }
}
