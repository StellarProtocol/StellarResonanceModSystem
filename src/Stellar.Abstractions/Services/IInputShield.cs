using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// While any handle is held (reference-counted across plugins): blocks the game's movement, camera, combat and
/// interaction actions (mouse included), and blocks <b>every keyboard key</b> from the game — the same gate a focused
/// Stellar text field uses, so Esc, Enter, M, Tab and the rest open nothing in the game. The holder reads the keyboard
/// and mouse itself through the handle; framework hotkeys keep working. Main thread only. Handles a plugin still holds
/// are released when it unloads.
/// </summary>
public interface IInputShield
{
    /// <summary>Raises the shield until the returned handle is disposed.</summary>
    IInputShieldHandle Shield();

    /// <summary>True while the game's input mask is up (the keyboard gate follows any held handle).</summary>
    bool IsShielded { get; }

    /// <summary>
    /// True when the pointer is over the game's own interface (a game window, button or slider) and not over a Stellar
    /// window. A free camera that lets the player use the game's interface leaves clicks, drags and the wheel to the game
    /// while this is true. Game interface hidden through <see cref="ISceneVisibility"/> may still answer, so consult it
    /// only while the game interface is shown. Works with or without a held handle. Main thread; evaluated at most once
    /// per rendered frame.
    /// </summary>
    bool IsPointerOverGameUi { get; }
}

/// <summary>A held input shield plus raw input reads. Reads return defaults once disposed.</summary>
public interface IInputShieldHandle : IDisposable
{
    /// <summary>True until disposed.</summary>
    bool IsActive { get; }

    /// <summary>Whether <paramref name="key"/> is held down this frame.</summary>
    /// <param name="key">The key.</param>
    bool IsHeld(StellarKeyCode key);

    /// <summary>Shift / Ctrl / Alt held this frame.</summary>
    ModifierKeys Modifiers { get; }

    /// <summary>Whether a mouse button is held (0 = left, 1 = right, 2 = middle).</summary>
    /// <param name="button">Mouse button index.</param>
    bool IsMouseHeld(int button);

    /// <summary>Pointer movement this frame in screen pixels, origin top-left (X right, Y down).</summary>
    (float X, float Y) MouseDelta { get; }

    /// <summary>Mouse wheel notches this frame; positive = scrolled up (away from the player).</summary>
    float Wheel { get; }

    /// <summary>Pointer position in screen pixels, origin top-left.</summary>
    (float X, float Y) Pointer { get; }

    /// <summary>
    /// True while a Stellar overlay text field has keyboard focus; free-camera consumers should ignore movement/edge
    /// keys then. False once disposed.
    /// </summary>
    bool TextFieldFocused { get; }
}
