namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface — supplies the running game's install facts (game root
/// directory and main executable file name). Implemented in Infrastructure
/// (BepInEx <c>Paths</c> + the current process); kept behind this port so the
/// pure detection logic in Application stays unit-testable.
/// </summary>
internal interface IInstallInfo
{
    /// <summary>Absolute game root directory (BepInEx <c>Paths.GameRootPath</c>), or null when unavailable.</summary>
    string? GameRootPath { get; }

    /// <summary>File name of the game executable (e.g. <c>"StarSEA.exe"</c>), or null when unavailable.</summary>
    string? ExecutableName { get; }
}
