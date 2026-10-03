using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

// Binding resolution and the one-time saved-binding migration (framework 2.14.0, spec 2026-10-01 free camera D7).
// A SAVED binding (the player's own choice) beats a suggested default whatever order plugins declare in; two saved
// claims on one chord resolve alphabetically, like two defaults. Losers are unbound at run time only (never persisted).
// Invariant: at run time at most one action holds any chord.
internal sealed partial class HotkeyService
{
    public SavedBindingMigration MigrateSavedBinding(string actionId, KeyBinding from, KeyBinding to)
    {
        var stored = LoadStoredBinding(actionId);
        if (stored is null) return SavedBindingMigration.NothingSaved;
        if (stored.Value.IsUnbound || stored.Value.Binding != from) return SavedBindingMigration.KeptOther;
        KeyBinding? target = Holder(to, except: actionId) is null ? to : null;
        PersistBinding(actionId, target);
        if (_actions.TryGetValue(actionId, out var declared))
        {
            declared.CurrentBinding = target;
            declared.Saved = target is not null;
            BindingChanged?.Invoke(actionId);
            SyncBlockedKeys();
        }
        _log.Info($"[Hotkeys] '{actionId}': saved {from} moved to {(target?.ToString() ?? "unbound")} (one-time migration)");
        return target is null ? SavedBindingMigration.Cleared : SavedBindingMigration.Moved;
    }

    private (KeyBinding? Binding, bool Saved) ResolveBinding(HotkeyAction action)
    {
        // Persisted user choice trumps SuggestedDefault; "_unbound_" means the user explicitly cleared the binding.
        var stored = LoadStoredBinding(action.Id);
        if (stored is { IsUnbound: true }) return (null, false);
        if (stored is { Binding: { } savedBinding }) return ClaimSaved(action.Id, savedBinding);
        return action.SuggestedDefault is { } suggested ? (ClaimDefault(action.Id, suggested), false) : (null, false);
    }

    // A saved chord takes it from a default holder; against another saved holder the alphabetically-first id keeps it.
    private (KeyBinding? Binding, bool Saved) ClaimSaved(string id, KeyBinding binding)
    {
        if (Holder(binding, except: id) is { } other)
        {
            if (other.Saved && string.CompareOrdinal(other.Id, id) < 0)
            {
                LogCollision(id, binding, other.Id);
                return (null, false);
            }
            other.CurrentBinding = null;
            other.Saved = false;
            LogCollision(other.Id, binding, id);
        }
        return (binding, true);
    }

    // A default never takes a saved chord; against another default the alphabetically-first id wins (pre-2.14 rule).
    private KeyBinding? ClaimDefault(string id, KeyBinding binding)
    {
        if (Holder(binding, except: id) is not { } other) return binding;
        if (other.Saved || string.CompareOrdinal(other.Id, id) < 0)
        {
            LogCollision(id, binding, other.Id);
            return null;
        }
        other.CurrentBinding = null;
        LogCollision(other.Id, binding, id);
        return binding;
    }

    private RegisteredAction? Holder(KeyBinding binding, string except)
    {
        foreach (var a in _actions.Values)
            if (a.Id != except && a.CurrentBinding == binding) return a;
        return null;
    }
}
