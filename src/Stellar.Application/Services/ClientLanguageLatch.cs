using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// The single owner of "which language is the game client in". Latches the game's <c>LanguageType</c> index only
/// when the value is TRUSTWORTHY, and reports "unknown" (<see cref="Unknown"/>, served as English) until then.
///
/// <para>
/// Why a latch with a readiness rule: the game's <c>LocalizationMgr.CurrentLanguageTypeIndex</c> is a static whose
/// backing field defaults to <c>0</c> (<c>zh_Hans</c>) until the game's localization manager initialises — which
/// happens AFTER the framework wires its UI. The old reader latched the first value ≥ 0, so the framework's
/// wiring-time read pinned <c>zh_Hans</c> for the whole session (measured: <c>index=0</c> at boot on an en_TH and
/// a ja client) — every <c>follow</c> user got English and the Chinese design-label fallback was armed.
/// </para>
///
/// <para>
/// Rule: a NON-ZERO read is authoritative whenever it is seen (only the game writes a non-default value), so
/// <see cref="TryLatchNonDefault"/> latches it. A ZERO read is latched only on a game signal that the language is
/// set — the game's own language setter running, or the client reaching character select
/// (<see cref="OnLanguageSet"/>) — so a genuine Simplified-Chinese client still latches <c>0</c>.
/// Reads of <see cref="CurrentLanguageIndex"/> / <see cref="SupportedLanguage"/> never touch the game (hot path:
/// every <c>T()</c> call) and never latch.
/// </para>
///
/// <para>
/// <see cref="Changed"/> is NOT raised inside a signal (the signals run inside the game's own language setter, a Harmony
/// postfix on an unverified thread): a latch only records the index and sets a pending flag, and the framework tick calls
/// <see cref="DrainPendingChange"/> on the main thread, which raises it once. Event-driven — the tick only checks a flag.
/// </para>
/// </summary>
internal sealed partial class ClientLanguageLatch : IClientLanguageProbe, IClientLanguageInfo
{
    /// <summary>The index reported while the client language is not yet known.</summary>
    public const int Unknown = -1;

    // LanguageType enum indices that use the Chinese-authored design labels (NameDesign).
    private const int LanguageZhHans = 0;
    private const int LanguageZhHant = 3;

    private readonly IClientLanguageReader _reader;
    private readonly IPluginLog _log;
    private volatile int _index = Unknown;
    private volatile bool _pendingChange;

    public ClientLanguageLatch(IClientLanguageReader reader, IPluginLog log)
    {
        _reader = reader;
        _log = log;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>The latched <c>LanguageType</c> index, or <see cref="Unknown"/> until the game has set it.</summary>
    public int CurrentLanguageIndex
    {
        get
        {
            if (_index == Unknown) NoteProvisionalRead();
            return _index;
        }
    }

    /// <summary>
    /// True when the KNOWN client language is the design language (Simplified/Traditional Chinese) — the language
    /// the table authors wrote <c>NameDesign</c> in. False while unknown: never decided from the uninitialised 0.
    /// </summary>
    public bool IsDesignLanguage => _index is LanguageZhHans or LanguageZhHant;

    /// <inheritdoc />
    public string SupportedLanguage => CurrentLanguageIndex switch
    {
        1 => "en",
        2 => "ja",
        4 => "ko",
        5 => "th",
        6 => "id",
        _ => "en",   // unknown, Chinese, European, en_TH/en_TW: Stellar ships no other catalog
    };

    /// <summary>
    /// Opportunistic read (e.g. right after the framework hooks the game's language setter, in case the game set it
    /// first): latches a non-default value; a <c>0</c> is left unknown because it may be the uninitialised default.
    /// </summary>
    public void TryLatchNonDefault(string source) => Latch(_reader.ReadLanguageIndex(), allowDefault: false, source);

    /// <summary>
    /// Game signal that its language IS set (its language setter ran, or the client reached character select):
    /// latches the current value, <c>0</c> included; when it differs, <see cref="Changed"/> is queued for the next
    /// <see cref="DrainPendingChange"/>.
    /// </summary>
    public void OnLanguageSet(string source) => Latch(_reader.ReadLanguageIndex(), allowDefault: true, source);

    private void Latch(int index, bool allowDefault, string source)
    {
        NoteSignal(index, allowDefault, source);
        if (index < 0 || (index == LanguageZhHans && !allowDefault) || index == _index) return;
        var first = _index == Unknown;
        _index = index;
        _log.Info($"[Stellar][GameData] client language index={index} designLanguage={IsDesignLanguage} source={source}{(first ? "" : " (changed)")}");
        _pendingChange = true;
    }

    /// <summary>
    /// Called from the framework tick (main thread, every phase): raises <see cref="Changed"/> once if a signal latched a
    /// new value since the last drain. A flag check otherwise — no game read.
    /// </summary>
    public void DrainPendingChange()
    {
        if (!_pendingChange) return;
        _pendingChange = false;
        NoteDrain();
        Changed?.Invoke();
    }
}
