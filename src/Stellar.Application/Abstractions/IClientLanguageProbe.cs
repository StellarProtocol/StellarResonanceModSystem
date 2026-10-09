using System;

namespace Stellar.Application.Abstractions;

/// <summary>
/// The game client's current UI language mapped to a Stellar-supported code (<c>"en"</c>, <c>"ja"</c>,
/// <c>"ko"</c>, <c>"th"</c>, <c>"id"</c>), or <c>"en"</c> when the client language is unsupported or not yet KNOWN
/// (Filipino has no game client language and is never returned here). Backs the localization engine's
/// <c>follow</c> setting. Implemented by <see cref="Services.ClientLanguageLatch"/>.
/// </summary>
internal interface IClientLanguageProbe
{
    /// <summary>The client UI language mapped to a supported code, defaulting to <c>"en"</c> while unknown.</summary>
    string SupportedLanguage { get; }

    /// <summary>
    /// Raised (main thread, from a game-side signal — never from a <see cref="SupportedLanguage"/> read) when the
    /// known client language changes: its first latch after the game sets it, or a later in-game switch.
    /// </summary>
    event Action? Changed;
}
