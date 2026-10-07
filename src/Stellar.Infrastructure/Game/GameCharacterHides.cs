using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game;

// Game reflection: GameCharacterHides.Game.cs; game hooks: GameCharacterHides.Hooks.cs; StellarDiagnostics lines:
// GameCharacterHides.Diagnostics.cs.

/// <summary>
/// Per-character hides the game's photo screen cannot express (framework 2.20.0, owner go 2026-10-07): Friends / Party /
/// Guild as REAL hides (their members vanish even while Other adventurers is shown) and Weapon for every player, not
/// only the local one. Rules: <see cref="CharacterHideRules"/>; bookkeeping: <see cref="CharacterHideLedger"/>.
/// <para><b>Mechanism (static RE, release_3.7, docs/recon/photo-hide-recon.md § "Per-character hides").</b> Body =
/// <c>ZEntityHelper.SetModelRenderVisible(entity, visible, (EVisibleSource)28, withSummon:false, withEffect:true)</c>:
/// a private bit of the entity's per-source visibility mask that no game path writes, so it composes with the game's
/// own photo counters and is never cleared under us. Weapon = <c>ZModelHelper.SetVisilbe(entity.Model, 0x6000, visible,
/// EModelAlphaSourceType.EPhoto)</c> — exactly what <c>WeaponComp.ChangeWeaponVisible</c> does for the local player;
/// every game writer of EPhoto targets <c>PlayerEnt</c> only, so on another player it is ours alone.</para>
/// <para><b>Event-driven, never polled.</b> One pass over the game's character list when the request changes or is
/// re-asserted, or when a membership changes (party roster, friend / guild caches); a character entering view is
/// handled by a postfix on <c>ZEntityMgr.checkIsRenderVisible</c>; a weapon model the game (re)built — skin, class or
/// shape change, re-entering view — by a postfix on <c>WeaponModelComp.onWeaponModelLoaded</c>, re-applied on the next
/// tick. The hooks are installed the first time a layer needs them. Fails open: a missing member leaves everyone
/// visible with one warning. Main thread only.</para>
/// </summary>
internal sealed partial class GameCharacterHides
{
    private const string Tag = "[PhotoStudio] ";

    /// <summary>The request bits that need the entity hooks: the per-character layers, plus the local player's Me
    /// (a re-created local player needs its Me / Weapon flags re-asserted — <see cref="LocalPlayerRebuilt"/>).</summary>
    internal const VisibilityLayers HookedLayers =
        CharacterHideRules.Driven | VisibilityLayers.SelfCharacter | VisibilityLayers.Self;

    private readonly GameEntityAccess _entities;
    private readonly Func<IReadOnlyList<PartyMember>> _party;
    private readonly IPluginLog _log;
    private readonly CharacterHideLedger _bodies = new();
    private readonly CharacterHideLedger _weapons = new();
    private readonly List<long> _charIds = new();
    private readonly HashSet<long> _present = new();
    private readonly HashSet<long> _weaponReapply = new();
    private readonly List<long> _drain = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly int _mainThread = Environment.CurrentManagedThreadId;   // constructed in Load() on the main thread
    private VisibilityLayers _requested;
    private bool _membershipDirty;

    public GameCharacterHides(IGameTypeRegistry types, GameEntityAccess entities, Func<IReadOnlyList<PartyMember>> party,
        IPluginLog log)
    {
        _types = types;
        _entities = entities;
        _party = party;
        _log = log;
    }

    /// <summary>Raised (main thread) when the game re-created the local player or rebuilt its weapon while Me or Weapon
    /// is requested: both are per-source flags on that entity, so the host re-asserts them on the next tick.</summary>
    public event Action? LocalPlayerRebuilt;

    /// <summary>True while this class still holds a hidden body or weapon — after a release, only a show that failed
    /// (or a character not yet pruned); the backend's pending-restore signal.</summary>
    public bool HoldsAny => _bodies.Count > 0 || _weapons.Count > 0;

    /// <summary>True while a membership change or a rebuilt weapon waits for <see cref="Drain"/>.</summary>
    public bool HasPending => _membershipDirty || _weaponReapply.Count > 0;

