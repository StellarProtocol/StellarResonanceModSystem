namespace Stellar.Application.Abstractions;

/// <summary>
/// Read-only view of the latched game client language for Infrastructure consumers (the game-data probe's design-label
/// fallback). Implemented by <see cref="Services.ClientLanguageLatch"/>; reads never touch the game.
/// </summary>
internal interface IClientLanguageInfo
{
    /// <summary>The latched <c>LanguageType</c> index, or <c>-1</c> while the client language is not yet known.</summary>
    int CurrentLanguageIndex { get; }

    /// <summary>True when the KNOWN client language is Simplified/Traditional Chinese (false while unknown).</summary>
    bool IsDesignLanguage { get; }
}
