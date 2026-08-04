using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED SAFETY GATE for blackout rescue (mounted => IPlayerState hp 0, owner
/// toggle 2026-07-29). When the manager's player entity goes dark the probe
/// re-looks-up the player by uuid / char id; this predicate decides whether the
/// replacement may be used.
///
/// <para>Do not loosen. A false accept feeds the MOUNT's — or another
/// character's — vitals and <b>position</b> into <c>IPlayerState</c>, and
/// position corruption breaks the CombatMeter replay's entry-to-end coverage,
/// which is a hard product requirement (P0).</para>
/// </summary>
public sealed class LocalPlayerEntityCheckTests
{
    private const long Self = 1248014;   // the owner's real char id
    private const long Other = 1959717569152;

    [Fact]
    public void AcceptsAMatchingCharId()
    {
        Assert.True(LocalPlayerEntityCheck.Validates(
            entityCharId: Self, expectedCharId: Self, isPlayerCtrl: false, isPlayer: false));
    }

    [Fact]
    public void RejectsAKnownMismatchEvenWhenThePlayerFlagsAreSet()
    {
        // The decisive case: two known-but-different char ids must lose to
        // NOTHING. If a mount/other entity ever reports IsPlayer, the char id
        // still wins and we reject.
        Assert.False(LocalPlayerEntityCheck.Validates(
            entityCharId: Other, expectedCharId: Self, isPlayerCtrl: true, isPlayer: true));
    }

    [Fact]
    public void FallsBackToPlayerControlFlagsWhenNoCharIdIsComparable()
    {
        // Entity exposes no char id (or the record hasn't landed) — the game's own
        // player-control flag is then the best evidence available.
        Assert.True(LocalPlayerEntityCheck.Validates(
            entityCharId: 0, expectedCharId: Self, isPlayerCtrl: true, isPlayer: false));
        Assert.True(LocalPlayerEntityCheck.Validates(
            entityCharId: Self, expectedCharId: 0, isPlayerCtrl: false, isPlayer: true));
    }

    [Fact]
    public void RejectsWhenNothingIdentifiesTheEntity()
    {
        // Unknown means NO — never adopt an entity on the hope that it is us.
        Assert.False(LocalPlayerEntityCheck.Validates(
            entityCharId: 0, expectedCharId: 0, isPlayerCtrl: false, isPlayer: false));
        Assert.False(LocalPlayerEntityCheck.Validates(
            entityCharId: 0, expectedCharId: Self, isPlayerCtrl: false, isPlayer: false));
    }
}
