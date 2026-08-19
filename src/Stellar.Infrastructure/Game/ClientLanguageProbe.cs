using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IClientLanguageProbe"/> over <see cref="PandaClientLanguage"/>: maps the game's
/// <c>LanguageType</c> index to a Stellar-supported code. The four supported UI languages map
/// from <c>en=1, ja=2, th=5, id=6</c>; every other client language (Chinese, Korean, European)
/// falls back to English — Stellar ships no catalog for them.
/// </summary>
internal sealed class ClientLanguageProbe : IClientLanguageProbe
{
    private readonly PandaClientLanguage _lang;

    public ClientLanguageProbe(PandaClientLanguage lang) => _lang = lang;

    public string SupportedLanguage => _lang.CurrentLanguageIndex switch
    {
        1 => "en",
        2 => "ja",
        5 => "th",
        6 => "id",
        _ => "en",
    };
}
