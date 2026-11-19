namespace Stellar.Abstractions.Services;

/// <summary>
/// What kind of effect a buff row is, guessed from its design label — the glyph an icon-less effect's placeholder tile
/// is drawn with (<see cref="IGameText.GetEffectCategory"/> → <see cref="IEffectPlaceholders.LoadEffectPlaceholder"/>).
/// The game has no category field for its ~8.6k icon-less buffs, so this is a keyword heuristic, not game data.
/// Values index the pre-drawn placeholder art — append only.
/// </summary>
public enum EffectCategory
{
    /// <summary>No keyword matched (most internal markers / counters): ring + centre dot.</summary>
    Default = 0,
    /// <summary>Stun, freeze, root, silence, slow, knock-up, taunt, fear…: spiral.</summary>
    CrowdControl = 1,
    /// <summary>Immunity, invincibility, super armor: shield + check.</summary>
    Immunity = 2,
    /// <summary>Damage-absorb shield or damage reduction: shield outline.</summary>
    Shield = 3,
    /// <summary>Healing, HP regeneration, lifesteal: plus.</summary>
    Heal = 4,
    /// <summary>Burn, poison, bleed, damage over time: droplet.</summary>
    DamageOverTime = 5,
    /// <summary>A stat increase: up arrow.</summary>
    StatUp = 6,
    /// <summary>A stat decrease or vulnerability: down arrow.</summary>
    StatDown = 7,
}
