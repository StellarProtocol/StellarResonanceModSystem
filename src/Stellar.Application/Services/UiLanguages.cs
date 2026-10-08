using System.Collections.Generic;

namespace Stellar.Application.Services;

/// <summary>
/// The ONE list of Stellar UI languages. Every consumer — the engine's supported set, the resource scan,
/// the Settings dropdown — reads this, so a language cannot be half-added (the Filipino deploy showed a
/// code selectable in Infrastructure but unknown to Application). Order = dropdown order.
/// </summary>
internal static class UiLanguages
{
    public static IReadOnlyList<string> Codes { get; } = new[] { "en", "ja", "th", "id", "fil", "ko" };

    /// <summary>Each language's name in its own script, index-aligned to <see cref="Codes"/>.</summary>
    public static IReadOnlyList<string> NativeNames { get; } =
        new[] { "English", "日本語", "ไทย", "Bahasa Indonesia", "Filipino", "한국어" };
}
