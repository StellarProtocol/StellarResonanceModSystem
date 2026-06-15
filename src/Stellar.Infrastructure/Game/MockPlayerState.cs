using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Deterministic <see cref="IPlayerState"/> implementation activated by the
/// <c>STELLAR_MOCK_STATS=1</c> environment variable. Returns a fixed identity /
/// vitals / position snapshot so the PlayerHUD and StatInspector plugins can
/// render outside of real gameplay (title screen, character select) for the
/// Phase 9a.5 visual verification toolkit.
///
/// The snapshot is static — values never change and no refresh / poll path
/// exists. Visual scenarios sit on the title screen and capture the rendered
/// HUD; they don't exercise the polling cadence.
///
/// Production code path is unaffected when the env var is absent — the mock
/// is wired in <c>BootstrapPlugin.WireGameEventsAndPluginHost</c> only when
/// the env var equals <c>"1"</c>. The production
/// <c>PandaPlayerStateProbe</c> + <c>PlayerStateService</c> still run and
/// refresh per tick; the mock simply replaces what plugins see via
/// <see cref="IPluginServices.PlayerState"/>.
/// </summary>
internal sealed class MockPlayerState : IPlayerState
{
    public bool IsAvailable => true;
    public string? Name => "MockHero";
    public int Level => 60;

    // Profession 1 = Stormblade in the game's profession table (matches what a
    // generic level-60 fixture would project for icon / colour rendering).
    public int Profession => 1;

    public int Health => 12345;
    public int MaxHealth => 15000;
    public int Stamina => 80;
    public int MaxStamina => 100;
    public Position3D Position => new(1234.5f, 678.9f, 100.0f);
}
