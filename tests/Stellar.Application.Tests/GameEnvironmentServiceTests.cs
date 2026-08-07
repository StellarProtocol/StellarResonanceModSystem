using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using System.Collections.Generic;
using Xunit;

namespace Stellar.Application.Tests;

public class GameEnvironmentServiceTests
{
    private sealed class StubInstallInfo : IInstallInfo
    {
        public string? GameRootPath { get; set; }
        public string? ExecutableName { get; set; }
    }

    private sealed class StubConfigSection : IConfigSection
    {
        private readonly Dictionary<string, object?> _store = new();
        public T? Get<T>(string key, T? defaultValue)
            => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
    }

    private const string SeaRoot = "/opt/game/BlueProtocol2/drive_c/Star/StarLauncher/game/release_2.11/game_mini";

    [Fact]
    public void SeaExecutable_DetectsSea()
    {
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = SeaRoot, ExecutableName = "StarSEA.exe" },
            new StubConfigSection());
        Assert.Equal(GameRegion.Sea, svc.Region);
        Assert.Equal("sea", svc.RegionCode);
    }

    // Regression: the Steam SEA build's process exe is StarSEA_STEAM.exe (confirmed
    // 2026-08-07 — reported as region Unknown → uploads withheld). Prefix matching on
    // "StarSEA" resolves it to SEA without a per-channel row. See UnknownExecutable_*
    // below, which pins that the prefix does NOT over-match an unrelated exe.
    [Fact]
    public void SteamSeaExecutable_DetectsSea()
    {
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = SeaRoot, ExecutableName = "StarSEA_STEAM.exe" },
            new StubConfigSection());
        Assert.Equal(GameRegion.Sea, svc.Region);
        Assert.Equal("sea", svc.RegionCode);
        Assert.Equal("install-marker", svc.RegionSource);
    }

    // Real JP install layout (owner's machine, 2026-07-11): Windows path, same
    // StarLauncher/release_<ver>/game_mini shape as SEA, executable StarASIA.exe.
    private const string JpRoot = @"E:\bpsr\StarLauncher\game\release_2.11\game_mini";

    [Fact]
    public void JpExecutable_DetectsJp()
    {
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = JpRoot, ExecutableName = "StarASIA.exe" },
            new StubConfigSection());
        Assert.Equal(GameRegion.Jp, svc.Region);
        Assert.Equal("jp", svc.RegionCode);
    }

    [Fact]
    public void JpInstallPath_ParsesGameVersion_FromWindowsPath()
    {
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = JpRoot, ExecutableName = "StarASIA.exe" },
            new StubConfigSection());
        Assert.Equal("2.11", svc.GameVersion);
    }

    [Fact]
    public void UnknownExecutable_DetectsUnknown()
    {
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = "/somewhere/else", ExecutableName = "Game.exe" },
            new StubConfigSection());
        Assert.Equal(GameRegion.Unknown, svc.Region);
        Assert.Equal("unknown", svc.RegionCode);
    }

    [Fact]
    public void NullInstallFacts_DetectsUnknown_NeverThrows()
    {
        var svc = new GameEnvironmentService(new StubInstallInfo(), new StubConfigSection());
        Assert.Equal(GameRegion.Unknown, svc.Region);
        Assert.Equal("unknown", svc.GameVersion);
    }

    [Fact]
    public void ConfigOverride_WinsOverDetection()
    {
        var section = new StubConfigSection();
        section.Set("region", "jp");
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = SeaRoot, ExecutableName = "StarSEA.exe" },
            section);
        Assert.Equal(GameRegion.Jp, svc.Region);
        Assert.Equal("jp", svc.RegionCode);
    }

    [Fact]
    public void InvalidConfigOverride_FallsBackToDetection()
    {
        var section = new StubConfigSection();
        section.Set("region", "mars");
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = SeaRoot, ExecutableName = "StarSEA.exe" },
            section);
        Assert.Equal(GameRegion.Sea, svc.Region);
    }

    [Fact]
    public void GameVersion_ParsedFromReleaseSegment()
    {
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = SeaRoot, ExecutableName = "StarSEA.exe" },
            new StubConfigSection());
        Assert.Equal("2.11", svc.GameVersion);
    }

    [Fact]
    public void ConfigOverride_IsCaseInsensitive()
    {
        var section = new StubConfigSection();
        section.Set("region", "JP");
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = SeaRoot, ExecutableName = "StarSEA.exe" },
            section);
        Assert.Equal(GameRegion.Jp, svc.Region);
    }

    [Fact]
    public void ValidConfigOverride_RegionSourceIsConfig()
    {
        var section = new StubConfigSection();
        section.Set("region", "jp");
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = SeaRoot, ExecutableName = "StarSEA.exe" },
            section);
        Assert.Equal("config", svc.RegionSource);
    }

    [Fact]
    public void InvalidConfigOverride_RegionSourceIsInstallMarker()
    {
        var section = new StubConfigSection();
        section.Set("region", "mars");
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = SeaRoot, ExecutableName = "StarSEA.exe" },
            section);
        Assert.Equal(GameRegion.Sea, svc.Region);
        Assert.Equal("install-marker", svc.RegionSource);
    }

    [Fact]
    public void NoOverride_RegionSourceIsInstallMarker()
    {
        var svc = new GameEnvironmentService(
            new StubInstallInfo { GameRootPath = SeaRoot, ExecutableName = "StarSEA.exe" },
            new StubConfigSection());
        Assert.Equal("install-marker", svc.RegionSource);
    }
}
