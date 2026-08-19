using System;
using System.Collections.Generic;
using System.Text.Json;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// The single localization engine: a per-namespace catalog registry plus the active-language
/// state, resolution (active → English → key literal), <c>string.Format</c> support, the
/// persisted <c>localization.language</c> setting, and a <see cref="LanguageChanged"/> event.
/// Namespaces are plugin GUIDs (and <c>BootstrapPlugin.PluginGuid</c>, i.e. <c>"stellar.framework"</c>,
/// for the framework's own strings); each plugin reads only its own namespace through a
/// <see cref="PluginLocalization"/> façade.
/// Pure managed — no Unity/BepInEx.
/// </summary>
internal sealed partial class LocalizationEngine : ILocalizationControl
{
    private const string LanguageKey = "language";
    private const string Follow = "follow";
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal) { "en", "ja", "th", "id", "fil" };

    // ns → (langCode → (key → value))
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _catalogs = new(StringComparer.Ordinal);
    private readonly IConfigSection _settings;
    private readonly IClientLanguageProbe _probe;
    private readonly IPluginLog _log;
    private string _setting;

    public LocalizationEngine(IConfigSection settings, IClientLanguageProbe probe, IPluginLog log)
    {
        _settings = settings;
        _probe = probe;
        _log = log;
        var stored = settings.Get(LanguageKey, Follow) ?? Follow;
        _setting = IsValidSetting(stored) ? stored : Follow;
        _log.Info($"[Stellar][i18n] localization setting='{_setting}'");
    }

    /// <summary>Raised after the active language changes (live switch).</summary>
    public event Action? LanguageChanged;

    /// <summary>Raw persisted setting: <c>"follow"</c> or a supported code.</summary>
    public string LanguageSetting => _setting;

    /// <summary>Resolved active language — always one of <c>"en"/"ja"/"th"/"id"</c>.</summary>
    public string ActiveLanguage => _setting == Follow ? Normalize(_probe.SupportedLanguage) : _setting;

    /// <summary>Parse and store one catalog under <paramref name="ns"/>/<paramref name="langCode"/>.</summary>
    public void RegisterCatalog(string ns, string langCode, string json)
    {
        if (!_catalogs.TryGetValue(ns, out var byLang))
            _catalogs[ns] = byLang = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        byLang[langCode] = Parse(json);
    }

    /// <summary>Resolve <paramref name="key"/> in <paramref name="ns"/>: active → English → the key literal.</summary>
    public string Resolve(string ns, string key) => TryResolve(ns, key, out var v) ? v : key;

    /// <summary><see cref="Resolve"/> then <c>string.Format</c>; a missing key returns the key literal, unformatted.</summary>
    public string ResolveFormat(string ns, string key, object[] args)
        => TryResolve(ns, key, out var t) ? SafeFormat(t, args) : key;

    /// <summary>Set the language setting (<c>"follow"</c> or a supported code); persists and fires
    /// <see cref="LanguageChanged"/> when the active language actually changes.</summary>
    public void SetLanguageSetting(string setting)
    {
        if (!IsValidSetting(setting)) { _log.Warning($"[Stellar][i18n] ignored invalid language '{setting}'"); return; }
        if (_setting == setting) return;
        var oldActive = ActiveLanguage;
        _setting = setting;
        _settings.Set(LanguageKey, setting);
        _settings.Save();
        _log.Info($"[Stellar][i18n] language setting → '{setting}' (active={ActiveLanguage})");
        if (ActiveLanguage != oldActive) LanguageChanged?.Invoke();
    }

    private bool TryResolve(string ns, string key, out string value)
    {
        if (_catalogs.TryGetValue(ns, out var byLang))
        {
            if (byLang.TryGetValue(ActiveLanguage, out var active) && active.TryGetValue(key, out value!)) return true;
            if (byLang.TryGetValue("en", out var en) && en.TryGetValue(key, out value!)) return true;
        }
        value = key;
        return false;
    }

    private static string SafeFormat(string template, object[] args)
    {
        try { return string.Format(template, args); }
        catch (FormatException) { return template; }
    }

    private static bool IsValidSetting(string s) => s == Follow || Supported.Contains(s);

    private static string Normalize(string code) => Supported.Contains(code) ? code : "en";

    private static Dictionary<string, string> Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }
}
