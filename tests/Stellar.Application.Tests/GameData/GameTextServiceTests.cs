using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

/// <summary>IGameText over the REAL embedded table (so a regenerated table that drops these rows fails here).</summary>
public sealed class GameTextServiceTests
{
    private static GameTextService Loaded(out List<Action> posted)
    {
        var queue = new List<Action>();
        var svc = new GameTextService(new StubLog(), queue.Add);
        svc.LoadAndPublish();
        posted = queue;
        return svc;
    }

    [Theory]
    // Unnamed sub-buffs / damage zones from CombatMeter's Skill Breakdown: the framework names them "Buff <id>".
    [InlineData(2203291, "Firmament Fall-child buff")]
    [InlineData(2203521, "Steel Beak - child buff, stack")]
    [InlineData(55240, "Light Energy Bombardment damage zone")]
    public void UnnamedBuff_ResolvesToDesignName_FlaggedAsFallback(int id, string expected)
    {
        var svc = Loaded(out _);
        Assert.True(svc.IsReady);
        Assert.Null(svc.GetName(GameTextKind.Buff, id));
        Assert.Equal(expected, svc.GetDesignName(GameTextKind.Buff, id));

        Assert.Equal(expected, svc.ResolveDisplayName(GameTextKind.Buff, id, $"Buff {id}", out var design));
        Assert.True(design);
        // A curated framework stand-in name for an internal row is also replaced by the per-row design name.
        Assert.Equal(expected, svc.ResolveDisplayName(GameTextKind.Buff, id, "Feather Burst (AoE sub-buff)", out design));
        Assert.True(design);
    }

    [Fact]
    public void RealNamedBuff_KeepsGameName_NoFallbackFlag()
    {
        var svc = Loaded(out _);
        Assert.Equal("Burn", svc.GetName(GameTextKind.Buff, 4501));
        Assert.Equal("Burn", svc.ResolveDisplayName(GameTextKind.Buff, 4501, "Burn", out var design));
        Assert.False(design);
        // Another client language: the game's own localized name wins over the English table.
        Assert.Equal("燃焼", svc.ResolveDisplayName(GameTextKind.Buff, 4501, "燃焼", out design));
        Assert.False(design);
        // No usable game name → the English real name, still not a design fallback.
        Assert.Equal("Burn", svc.ResolveDisplayName(GameTextKind.Buff, 4501, "Buff 4501", out design));
        Assert.False(design);
        Assert.False(string.IsNullOrEmpty(svc.GetDescription(GameTextKind.Buff, 4501)));
    }

    [Fact]
    public void RealNamedSkill_ResolvesFromSkillTable()
    {
        var svc = Loaded(out _);
        Assert.Equal("Radiance Barrage", svc.GetName(GameTextKind.Skill, 2234));
        Assert.Equal("Radiance Barrage", svc.ResolveDisplayName(GameTextKind.Skill, 2234, null, out var design));
        Assert.False(design);
    }

    [Fact]
    public void UnknownId_FallsBackToGameNameThenIdLabel()
    {
        var svc = Loaded(out _);
        Assert.Equal("Something", svc.ResolveDisplayName(GameTextKind.Buff, 987654321, "Something", out var design));
        Assert.False(design);
        Assert.Equal("Skill 987654321", svc.ResolveDisplayName(GameTextKind.Skill, 987654321, "", out design));
        Assert.False(design);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("  ", true)]
    [InlineData("气刃突刺计数", true)]
    [InlineData("Spirit Blade Thrust count", true)]
    [InlineData("场地标记01", true)]
    [InlineData("Buff 2203291", true)]
    [InlineData("Skill 12", true)]
    [InlineData("Buff ", false)]
    [InlineData("Skill 12a", false)]
    [InlineData("Burn", false)]
    public void IsPlaceholderName(string? name, bool expected)
        => Assert.Equal(expected, new GameTextService(new StubLog(), _ => { }).IsPlaceholderName(name));

    [Fact]
    public void NotReady_ReturnsGameNamePath_ThenReadyFiresOnPostedTick()
    {
        using var gate = new ManualResetEventSlim(false);
        var queue = new List<Action>();
        var svc = new GameTextService(new StubLog(), a => { lock (queue) queue.Add(a); },
            () => { gate.Wait(5000); return GameTextTable.LoadEmbedded(); });
        int fired = 0;
        svc.Ready += () => fired++;                     // starts the background load (blocked on the gate)

        Assert.False(svc.IsReady);
        Assert.Null(svc.GetDesignName(GameTextKind.Buff, 2203291));
        Assert.Equal("Buff 2203291", svc.ResolveDisplayName(GameTextKind.Buff, 2203291, "Buff 2203291", out var design));
        Assert.False(design);

        gate.Set();
        SpinWait.SpinUntil(() => { lock (queue) return queue.Count > 0; }, 10000);
        Assert.True(svc.IsReady);
        Assert.Equal(0, fired);                         // not raised on the loader thread
        Action tick; lock (queue) tick = queue[0];
        tick();                                         // the framework's next main-thread tick
        Assert.Equal(1, fired);

        // A late subscriber (raced the publish) still gets one call on the next tick.
        int late = 0;
        svc.Ready += () => late++;
        Action lateTick; lock (queue) lateTick = queue[^1];
        lateTick();
        Assert.Equal(1, late);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void FailedLoad_PublishesEmptyTable_ResolvesGameName()
    {
        var log = new StubLog();
        var queue = new List<Action>();
        var svc = new GameTextService(log, queue.Add, () => throw new InvalidOperationException("boom"));
        svc.LoadAndPublish();
        foreach (var a in queue) a();
        Assert.True(svc.IsReady);
        Assert.Equal("Buff 2203291", svc.ResolveDisplayName(GameTextKind.Buff, 2203291, "", out _));
        Assert.Contains(log.WarningLines, l => l.Contains("failed to load"));
    }
}