    /// <summary>Moves every character to <paramref name="requested"/>: hides who the rules hide, shows back only who this
    /// class hid. Idempotent — <c>Reassert</c> calls it again after the game's photo screen closed or a target was rebuilt.</summary>
    public void Apply(VisibilityLayers requested)
    {
        _requested = requested & HookedLayers;
        if (_requested != VisibilityLayers.None) EnsureHooks();
        Pass("apply");
    }

    /// <summary>A party / friend / guild membership changed: re-evaluate everyone on the next <see cref="Drain"/>.</summary>
    public void MarkMembershipChanged()
    {
        if ((_requested & CharacterHideRules.Groups) != 0) _membershipDirty = true;
    }

    /// <summary>Runs the deferred work (one tick after the game signal): weapon re-hides, then a membership pass.
    /// A no-op costing two field reads when nothing is pending — called every framework tick.</summary>
    public void Drain()
    {
        if (!HasPending) return;
        if (_weaponReapply.Count > 0) ReapplyWeapons();
        if (!_membershipDirty) return;
        _membershipDirty = false;
        Pass("membership");
    }

    private void Pass(string why)
    {
        if (!CharacterHideRules.Needed(_requested) && _bodies.Count == 0 && _weapons.Count == 0) return;
        if (!Resolve()) return;
        var local = _entities.PlayerUuid();
        _entities.CharIds(_charIds);
        _present.Clear();
        var before = (_bodies.Count, _weapons.Count);
        foreach (var charId in _charIds)
        {
            if (_entities.CharEntity(charId) is not { } entity) continue;
            var uuid = _entities.Uuid(entity);
            _present.Add(uuid);
            Step(entity, uuid, RelationOf(entity, uuid, local));
        }
        _bodies.Prune(_present);
        _weapons.Prune(_present);
        OnPass(why, _charIds.Count, before);
    }

    private void Step(object entity, long uuid, CharacterRelation relation)
    {
        if (_bodies.Step(uuid, CharacterHideRules.HideBody(_requested, relation)) is { } hideBody &&
            !SetBodyHidden(entity, hideBody))
            _bodies.Failed(uuid, hideBody);
        if (_weapons.Step(uuid, CharacterHideRules.HideWeapon(_requested, relation)) is { } hideWeapon &&
            !SetWeaponHidden(entity, hideWeapon))
            _weapons.Failed(uuid, hideWeapon);
    }

    private void ReapplyWeapons()
    {
        _drain.Clear();
        _drain.AddRange(_weaponReapply);
        _weaponReapply.Clear();
        if (!Resolve()) return;
        foreach (var uuid in _drain)
        {
            if (_entities.EntityByUuid(uuid) is not { } entity) continue;
            var want = CharacterHideRules.HideWeapon(_requested, CharacterRelation.None);   // the local one never queues
            if (_weapons.Step(uuid, want) is { } hide && !SetWeaponHidden(entity, hide)) _weapons.Failed(uuid, hide);
            OnWeaponReapplied(uuid, want);
        }
    }

    /// <summary>The relations that decide a hide. Membership is read only while a group layer is requested — without
    /// one, <see cref="CharacterHideRules.HideBody"/> is false whatever the character is.</summary>
    private CharacterRelation RelationOf(object entity, long uuid, long local)
    {
        if (uuid == local) return CharacterRelation.Local;
        if ((_requested & CharacterHideRules.Groups) == 0) return CharacterRelation.None;
        var relation = CharacterRelation.None;
        if (InParty(CharIdOf(entity))) relation |= CharacterRelation.Party;
        if (IsFriend(uuid)) relation |= CharacterRelation.Friend;
        if (IsGuildMate(uuid)) relation |= CharacterRelation.Guild;
        return relation;
    }

    private bool InParty(long charId)
    {
        if (charId == 0) return false;
        foreach (var m in _party())
            if (m.CharId == charId && !m.IsSelf) return true;
        return false;
    }

    private bool OnMainThread => Environment.CurrentManagedThreadId == _mainThread;

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _log.Warning(Tag + message);
    }

    partial void OnPass(string why, int characters, (int Bodies, int Weapons) before);
    partial void OnWeaponReapplied(long uuid, bool hidden);
}
