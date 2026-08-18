using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Framework-facing control over the active UI language, injected into Settings only.
/// Deliberately NOT part of <see cref="IPluginServices"/> — plugins read the language via
/// <see cref="ILocalization.Language"/> but cannot change the global setting.
/// </summary>
public interface ILocalizationControl
{
    /// <summary>Raw persisted setting: <c>"follow"</c> (game client) or <c>"en"/"ja"/"th"/"id"</c>.</summary>
    string LanguageSetting { get; }

    /// <summary>Resolved active language — always one of <c>"en"/"ja"/"th"/"id"</c>.</summary>
    string ActiveLanguage { get; }

    /// <summary>Set the language setting (<c>"follow"</c> or a supported code). Persists and raises
    /// <see cref="LanguageChanged"/> when the active language changes; an invalid value is ignored.</summary>
    void SetLanguageSetting(string setting);

    /// <summary>Raised after the active language changes (live switch).</summary>
    event Action LanguageChanged;
}
