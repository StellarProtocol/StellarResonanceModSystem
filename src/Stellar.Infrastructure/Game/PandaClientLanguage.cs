using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reads the game client's current UI language once and caches it. Encapsulates
/// the discovery of <c>Panda.Utility.Localization.LocalizationMgr.CurrentLanguageTypeIndex</c>
/// (a static <see cref="int"/> property whose value indexes the game's
/// <c>Panda.Utility.Localization.LanguageType</c> enum).
///
/// <para>
/// Recon (Cpp2IL / ILSpy of <c>Panda.Script.dll</c>) confirmed the enum order:
/// <c>zh_Hans=0, en=1, ja=2, zh_Hant=3, ko=4, th=5, id=6, de=7, fr=8, es=9, pt=10,
/// en_TH=11, en_TW=12</c>. The table authors write the design label (<c>NameDesign</c>)
/// in Chinese, so the two Chinese indices (<c>zh_Hans</c>, <c>zh_Hant</c>) are the
/// "design language": on those clients a <c>NameDesign</c> fallback is correct;
/// on every other client it would surface Chinese text and must not be used.
/// </para>
///
/// <para>
/// The lookup is read once and cached (the language cannot change without a client
/// restart, and the framework caches every other one-shot recon the same way). If
/// the type/property is not yet available the read is deferred — the cache only
/// latches once a real value is obtained, so an early call before HybridCLR has
/// loaded <c>Panda.Script</c> does not pin an incorrect default.
/// </para>
/// </summary>
internal sealed class PandaClientLanguage
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // LanguageType enum indices that use the Chinese-authored design labels.
    private const int LanguageZhHans = 0;
    private const int LanguageZhHant = 3;

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    private bool _resolved;
    private int _languageIndex = -1;

    public PandaClientLanguage(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log;
        _typeRegistry = typeRegistry;
    }

    /// <summary>
    /// True when the client UI language is the design language (Simplified or
    /// Traditional Chinese) — the language the table authors wrote
    /// <c>NameDesign</c> in. Defaults to <c>false</c> until a real value is read,
    /// so an unknown/unavailable language is treated as non-Chinese (English path).
    /// </summary>
    public bool IsDesignLanguage
    {
        get
        {
            var index = CurrentLanguageIndex;
            return index is LanguageZhHans or LanguageZhHant;
        }
    }

    /// <summary>
    /// The cached <c>LanguageType</c> index, or <c>-1</c> if it could not be read
    /// yet. Reads <c>LocalizationMgr.CurrentLanguageTypeIndex</c> on first success
    /// and caches the result.
    /// </summary>
    public int CurrentLanguageIndex
    {
        get
        {
            if (_resolved)
            {
                return _languageIndex;
            }

            var index = ReadLanguageIndex();
            if (index >= 0)
            {
                _resolved = true;
                _languageIndex = index;
                _log.Info($"[Stellar][GameData] client language index={index} designLanguage={(index is LanguageZhHans or LanguageZhHant)}");
            }
            return index;
        }
    }

    private int ReadLanguageIndex()
    {
        try
        {
            var locType = _typeRegistry.FindType("Panda.Utility.Localization.LocalizationMgr");
            if (locType is null)
            {
                return -1;
            }

            var prop = locType.GetProperty("CurrentLanguageTypeIndex", AnyStatic);
            if (prop is null || prop.GetValue(null) is not int index)
            {
                return -1;
            }

            return index;
        }
        catch
        {
            return -1;
        }
    }
}
