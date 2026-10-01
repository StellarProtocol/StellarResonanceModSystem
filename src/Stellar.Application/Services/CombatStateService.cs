using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// Local-player combat state, event-driven (spec § 7): wire attr 104 <c>AttrCombatState</c> (&gt; 0 = in combat, the game
/// HUD's test) OR the game's local <c>SetLocalCombatData</c> / <c>SetLocalAttrInBattleShow</c> flags. Attr events may arrive
/// off the main thread and are posted. <see cref="Reseed"/> runs once per scene change. Main thread otherwise.
/// </summary>
internal sealed class CombatStateService : ICombatState
{
    internal const int AttrCombatState = 104;

    private readonly ICombatSnapshot _snapshot;
    private readonly ICombatFlagSource _flags;
    private readonly Action<Action> _post;
    private Action<bool>? _changed;
    private bool _wire, _local, _show;

    public CombatStateService(ICombatEvents events, ICombatSnapshot snapshot, ICombatFlagSource flags, Action<Action> post)
    {
        _snapshot = snapshot;
        _flags = flags;
        _post = post;
        events.CombatEventOccurred += OnCombatEvent;
        flags.Changed += OnFlag;
    }

    public bool LocalPlayerInCombat { get; private set; }

    public event Action<bool>? Changed
    {
        add { _flags.EnsureHooks(); _changed += value; }
        remove => _changed -= value;
    }

    internal void Reseed()
    {
        _wire = _flags.ReadLocalInCombat() ?? false;
        _local = false;
        _show = false;
        Recompute();
    }

    private void OnCombatEvent(CombatEvent ev)
    {
        if (ev is not CombatEvent.EntityAttributesChanged a) return;
        var self = _snapshot.LocalEntityId;
        if (self.IsNone || a.TargetId != self) return;
        foreach (var v in a.Attrs)
        {
            if (v.AttrId != AttrCombatState) continue;
            var on = v.Value > 0;
            _post(() => { _wire = on; Recompute(); });
        }
    }

    private void OnFlag(CombatFlagKind kind, bool on)
    {
        _post(() =>
        {
            if (kind == CombatFlagKind.LocalCombatData) _local = on;
            else _show = on;
            Recompute();
        });
    }

    private void Recompute()
    {
        var now = _wire || _local || _show;
        if (now == LocalPlayerInCombat) return;
        LocalPlayerInCombat = now;
        _changed?.Invoke(now);
    }
}
