namespace Stellar.Abstractions.Domain;

/// <summary>What <c>IHotkeys.MigrateSavedBinding</c> found and did.</summary>
public enum SavedBindingMigration
{
    /// <summary>The player never saved a binding for the action; its suggested default applies. Nothing was written.</summary>
    NothingSaved,
    /// <summary>A different saved binding (or a saved "unbound") was left as the player chose it.</summary>
    KeptOther,
    /// <summary>The saved binding was the old chord and now holds the new one.</summary>
    Moved,
    /// <summary>The saved binding was the old chord, but another action holds the new one, so the action is now saved as
    /// unbound (never a duplicate). The player can bind it in Settings → Hotkeys.</summary>
    Cleared,
}
