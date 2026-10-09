namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound port implemented by Infrastructure: one raw read of the game client's current UI language as its
/// <c>LanguageType</c> index (<c>zh_Hans=0, en=1, ja=2, zh_Hant=3, ko=4, th=5, id=6, … en_TH=11</c>), or <c>-1</c>
/// when the game type/property cannot be read yet. NOT a "language is ready" answer: the game's static backing field
/// defaults to <c>0</c> until its localization manager initialises, so a <c>0</c> read alone is ambiguous —
/// <see cref="Services.ClientLanguageLatch"/> owns that decision.
/// </summary>
internal interface IClientLanguageReader
{
    /// <summary>The raw <c>LanguageType</c> index, or <c>-1</c> when unreadable.</summary>
    int ReadLanguageIndex();
}
