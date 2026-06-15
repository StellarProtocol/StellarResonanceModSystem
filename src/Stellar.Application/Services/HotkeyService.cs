using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

internal sealed class HotkeyService : IHotkeys, IHotkeyDirectory
{
    private const string UnboundSentinel = "_unbound_";
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(30);

    private readonly IInputGateway _input;
    private readonly IPluginLog _log;
    private readonly IConfigSection? _config;
    private readonly Dictionary<string, RegisteredAction> _actions = new();
    private readonly Dictionary<string, KeyBinding?> _suggestedDefaults = new();
    private readonly HashSet<string> _collisionsLogged = new();
    private readonly Dictionary<string, DateTime> _lastErrorLog = new();
    // Reused scratch buffer for matched actions; cleared at the top of every
    // Tick. Previously allocated a fresh List per Tick — observed as the
    // dominant per-frame allocation under the Settings hub on a quiet idle.
    private readonly List<RegisteredAction> _matchedScratch = new();
    // Set by BeginCapture / EndCapture (called from HotkeysPanel). While true,
    // Tick early-returns BEFORE dispatching matches so the key that the
    // capture cell consumes doesn't also fire its currently-bound action.
    private string? _capturingActionId;

    public HotkeyService(IInputGateway input, IPluginLog log, IConfigSection? config = null)
    {
        _input = input;
        _log = log;
        _config = config;
    }

    public event Action<string>? BindingChanged;

    IReadOnlyList<IHotkeyAction> IHotkeyDirectory.Actions => CachedActionsList;
    bool IHotkeyDirectory.IsCapturing => _capturingActionId is not null;
    void IHotkeyDirectory.BeginCapture(string actionId) => _capturingActionId = actionId;
    void IHotkeyDirectory.EndCapture() => _capturingActionId = null;

    // Cached read-only view of _actions.Values; rebuilt only when DeclareAction
    // mutates the dictionary. HotkeysPanel.OrderedActions() reads this every
    // OnGUI pass; previously the getter allocated a fresh List per call.
    private IReadOnlyList<IHotkeyAction>? _cachedActionsList;
    private IReadOnlyList<IHotkeyAction> CachedActionsList
    {
        get
        {
            if (_cachedActionsList is not null) return _cachedActionsList;
            var list = new List<IHotkeyAction>(_actions.Count);
            foreach (var a in _actions.Values) list.Add(a);
            _cachedActionsList = list;
            return list;
        }
    }

    KeyBinding? IHotkeyDirectory.GetSuggestedDefault(string actionId)
        => _suggestedDefaults.TryGetValue(actionId, out var v) ? v : null;

    public void Rebind(string actionId, KeyBinding? newBinding)
    {
        if (!_actions.TryGetValue(actionId, out var action))
        {
            _log.Warning($"[Hotkeys] Rebind: unknown action '{actionId}'");
            return;
        }
        action.CurrentBinding = newBinding;
        PersistBinding(actionId, newBinding);
        BindingChanged?.Invoke(actionId);
    }

    public IHotkeyAction DeclareAction(HotkeyAction action, Action callback)
    {
        if (_actions.TryGetValue(action.Id, out var existing))
        {
            _log.Warning($"[Hotkeys] action '{action.Id}' declared twice; replacing callback.");
            existing.Callback = callback;
            return existing;
        }

        _suggestedDefaults[action.Id] = action.SuggestedDefault;
        var resolved = ResolveBinding(action);
        var registered = new RegisteredAction(action.Id, resolved, callback, _actions, InvalidateActionsCache);
        _actions[action.Id] = registered;
        _cachedActionsList = null;   // invalidate the snapshot served to IHotkeyDirectory consumers
        return registered;
    }

    private void InvalidateActionsCache() => _cachedActionsList = null;

    /// <summary>
    /// Lockout safety net — if framework.settings-toggle has no binding AND
    /// no other framework.* action has a binding, restore the suggested
    /// default so the user can always reopen the Settings hub. Called once
    /// after all framework hotkeys are declared (from BootstrapPlugin.Phase9).
    /// </summary>
    internal void RestoreSettingsHotkeyIfLocked()
    {
        if (!_actions.TryGetValue("framework.settings-toggle", out var settings)) return;
        if (settings.CurrentBinding is not null) return;
        foreach (var a in _actions.Values)
            if (a.Id.StartsWith("framework.", StringComparison.Ordinal) && a.CurrentBinding is not null)
                return;   // some other framework hotkey is bound; user can still reach Settings via Hotkeys panel
        if (_suggestedDefaults.TryGetValue("framework.settings-toggle", out var fallback) && fallback is not null)
        {
            Rebind("framework.settings-toggle", fallback);
            _log.Warning("[Hotkeys] settings hotkey unbound — restoring suggested default to keep Settings reachable.");
        }
    }

