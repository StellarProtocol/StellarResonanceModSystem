using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// PINNED REGRESSION — identity must survive a world-entity attribute blackout.
///
/// <para>Reported 2026-07-29: relaunching while mounted left the CombatMeter's own
/// row rendering the literal <c>"Self"</c> with no class crest, because the probe
/// read every field off the world entity's attribute bag and that bag was empty
/// (<c>hp=0, stamina=0, lvl=0, name=''</c>). One failed attribute read blanked the
/// name AND the crest AND the hp together. The fix decouples identity (served from
/// the char record) from liveness (vitals/position, still entity-sourced).</para>
///
/// <para>Do not weaken these: <see cref="IdentitySurvivesFailedSample"/> and
/// <see cref="VitalsStayGatedWhenSampleFails"/> together pin BOTH halves — that
/// identity keeps working when the entity is dark, and that vitals do not start
/// reporting stale/garbage values as a side effect.</para>
/// </summary>
public sealed class PlayerStateIdentityTests
{
    // Probe double: TrySample and TryReadIdentity are controlled independently so
    // a test can reproduce "entity dark, record readable".
    private sealed class FakeProbe : IPlayerStateProbe
    {
        public bool SampleOk { get; set; }
        public PlayerStateSnapshot Sample { get; set; }

        public bool IdentityOk { get; set; }
        public PlayerIdentitySnapshot Identity { get; set; }

        public bool TrySample(out PlayerStateSnapshot snapshot)
        {
            snapshot = Sample;
            return SampleOk;
        }

        public bool TryReadIdentity(out PlayerIdentitySnapshot identity)
        {
            identity = Identity;
            return IdentityOk;
        }
    }

    private static PlayerIdentitySnapshot Revette(long charId = 1248014) => new()
    {
        CharId = charId,
        Name = "Revette",
        Level = 60,
        Profession = 2,
    };

    [Fact]
    public void IdentitySurvivesFailedSample()
    {
        var probe = new FakeProbe { SampleOk = false, IdentityOk = true, Identity = Revette() };
        var service = new PlayerStateService(new StubClientState());

        service.Refresh(probe);

        // The entity is dark — IsAvailable must still report that honestly...
        Assert.False(service.IsAvailable);
        // ...but the identity the client plainly knows must be served anyway.
        Assert.Equal("Revette", service.Name);
        Assert.Equal(60, service.Level);
        Assert.Equal(2, service.Profession);
    }

    [Fact]
    public void VitalsStayGatedWhenSampleFails()
    {
        var probe = new FakeProbe { SampleOk = false, IdentityOk = true, Identity = Revette() };
        var service = new PlayerStateService(new StubClientState());

        service.Refresh(probe);

        // Identity is recoverable from the record; vitals and position are NOT,
        // so they must stay at defaults rather than leak a stale value.
        Assert.Equal(0, service.Health);
        Assert.Equal(0, service.MaxHealth);
        Assert.Equal(0, service.Stamina);
        Assert.Equal(0, service.MaxStamina);
        Assert.Equal(Position3D.Zero, service.Position);
    }

    [Fact]
    public void LiveEntityValuesWinOverStickyIdentity()
    {
        var probe = new FakeProbe
        {
            IdentityOk = true,
            Identity = Revette(),
            SampleOk = true,
            // The live entity reports a DIFFERENT profession — the player switched
            // class this session, and the entity tracks that immediately.
            Sample = new PlayerStateSnapshot { Name = "Revette", Level = 60, Profession = 5, MaxHealth = 15000 },
        };
        var service = new PlayerStateService(new StubClientState());

        service.Refresh(probe);

        Assert.True(service.IsAvailable);
        Assert.Equal(5, service.Profession);
    }

    [Fact]
    public void StickyIdentityFillsGapsInALiveSample()
    {
        // Entity is live enough to pass the probe's own gate (MaxHealth > 0) but
        // its name/level/profession attrs are still empty.
        var probe = new FakeProbe
        {
            IdentityOk = true,
            Identity = Revette(),
            SampleOk = true,
            Sample = new PlayerStateSnapshot { Name = null, Level = 0, Profession = 0, MaxHealth = 15000 },
        };
        var service = new PlayerStateService(new StubClientState());

        service.Refresh(probe);

        Assert.True(service.IsAvailable);
        Assert.Equal("Revette", service.Name);
        Assert.Equal(60, service.Level);
        Assert.Equal(2, service.Profession);
    }

    [Fact]
    public void IdentityIsNotDowngradedByALaterUnreadableRecord()
    {
        var probe = new FakeProbe { SampleOk = false, IdentityOk = true, Identity = Revette() };
        var service = new PlayerStateService(new StubClientState());
        service.Refresh(probe);

        // The record goes unreadable (scene teardown). A false return means "not
        // known right now", never "cleared" — the known identity must persist.
        probe.IdentityOk = false;
        service.Refresh(probe);

        Assert.Equal("Revette", service.Name);
        Assert.Equal(60, service.Level);
        Assert.Equal(2, service.Profession);
    }

    [Fact]
    public void CharacterSwitchDropsTheStaleIdentity()
    {
        var probe = new FakeProbe { SampleOk = false, IdentityOk = true, Identity = Revette() };
        var service = new PlayerStateService(new StubClientState());
        service.Refresh(probe);
        Assert.Equal("Revette", service.Name);

        // A different character logs in and the record carries only a char id so
        // far. The previous character's name must NOT be attributed to them.
        probe.Identity = new PlayerIdentitySnapshot { CharId = 9999999, Name = null, Level = 0, Profession = 0 };
        service.Refresh(probe);

        Assert.Null(service.Name);
        Assert.Equal(0, service.Level);
        Assert.Equal(0, service.Profession);
    }

    [Fact]
    public void IdentityStaysUnavailableWhenTheProbeHasNoRecordSource()
    {
        // Host may wire no char-record source at all; behaviour must then be
        // exactly what it was before the identity path existed.
        var probe = new FakeProbe { SampleOk = false, IdentityOk = false };
        var service = new PlayerStateService(new StubClientState());

        service.Refresh(probe);

        Assert.False(service.IsAvailable);
        Assert.Null(service.Name);
        Assert.Equal(0, service.Level);
        Assert.Equal(0, service.Profession);
    }
}
