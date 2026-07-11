using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Identity of the running game install: which regional release it is and the
/// installed game version. Detected once at boot from install markers
/// (executable name / install layout); the framework config key
/// <c>environment.region</c> (<c>"sea"</c> | <c>"jp"</c>) overrides detection.
/// Values are latched at boot and never change during a session.
/// </summary>
public interface IGameEnvironment
{
    /// <summary>Detected region, or <see cref="GameRegion.Unknown"/> when no marker matched and no override is set.</summary>
    GameRegion Region { get; }

    /// <summary>Lowercase wire form of <see cref="Region"/>: <c>"sea"</c>, <c>"jp"</c> or <c>"unknown"</c>.</summary>
    string RegionCode { get; }

    /// <summary>Installed game version (e.g. <c>"2.11"</c>), or <c>"unknown"</c> when not parseable from the install.</summary>
    string GameVersion { get; }
}
