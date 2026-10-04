namespace Stellar.Infrastructure.Rendering;

/// <summary>The bridge add-on's snapshot status, read in one call.</summary>
internal readonly record struct ReShadeNativeStatus(bool Ready, bool Loading, int Frames, int TechniqueCount, bool Enabled);

/// <summary>Safe managed view of the Stellar ReShade bridge add-on's state and request exports (see
/// <see cref="ReShadeBridge"/>). Extracted so <see cref="ReShadeService"/> is testable without the native add-on.
/// Every member returns a default instead of throwing.</summary>
internal interface IReShadeNative
{
    /// <summary>True once the add-on is found in the process and its ABI version matches.</summary>
    bool IsLoaded { get; }
    ReShadeNativeStatus ReadStatus();
    /// <summary>Technique <paramref name="index"/> of the latest snapshot; <paramref name="effectFile"/> is the effect
    /// FILE name exactly as ReShade reports it (e.g. "Clarity.fx"). False when out of range.</summary>
    bool TryGetTechnique(int index, out string name, out string effectFile, out bool enabled);
    /// <summary>Path of the current preset, or null when none / not loaded.</summary>
    string? GetPreset();
    void RequestEnabled(bool on);
    /// <summary>Queues a technique toggle. A null <paramref name="effectFile"/> matches the name in any effect;
    /// <paramref name="save"/> false is a temporary override the add-on never writes to a preset.</summary>
    void RequestTechnique(string? effectFile, string name, bool on, bool save);
    /// <summary>Queues a preset switch. The add-on ignores it while no technique is listed — callers hold it.</summary>
    void RequestPreset(string path);
    /// <summary>Queues new ';'-separated effect / texture search paths; null leaves that setting unchanged.</summary>
    void RequestSearchPaths(string? effects, string? textures);
}
