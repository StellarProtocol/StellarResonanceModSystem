using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// UI text that names a framework hotkey ("Alt+E toggles layout editing anywhere.") built from the action's LIVE
/// binding, so a rebind in Settings → Hotkeys shows up at once. The catalog strings used to hard-code the default
/// keys — and after a rebind the hint lied (layout editing even said Shift+` while its real default is Alt+E).
/// <para>The key is formatted exactly like the Hotkeys tab's key pill (<see cref="KeyBinding.ToString"/>, the same call
/// <c>HotkeysPanel.GetOrBuildBindingLabel</c> makes). An unbound action switches to a separate catalog line that
/// doesn't name a key at all.</para>
/// <para>Render-driven only (hub Funcs), never from a per-tick path: the language must not be read during wiring. The
/// result is cached per template key — the hub footer pulls its hint on every apply — and the cache drops on a
/// binding change, a language switch, or a new declaration (a later plugin's default can silently take a framework
/// default's chord inside <c>Declare</c>, which raises no <see cref="IHotkeyDirectory.BindingChanged"/>; the
/// directory's action-list snapshot is replaced on every declare, so a reference check catches it).</para>
/// </summary>
internal sealed class HotkeyHintText
{
    private readonly IHotkeyDirectory _directory;
    private readonly ILocalization _loc;
    private readonly Dictionary<string, string> _cache = new();
    private IReadOnlyList<IHotkeyAction>? _seenActions;

    public HotkeyHintText(IHotkeyDirectory directory, ILocalization loc)
    {
        _directory = directory;
        _loc = loc;
        _directory.BindingChanged += _ => _cache.Clear();
        _loc.LanguageChanged += _cache.Clear;
    }

    /// <summary>The action's live binding as the Hotkeys pill shows it ("Shift+Home"), or <paramref name="unboundKey"/>
    /// resolved when the action has no key.</summary>
    public string BindingLabel(string actionId, string unboundKey) => Resolve(actionId, null, unboundKey);

    /// <summary><paramref name="boundKey"/> formatted with the live binding of <paramref name="actionId"/> as
    /// <c>{0}</c>, or <paramref name="unboundKey"/> when the action has no key.</summary>
    public string Text(string actionId, string boundKey, string unboundKey) => Resolve(actionId, boundKey, unboundKey);

    // boundKey null = the bare key label. Cache key: the template, or the action id for a bare label (the two never
    // collide: action ids are "framework.x-y", catalog keys "tab.x.y").
    private string Resolve(string actionId, string? boundKey, string unboundKey)
    {
        var actions = _directory.Actions;
        if (!ReferenceEquals(actions, _seenActions)) { _cache.Clear(); _seenActions = actions; }
        var cacheKey = boundKey ?? actionId;
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
        var binding = Find(actionId, out var declared);
        var text = binding is not { } b ? _loc.T(unboundKey)
                 : boundKey is null ? b.ToString()
                 : _loc.TFormat(boundKey, b.ToString());
        // Not cached until the action exists: the Settings toggle is declared after the hub is built, and nothing
        // would invalidate a "no key" read taken before that.
        if (declared) _cache[cacheKey] = text;
        return text;
    }

    private KeyBinding? Find(string actionId, out bool declared)
    {
        var actions = _directory.Actions;
        for (var i = 0; i < actions.Count; i++)
        {
            if (actions[i].Id != actionId) continue;
            declared = true;
            return actions[i].CurrentBinding;
        }
        declared = false;
        return null;
    }
}
