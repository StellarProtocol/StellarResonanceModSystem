using System;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Pins the wire-position fallback POLICY — conditional substitution:
/// the wire may substitute ONLY for the two measured degenerate view shapes (exact zero sentinel /
/// near-origin jitter below a real wire floor — run sea/223237664013287424, Thanatos walk-in), and
/// NEVER for a healthy view read. Self-AttrPos re-broadcasts TELEPORT-PAD anchor coordinates while
/// the player is elsewhere (measured run sea/i06l2Q07Fk: fresh wire said the lobby pad
/// (398.1, 323.8, −42.7) while the live GO walked the arena at Y≈101), so both the unconditional
/// wire-first policy and the older |GO.Y − wire.Y| heuristic painted pad coordinates into the
/// replay; the pad pins below must never be weakened.
/// </summary>
public sealed class EntityTransformsFallbackTests
{
    // Real numbers from run sea/223237664013287424 (degenerate walk-in)…
    private static readonly Position3D JitterGo = new(12f, 0.3f, -8f);          // near-origin jitter (NOT zero-sentinel)
    private static readonly Position3D WireFloor = new(150.8f, 100.2f, -304.1f); // fresh wire (true boss-room floor)
    // …and from run sea/i06l2Q07Fk (pad broadcast vs healthy view).
    private static readonly Position3D ArenaGo = new(-12.25f, 100.9f, 0.2f);     // live GO, walking the arena
    private static readonly Position3D LobbyPadWire = new(398.1f, 323.8f, -42.7f); // fresh wire = teleport-pad anchor

    private sealed class StubTypeRegistry : IGameTypeRegistry
    {
        public Type? FindType(string fullName) => null;
    }

    private static EntityTransformsService NewService(WireEntityPositions cache, StubLog log)
        => new(new StubTypeRegistry(), cache, log);

    // ── Pure decision (IsDegenerateGoView) ───────────────────────────────────

    [Fact]
    public void Decision_ZeroSentinelGo_IsDegenerate()
        => Assert.True(EntityTransformsService.IsDegenerateGoView(Position3D.Zero, WireFloor));

    [Fact]
    public void Decision_NearOriginJitterGo_BelowWireFloor_IsDegenerate()
        => Assert.True(EntityTransformsService.IsDegenerateGoView(JitterGo, WireFloor));

    [Fact]
    public void Decision_SettledGo_IsHealthy()
    {
        // Both on the real floor (ΔY ≈ 0.4 m) — GO is healthy even though X/Z differ ~5 m.
        var settledGo = new Position3D(170f, 100.5f, -290f);
        var wire = new Position3D(165f, 100.1f, -295f);
        Assert.False(EntityTransformsService.IsDegenerateGoView(settledGo, wire));
    }

    [Fact]
    public void Decision_PadBroadcastWire_NeverDegradesHealthyGo()
    {
        // REGRESSION PIN (run sea/i06l2Q07Fk): a pad-anchor wire broadcast 220 m above the live GO
        // floor must NOT mark the healthy GO degenerate — the old Y-disagreement heuristic and
        // wire-first both failed exactly here. Never weaken this pin.
        Assert.False(EntityTransformsService.IsDegenerateGoView(ArenaGo, LobbyPadWire));
    }

    [Fact]
    public void Decision_YEpsilonBoundary()
    {
        var wire = new Position3D(0f, 100f, 0f);
        // |GO.Y| exactly at the epsilon → still a degenerate candidate; just past it → healthy.
        Assert.True(EntityTransformsService.IsDegenerateGoView(new Position3D(5f, 1.0f, 5f), wire));
        Assert.False(EntityTransformsService.IsDegenerateGoView(new Position3D(5f, 1.1f, 5f), wire));
    }

    // ── Instance path (MaybeApplyWireFallback) ───────────────────────────────

    [Fact]
    public void Fallback_JitterGoWithFreshWire_SubstitutesAndFiresOneShot()
    {
        var cache = new WireEntityPositions(() => 0);
        var log = new StubLog();
        var svc = NewService(cache, log);
        cache.OnPosition(555, new WirePos(WireFloor.X, WireFloor.Y, WireFloor.Z, 0f, false));

        var pos = JitterGo;
        var yaw = 0f;
        svc.MaybeApplyWireFallback(new EntityId(555), ref pos, ref yaw);

        Assert.Equal(WireFloor.X, pos.X, 3);
        Assert.Equal(WireFloor.Y, pos.Y, 3);
        Assert.Equal(WireFloor.Z, pos.Z, 3);
        // (e) the one-shot fallback-engaged line fires on the degenerate branch.
        Assert.Contains(log.InfoLines, l => l.Contains("wire-position fallback engaged for entity 555"));
    }

