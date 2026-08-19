using System.Reflection;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED (review Critical, deepslumber session-leak): <see cref="PandaLoadoutProbe.ClearSession"/>
/// must blank the parsed Deep-Slumber state and the LIVE-line class/talents so a character switch
/// within one game process never serves the PREVIOUS character's data through
/// <see cref="IDeepSlumberProbe.Read"/> / <see cref="ILoadoutProbe.ReadLiveState"/> after logout.
///
/// Drives the private <c>UpdateDeepSlumberState</c> / <c>ReadLiveLine</c> parse steps directly via
/// reflection (both are pure string-in/field-out, no Lua bridge involved — the same rows
/// <see cref="PandaLoadoutProbeParseTests"/> already pins) to prime "logged-in with data" state
/// without needing a live IL2CPP host.
/// </summary>
public sealed class PandaLoadoutProbeClearSessionTests
{
    private sealed class FakeTypeRegistry : IGameTypeRegistry
    {
        public System.Type? FindType(string fullName) => null;   // bridge resolution not exercised here
    }

    private static void Prime(PandaLoadoutProbe probe)
    {
        const string raw =
            "CUR=1\n1\tAtk\t4\t106\t\t\t\n" +
            "LIVE\t200:2000835,201:2010937\t3:122,4:115,5:221\t4\t106\t69126,10442,1497\n" +
            "DSLV\t93:65,94:10\n" +
            "DSA\t93\t3\t1\t1\t120\t11:5110001\t\t21:4";

        Invoke(probe, "UpdateDeepSlumberState", raw);
        Invoke(probe, "ReadLiveLine", raw);
    }

    private static void Invoke(PandaLoadoutProbe probe, string methodName, string raw)
    {
        var m = typeof(PandaLoadoutProbe).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(m);
        m!.Invoke(probe, new object[] { raw });
    }

    [Fact]
    public void ClearSession_BlanksDeepSlumberState_SoARelogNeverServesThePreviousCharacter()
    {
        var probe = new PandaLoadoutProbe(new StubLog(), new FakeTypeRegistry());
        Prime(probe);

        Assert.NotNull(((IDeepSlumberProbe)probe).Read());   // sanity: primed state is really there

        probe.ClearSession();

        Assert.Null(((IDeepSlumberProbe)probe).Read());
    }

    [Fact]
    public void ClearSession_BlanksLiveLoadoutState_SoARelogNeverServesThePreviousCharacter()
    {
        var probe = new PandaLoadoutProbe(new StubLog(), new FakeTypeRegistry());
        Prime(probe);

        Assert.NotNull(((ILoadoutProbe)probe).ReadLiveState());   // sanity: primed state is really there

        probe.ClearSession();

        Assert.Null(((ILoadoutProbe)probe).ReadLiveState());
    }

    [Fact]
    public void ClearSession_ReArmsRefreshSoTheNextLoginRereadsPromptly()
    {
        var probe = new PandaLoadoutProbe(new StubLog(), new FakeTypeRegistry());
        Prime(probe);

        probe.ClearSession();

        var refreshPending = typeof(PandaLoadoutProbe).GetField("_refreshPending", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True((bool)refreshPending!.GetValue(probe)!);

        var lastDataRaw = typeof(PandaLoadoutProbe).GetField("_lastDataRaw", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Null(lastDataRaw!.GetValue(probe));
    }
}
