using System;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>What the pointer is over this frame, as the sticky cursor sees it.</summary>
internal enum StickyPointer
{
    /// <summary>The 3D world: no Stellar window and no game interface under the pointer.</summary>
    World,
    /// <summary>The game's own interface (bag, map, NPC dialog, the game's chat…).</summary>
    GameUi,
    /// <summary>The front-most Stellar window here is a HUD-category overlay (meters, cooldown bars).</summary>
    StellarHud,
    /// <summary>The front-most Stellar window here is an interactive (non-HUD) window: Settings, chat, plugin tools.</summary>
    StellarInteractive,
}

/// <summary>One rendered frame of input, sampled by the window ticker. A plain struct (not a positional record) so the
/// field list can grow without a wide constructor.</summary>
internal struct StickyCursorFrame
{
    public bool Enabled;                // the Settings → Layout "Keep cursor after clicking a Stellar window" toggle
    public bool WorldActive;            // IClientState.IsWorldActive (false at login / char select / loading)
    public bool CursorFree;             // the cursor is usable right now (Alt held, a game panel open, …)
    public bool MouseDown;              // left or right button went down this frame
    public bool AnyMouseHeld;           // left, right or middle is held
    public bool EscDown;                // Escape went down this frame
    public bool EscHeld;                // Escape is held
    public bool FieldFocused;           // a Stellar text field had focus at the START of this frame (before its Esc ran)
    public bool AnyInteractiveVisible;  // at least one non-HUD Stellar window is showing
    public StickyPointer Pointer;       // only meaningful when WantsPointer said so
}

/// <summary>
/// Opt-in "sticky cursor": an Alt+click on an interactive Stellar window keeps the cursor free after Alt is released, so
/// the windows can be clicked, dragged, scrolled and hovered with no key held. Uses the game's own free-cursor request
/// (<c>Z.UIMgr:AddShowMouseView</c>) rather than <c>Cursor.visible</c>, because only that also stops the camera turning.
/// A press on the 3D world or Escape hands the cursor back to the camera; both are hidden from the game so the release
/// press never attacks, targets or opens the menu. Pure state machine (unit-tested); the game calls go through
/// <see cref="IStickyCursorBackend"/>. Main thread, ticked once per rendered frame by the window ticker.
/// </summary>
internal sealed class StickyCursorService
{
    private readonly IStickyCursorBackend _backend;
    private readonly Action<string>? _log;
    private bool _sticky;
    private bool _requested;      // our key is (or may still be) in the game's show-mouse set
    private bool _escBlocked;
    private bool _mouseBlocked;
    private bool _lastWorldActive;

    public StickyCursorService(IStickyCursorBackend backend, Action<string>? log = null)
    {
        _backend = backend;
        _log = log;
    }

    /// <summary>True while the cursor is being kept free.</summary>
    public bool IsSticky => _sticky;

    /// <summary>Whether the ticker must classify the pointer this frame. Idle (not sticky) only needs it on a press, so an
    /// idle frame costs nothing; while sticky it is needed every frame to decide whether the world mouse is blocked.</summary>
    public bool WantsPointer(bool enabled, bool mouseDown) => _sticky || (enabled && mouseDown);

    public void Tick(StickyCursorFrame f)
    {
        _lastWorldActive = f.WorldActive;
        if (_sticky) TickSticky(f);
        else TryEngage(f);
        if (!_sticky && _requested && f.WorldActive && _backend.LuaReady) RemoveRequest();   // deferred release
        // Escape: blocked for the whole sticky period, then kept blocked until the key is let go, so the press that
        // released sticky never reaches the game as a down OR an up. Same rule for the mouse: once blocked it stays
        // blocked until every button is up. Re-enabling input under a held button would show the game a fresh press.
        SetEscBlocked(_sticky || (_escBlocked && f.EscHeld));
        SetMouseBlocked((_sticky && f.Pointer == StickyPointer.World) || (_mouseBlocked && f.AnyMouseHeld));
    }

    private void TryEngage(StickyCursorFrame f)
    {
        if (!f.Enabled || !f.WorldActive || !f.CursorFree || !f.MouseDown) return;
        if (f.Pointer != StickyPointer.StellarInteractive || !_backend.LuaReady) return;
        _backend.RequestGameCursor(true);
        _requested = true;
        _sticky = true;
        _log?.Invoke("[StickyCursor] engaged (click on an interactive Stellar window)");
    }

    private void TickSticky(StickyCursorFrame f)
    {
        if (!f.Enabled) Release("setting turned off", f.WorldActive);
        else if (!f.WorldActive) Release("world inactive", false);
        else if (!f.AnyInteractiveVisible) Release("no interactive Stellar window visible", true);
        // The release press itself: the world mouse was already blocked last frame (pointer over the world), and
        // Escape for the whole sticky period, so neither reaches the game. The Tick tail keeps them blocked while held.
        else if (f.MouseDown && f.Pointer == StickyPointer.World) Release("world click", true);
        // A focused field owns this Escape (it defocuses); sticky survives that press.
        else if (f.EscDown && !f.FieldFocused) Release("escape", true);
    }

    private void Release(string reason, bool canCallGame)
    {
        _sticky = false;
        // Outside the world the game call waits: Tick removes the key once the world is back (never strands it).
        if (canCallGame && _requested && _backend.LuaReady) RemoveRequest();
        _log?.Invoke($"[StickyCursor] released ({reason}){(_requested ? " — game-side removal deferred until the world is active" : "")}");
    }

    private void RemoveRequest()
    {
        _backend.RequestGameCursor(false);
        _requested = false;
    }

    /// <summary>The ticker that drives this is going away (canvas rebuilt on a scene change, or teardown): drop sticky
    /// and every block now, since nothing may tick us to release them. A pending game-side removal runs immediately when
    /// the world is still active, otherwise on the next tick of a new ticker.</summary>
    public void OnHostLost()
    {
        if (_sticky) Release("window host destroyed", _lastWorldActive);
        else if (_requested && _lastWorldActive && _backend.LuaReady) RemoveRequest();
        SetEscBlocked(false);
        SetMouseBlocked(false);
    }

    private void SetEscBlocked(bool on)
    {
        if (on == _escBlocked) return;
        _escBlocked = on;
        _backend.SetEscapeBlocked(on);
    }

    private void SetMouseBlocked(bool on)
    {
        if (on == _mouseBlocked) return;
        _mouseBlocked = on;
        _backend.SetWorldMouseBlocked(on);
    }
}
