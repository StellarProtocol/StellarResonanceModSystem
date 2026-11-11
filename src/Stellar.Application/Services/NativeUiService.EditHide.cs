namespace Stellar.Application.Services;

/// <summary>
/// Layout-editor "Hide others" for the game UI: while a single plugin's windows are being arranged, every allowlist
/// element is TRANSIENTLY hidden (wrapper scale 0). Never persisted and never written into <c>Visible</c> /
/// <c>IsModified</c> / the offset, so clearing it leaves each element exactly as before — a user-hidden one stays
/// hidden, an untouched one is unwrapped again (its temporary offset-0 wrapper removed). It survives rebuilds the same
/// way a customization does: a re-resolved element is re-wrapped hidden at once, and the liveness check re-wraps a lost
/// wrapper. Hidden-by-filter elements are not editable (no outline, placeholder, selection or hit-test).
/// </summary>
internal sealed partial class NativeUiService
{
    private bool _transientHidden;

    /// <summary>True while the editor's "Hide others" hides the game UI.</summary>
    public bool TransientHidden => _transientHidden;

    /// <summary>
    /// Set / clear the transient hide (idempotent; called every edit-mode tick with the desired state, so a filter
    /// change, chip toggle, slot switch or scene rebuild is re-evaluated at once). Returns how many elements it hides
    /// that the user would otherwise see (resolved and not user-hidden) — the toolbar's "N hidden while editing".
    /// </summary>
    public int SetTransientHidden(bool hidden)
    {
        if (_transientHidden != hidden)
        {
            _transientHidden = hidden;
            foreach (var e in _order) ApplyToGame(e);
        }
        if (!hidden) return 0;
        var n = 0;
        foreach (var e in _order) if (e.IsResolved && e.Visible) n++;
        return n;
    }
}
