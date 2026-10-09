using System;
using System.Reflection;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IClientLanguageReader"/> over the game: one raw read of
/// <c>Panda.Utility.Localization.LocalizationMgr.CurrentLanguageTypeIndex</c> (a static <see cref="int"/> property
/// indexing the game's <c>Panda.Utility.Localization.LanguageType</c> enum — recon of <c>Panda.Script.dll</c>:
/// <c>zh_Hans=0, en=1, ja=2, zh_Hant=3, ko=4, th=5, id=6, de=7, fr=8, es=9, pt=10, en_TH=11, en_TW=12</c>).
///
/// <para>
/// Deliberately NO caching/latching here: the property's backing field reads <c>0</c> until the game's
/// <c>LocalizationMgr</c> initialises (after the framework wires), so whether a read can be trusted is decided by
/// <see cref="Stellar.Application.Services.ClientLanguageLatch"/>, which calls this only on game signals.
/// </para>
/// </summary>
internal sealed class PandaClientLanguage : IClientLanguageReader
{
    /// <summary>Full name of the game's localization manager (also the type the language-setter hooks bind to).</summary>
    public const string LocalizationMgrTypeName = "Panda.Utility.Localization.LocalizationMgr";

    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private readonly IGameTypeRegistry _typeRegistry;
    private PropertyInfo? _property;

    public PandaClientLanguage(IGameTypeRegistry typeRegistry) => _typeRegistry = typeRegistry;

    /// <inheritdoc />
    public int ReadLanguageIndex()
    {
        try
        {
            _property ??= _typeRegistry.FindType(LocalizationMgrTypeName)?.GetProperty("CurrentLanguageTypeIndex", AnyStatic);
            return _property?.GetValue(null) is int index ? index : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
