namespace Stellar.Application.Abstractions;

/// <summary>Game side of the sticky cursor (<see cref="Services.StickyCursorService"/>), implemented in Infrastructure.
/// Every member is main-thread only and is called on a state CHANGE, never per frame.</summary>
internal interface IStickyCursorBackend
{
    /// <summary>True once the game's Lua state can take <see cref="RequestGameCursor"/>.</summary>
    bool LuaReady { get; }

    /// <summary>Adds (on) / removes (off) the framework's key in the game's show-mouse set
    /// (<c>Z.UIMgr:AddShowMouseView</c> / <c>RemoveShowMouseView</c>): the cursor stays free and the camera stops
    /// following the mouse, exactly as for an open game panel. Removing an absent key is a no-op in the game.</summary>
    void RequestGameCursor(bool on);

    /// <summary>Hides (on) / passes (off) the Escape key from the game's keyboard input.</summary>
    void SetEscapeBlocked(bool on);

    /// <summary>Hides (on) / passes (off) the mouse buttons, wheel and axes from the game's input.</summary>
    void SetWorldMouseBlocked(bool on);
}
