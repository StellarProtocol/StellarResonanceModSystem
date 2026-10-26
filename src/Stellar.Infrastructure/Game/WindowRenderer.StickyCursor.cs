using System;
using Stellar.Infrastructure.Unity;

namespace Stellar.Infrastructure.Game;

// Sticky cursor plumbing: the renderer owns the hooks (they outlive any one ticker, since the canvas is rebuilt on a scene
// change) and hands them to every ticker it creates (EnsureCanvas).
internal sealed partial class WindowRenderer
{
    private WindowInteractionTicker.StickyHooks? _stickyCursor;

    /// <summary>Gate disposer run on shutdown, after the service released (re-enables the game's mouse for good).</summary>
    internal Action? StickyCursorDispose { get; set; }

    /// <summary>Set once by the Host after construction; also handed to an already-live ticker.</summary>
    internal WindowInteractionTicker.StickyHooks? StickyCursor
    {
        get => _stickyCursor;
        set
        {
            _stickyCursor = value;
            if (_ticker != null) _ticker.Sticky = value;
        }
    }

    private void ShutdownStickyCursor()
    {
        try { _stickyCursor?.Service.OnHostLost(); }
        catch (Exception ex) { _log.Warning($"[Window] sticky-cursor release threw: {ex.Message}"); }
        try { StickyCursorDispose?.Invoke(); }
        catch (Exception ex) { _log.Warning($"[Window] sticky-cursor gate dispose threw: {ex.Message}"); }
        if (_ticker != null) _ticker.Sticky = null;   // the dying ticker's OnDestroy has nothing left to release
    }
}
