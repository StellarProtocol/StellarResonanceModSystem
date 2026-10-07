using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated lines that prove the per-character hides on an owner pass (each capped; the entity
/// lines dedupe per ENTITY, not per call — process rules § 57.8).</summary>
internal sealed partial class GameCharacterHides
{
    private const int MaxPassLines = 300;
    private const int MaxEntityLines = 200;
    private const int MaxEventLines = 100;
    private readonly HashSet<long> _loggedEntered = new();
    private int _passLines, _weaponLines, _eventLines;

    partial void OnPass(string why, int characters, (int Bodies, int Weapons) before)
    {
        if (!StellarDiagnostics.IsEnabled || _passLines++ >= MaxPassLines) return;
        _log.Info($"[PhotoVis] characters pass={why} req={_requested} chars={characters} " +
                  $"bodiesHidden={_bodies.Count} (was {before.Bodies}) weaponsHidden={_weapons.Count} (was {before.Weapons})");
    }

    partial void OnEntityStepped(long uuid, CharacterRelation relation)
    {
        if (!StellarDiagnostics.IsEnabled || _loggedEntered.Count >= MaxEntityLines || !_loggedEntered.Add(uuid)) return;
        _log.Info($"[PhotoVis] character entered uuid={uuid} rel={relation} body={(_bodies.Holds(uuid) ? "hidden" : "shown")} " +
                  $"weapon={(_weapons.Holds(uuid) ? "hidden" : "shown")}");
    }

    partial void OnWeaponReapplied(long uuid, bool hidden)
    {
        if (!StellarDiagnostics.IsEnabled || _weaponLines++ >= MaxEntityLines) return;
        _log.Info($"[PhotoVis] weapon rebuilt uuid={uuid} re-hidden={hidden && _weapons.Holds(uuid)}");
    }

    partial void OnLocalRebuilt(string why)
    {
        if (!StellarDiagnostics.IsEnabled || _eventLines++ >= MaxEventLines) return;
        _log.Info($"[PhotoVis] local player rebuilt ({why}) -> Me/Weapon re-assert next tick");
    }

    partial void OnMembershipChangedLogged(string why)
    {
        if (!StellarDiagnostics.IsEnabled || _eventLines++ >= MaxEventLines) return;
        _log.Info($"[PhotoVis] membership changed ({why}) pending={_membershipDirty}");
    }
}
