using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IClientLanguageProbe"/> over <see cref="PandaClientLanguage"/>: maps the game's
/// <c>LanguageType</c> index to a Stellar-supported code: en=1, ja=2, ko=4, th=5, id=6 (Filipino has no game
/// client language — it is selectable manually only); every other client language (Chinese, European) falls
/// back to English — Stellar ships no catalog for them.
/// </summary>
internal sealed class ClientLanguageProbe : IClientLanguageProbe
{
    private readonly PandaClientLanguage _lang;

    public ClientLanguageProbe(PandaClientLanguage lang) => _lang = lang;

    public string SupportedLanguage => _lang.CurrentLanguageIndex switch
    {
        1 => "en",
        2 => "ja",
        4 => "ko",
        5 => "th",
        6 => "id",
        _ => "en",
    };
}