    /// <summary>Called once per IFramework.Update tick by the host.</summary>
    public void Tick()
    {
        // While a rebinding cell is open, every key press belongs to the
        // capture window — NOT to action dispatch. Without this gate, pressing
        // Shift+` to rebind the layout-edit hotkey would both rebind AND
        // toggle edit mode in the same frame.
        if (_capturingActionId is not null) return;

        var pressed = _input.PressedKeysThisFrame;
        if (pressed.Count == 0) return;

        var modifiers = _input.CurrentModifiers;
        _matchedScratch.Clear();

        foreach (var action in _actions.Values)
        {
            if (action.CurrentBinding is not { } binding) continue;
            if (binding.Modifiers != modifiers) continue;
            if (!pressed.Contains(binding.Key)) continue;
            _matchedScratch.Add(action);
        }

        foreach (var action in _matchedScratch)
        {
            InvokeCallback(action);
        }
    }

    private KeyBinding? ResolveBinding(HotkeyAction action)
    {
        // Persisted user choice trumps SuggestedDefault. "_unbound_" sentinel
        // means the user explicitly cleared the binding.
        var stored = LoadStoredBinding(action.Id);
        if (stored.HasValue && stored.Value.IsUnbound) return null;
        if (stored.HasValue) return stored.Value.Binding;

        if (action.SuggestedDefault is not { } suggested) return null;

        // Collision check: walk existing actions; if any already has this exact
        // binding, the alphabetically-first Id wins.
        var conflicting = _actions.Values
            .Where(a => a.CurrentBinding == suggested)
            .Select(a => a.Id)
            .ToList();

        if (conflicting.Count == 0) return suggested;

        // Sort all ids (existing + the new one) alphabetically; the first wins.
        var all = new List<string>(conflicting) { action.Id };
        all.Sort(StringComparer.Ordinal);
        var winnerId = all[0];

        if (winnerId == action.Id)
        {
            // New action wins; un-bind the others.
            foreach (var lostId in conflicting)
            {
                _actions[lostId].CurrentBinding = null;
                LogCollision(lostId, suggested, action.Id);
            }
            return suggested;
        }

        // Existing action keeps the binding; new action is unbound.
        LogCollision(action.Id, suggested, winnerId);
        return null;
    }

    private void PersistBinding(string actionId, KeyBinding? binding)
    {
        if (_config is null) return;
        if (binding is null)
        {
            _config.Set($"bindings.{actionId}.key", UnboundSentinel);
            _config.Set($"bindings.{actionId}.mods", "None");
        }
        else
        {
            _config.Set($"bindings.{actionId}.key", binding.Value.Key.ToString());
            _config.Set($"bindings.{actionId}.mods", binding.Value.Modifiers.ToString());
        }
        _config.Save();
    }

    private readonly record struct StoredBinding(KeyBinding? Binding, bool IsUnbound);

    private StoredBinding? LoadStoredBinding(string actionId)
    {
        if (_config is null) return null;
        var keyStr = _config.Get($"bindings.{actionId}.key", string.Empty) ?? string.Empty;
        if (keyStr.Length == 0) return null;   // not persisted
        if (keyStr == UnboundSentinel) return new StoredBinding(null, IsUnbound: true);
        var modsStr = _config.Get($"bindings.{actionId}.mods", "None") ?? "None";
        if (!Enum.TryParse<StellarKeyCode>(keyStr, ignoreCase: false, out var key)) return null;
        if (!Enum.TryParse<ModifierKeys>(modsStr, ignoreCase: false, out var mods)) mods = ModifierKeys.None;
        return new StoredBinding(new KeyBinding(key, mods), IsUnbound: false);
    }

    private void LogCollision(string loserId, KeyBinding binding, string winnerId)
    {
        var key = $"{loserId}->{winnerId}:{binding}";
        if (_collisionsLogged.Add(key))
        {
            _log.Warning(
                $"[Hotkeys] action '{loserId}' suggested {binding} — already claimed by " +
                $"'{winnerId}'. Falling back to unassigned; user can bind via Phase 9 Settings.");
        }
    }

    private void InvokeCallback(RegisteredAction action)
    {
        try
        {
            action.Callback();
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            if (_lastErrorLog.TryGetValue(action.Id, out var last) && now - last < ErrorLogInterval) return;
            _lastErrorLog[action.Id] = now;
            _log.Error($"[Hotkeys] action '{action.Id}' callback threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class RegisteredAction : IHotkeyAction
    {
        private readonly Dictionary<string, RegisteredAction> _registry;
        private readonly Action _invalidateCache;
        private bool _disposed;

        public RegisteredAction(string id, KeyBinding? binding, Action callback,
                                Dictionary<string, RegisteredAction> registry,
                                Action invalidateCache)
        {
            Id = id;
            CurrentBinding = binding;
            Callback = callback;
            _registry = registry;
            _invalidateCache = invalidateCache;
        }

        public string      Id              { get; }
        public KeyBinding? CurrentBinding  { get; internal set; }
        public Action      Callback        { get; internal set; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _registry.Remove(Id);
            _invalidateCache();
        }
    }
}
