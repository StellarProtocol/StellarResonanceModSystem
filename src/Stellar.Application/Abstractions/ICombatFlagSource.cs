using System;
namespace Stellar.Application.Abstractions;

internal enum CombatFlagKind { LocalCombatData, InBattleShow }

/// <summary>The game's client-side combat flags: postfixes on its own local setters, plus a one-shot read.</summary>
internal interface ICombatFlagSource
{
    /// <summary>Raised on the main thread when the game sets one of its local combat flags for the local player.</summary>
    event Action<CombatFlagKind, bool>? Changed;
    /// <summary>One read of the local player's combat flag (scene change only — never polled); null when unknown.</summary>
    bool? ReadLocalInCombat();
    /// <summary>Installs the setter postfixes on first use.</summary>
    void EnsureHooks();
}