    [Fact]
    public void Fallback_NoFreshWire_KeepsGo_FailsSafe()
    {
        var cache = new WireEntityPositions(() => 0); // empty
        var log = new StubLog();
        var svc = NewService(cache, log);

        var pos = JitterGo;
        var yaw = 0f;
        svc.MaybeApplyWireFallback(new EntityId(555), ref pos, ref yaw);

        Assert.Equal(JitterGo.X, pos.X, 3); // unchanged — today's behavior
        Assert.Equal(JitterGo.Y, pos.Y, 3);
        Assert.Empty(log.InfoLines);
    }

    [Fact]
    public void Fallback_HealthyGo_FreshDifferentWire_KeepsGo()
    {
        // Supersedes the wire-first pin: a fresh wire sample must NOT displace a healthy view read,
        // even when the two differ — the wire may be a pad re-broadcast (see class doc).
        var cache = new WireEntityPositions(() => 0);
        var log = new StubLog();
        var svc = NewService(cache, log);
        cache.OnPosition(555, new WirePos(165f, 100.1f, -295f, 0f, false));

        var pos = new Position3D(170f, 100.5f, -290f); // settled GO on the same floor
        var yaw = 0f;
        svc.MaybeApplyWireFallback(new EntityId(555), ref pos, ref yaw);

        Assert.Equal(170f, pos.X, 3);   // GO kept
        Assert.Empty(log.InfoLines);
    }

    [Fact]
    public void Fallback_PadBroadcastWire_KeepsHealthyGo()
    {
        // REGRESSION PIN (run sea/i06l2Q07Fk): fresh lobby-pad wire while the live GO walks the
        // arena — the pad must not be recorded. Never weaken this pin.
        var cache = new WireEntityPositions(() => 0);
        var log = new StubLog();
        var svc = NewService(cache, log);
        cache.OnPosition(555, new WirePos(LobbyPadWire.X, LobbyPadWire.Y, LobbyPadWire.Z, 0f, false));

        var pos = ArenaGo;
        var yaw = 0f;
        svc.MaybeApplyWireFallback(new EntityId(555), ref pos, ref yaw);

        Assert.Equal(ArenaGo.X, pos.X, 3);   // live GO kept — pad rejected
        Assert.Equal(ArenaGo.Y, pos.Y, 3);
        Assert.Equal(ArenaGo.Z, pos.Z, 3);
        Assert.Empty(log.InfoLines);
    }

    [Fact]
    public void Fallback_FreshButDegenerateWire_KeepsGo()
    {
        // "wire-degenerate" skip: a fresh zero-sentinel wire entry is unusable — GO stays.
        var cache = new WireEntityPositions(() => 0);
        var log = new StubLog();
        var svc = NewService(cache, log);
        cache.OnPosition(555, new WirePos(0f, 0f, 0f, 0f, false));

        var pos = JitterGo;
        var yaw = 0f;
        svc.MaybeApplyWireFallback(new EntityId(555), ref pos, ref yaw);

        Assert.Equal(JitterGo.X, pos.X, 3);
        Assert.Equal(JitterGo.Y, pos.Y, 3);
        Assert.Empty(log.InfoLines);   // no fallback-engaged one-shot
    }

    [Fact]
    public void Fallback_StaleWire_KeepsGo()
    {
        // "no-fresh-wire" skip via staleness: an entry older than the freshness window is
        // never served, so GO stays (same fail-safe as the empty-cache case).
        long nowMs = 0;
        var cache = new WireEntityPositions(() => nowMs);
        var log = new StubLog();
        var svc = NewService(cache, log);
        cache.OnPosition(555, new WirePos(WireFloor.X, WireFloor.Y, WireFloor.Z, 0f, false));
        nowMs = EntityTransformsService.WirePositionMaxStaleMs + 1;

        var pos = JitterGo;
        var yaw = 0f;
        svc.MaybeApplyWireFallback(new EntityId(555), ref pos, ref yaw);

        Assert.Equal(JitterGo.X, pos.X, 3);
        Assert.Empty(log.InfoLines);
    }

    [Fact]
    public void Fallback_WireYaw_SubstitutedAndNormalized()
    {
        // When the substitution engages (degenerate GO) and the wire entry carries a facing
        // (AttrDir/Position.dir), the yaw is substituted along with the position and normalised
        // into [0,360) to match the GO path's contract.
        var cache = new WireEntityPositions(() => 0);
        var log = new StubLog();
        var svc = NewService(cache, log);
        cache.OnPosition(555, new WirePos(WireFloor.X, WireFloor.Y, WireFloor.Z, -90f, true));

        var pos = JitterGo;
        var yaw = 12f;
        svc.MaybeApplyWireFallback(new EntityId(555), ref pos, ref yaw);

        Assert.Equal(WireFloor.X, pos.X, 3);
        Assert.Equal(270f, yaw, 3);    // -90 normalised into [0,360)
    }
}
