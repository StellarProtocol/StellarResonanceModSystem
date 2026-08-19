namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound port implemented by Infrastructure: the game client's current UI language
/// mapped to a Stellar-supported code (<c>"en"</c>, <c>"ja"</c>, <c>"th"</c>, <c>"id"</c>),
/// or <c>"en"</c> when the client language is unsupported or not yet readable. Backs the
/// localization engine's <c>follow</c> setting.
/// </summary>
internal interface IClientLanguageProbe
{
    /// <summary>The client UI language mapped to a supported code, defaulting to <c>"en"</c>.</summary>
    string SupportedLanguage { get; }
}
