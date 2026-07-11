using System;
using System.Text.RegularExpressions;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Detects the game region + version once at construction from install markers
/// supplied by the <see cref="IInstallInfo"/> outbound port. The framework
/// config override (section <c>environment</c>, key <c>region</c> =
/// <c>"sea"</c> | <c>"jp"</c>) wins over detection. Values are latched —
/// never re-read during the session. Never throws: unmatched installs yield
/// <see cref="GameRegion.Unknown"/> / <c>"unknown"</c>.
/// </summary>
internal sealed class GameEnvironmentService : IGameEnvironment
{
    // Install marker table: executable file name → region. The JP row is added
    // by Task 4 from the owner's JP install (spec §1 "First implementation task").
    private static readonly (string ExeName, GameRegion Region)[] ExeMarkers =
    {
        ("StarSEA.exe", GameRegion.Sea),
    };

    public GameRegion Region { get; }
    public string RegionCode { get; }
    public string GameVersion { get; }

    /// <summary>Where <see cref="Region"/> came from: "config" (valid override honored) or "install-marker" (detection, incl. no-match Unknown).</summary>
    public string RegionSource { get; }

    public GameEnvironmentService(IInstallInfo install, IConfigSection environmentSection)
    {
        (Region, RegionSource) = ResolveRegion(install, environmentSection);
        RegionCode = Region switch
        {
            GameRegion.Sea => "sea",
            GameRegion.Jp  => "jp",
            _              => "unknown",
        };
        GameVersion = ParseGameVersion(install.GameRootPath) ?? "unknown";
    }

    private static (GameRegion Region, string Source) ResolveRegion(IInstallInfo install, IConfigSection section)
    {
        var overrideCode = section.Get<string>("region", null);
        if (string.Equals(overrideCode, "sea", StringComparison.OrdinalIgnoreCase)) return (GameRegion.Sea, "config");
        if (string.Equals(overrideCode, "jp",  StringComparison.OrdinalIgnoreCase)) return (GameRegion.Jp, "config");

        var exe = install.ExecutableName;
        if (exe is not null)
        {
            foreach (var (name, region) in ExeMarkers)
                if (string.Equals(exe, name, StringComparison.OrdinalIgnoreCase))
                    return (region, "install-marker");
        }
        return (GameRegion.Unknown, "install-marker");
    }

    /// <summary>Parses the <c>release_&lt;ver&gt;</c> segment of the SEA install path. Null when absent (JP rule lands with Task 4).</summary>
    private static string? ParseGameVersion(string? gameRootPath)
    {
        if (string.IsNullOrEmpty(gameRootPath)) return null;
        var m = Regex.Match(gameRootPath, @"release_(\d+(?:\.\d+)*)");
        return m.Success ? m.Groups[1].Value : null;
    }
}
