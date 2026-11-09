using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → General (first rail tab, 2026-11-09): framework-wide settings that are not about looks. Today one
/// "Language" panel — the round Pill dropdown filling the row + its "?" — moved here from Themes. Header status = the
/// current language setting ("Follow game client" / "English" …). The catalog keys keep their old <c>themes.*</c>
/// names (same wording, so the translations carry over unchanged).
/// </summary>
internal sealed class GeneralPanel
{
    // Language dropdown: index 0 = "follow" (the only localized option); indices 1..N = UiLanguages, each shown
    // in its own script in every locale.
    private static readonly string[] LangCodes =
        System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Prepend(UiLanguages.Codes, "follow"));

    private readonly ILocalizationControl _loc;   // language setting (the dropdown)
    private readonly ILocalization _text;         // framework text lookup (labels)
    private SettingsKit _k = null!;
    // Options cached + rebuilt only when the active language changes (the "follow" label localizes), so the
    // per-frame dropdown poll doesn't allocate a fresh array.
    private string[]? _langOptCache;
    private string? _langOptLang;

    public GeneralPanel(ILocalizationControl loc, ILocalization text)
    {
        _loc = loc;
        _text = text;
    }

    public HudElement Describe(SettingsKit kit)
    {
        _k = kit;
        return _k.Panel("themes.language", null,
            SettingsKit.Row(
                SettingsKit.Dropdown(LangIndex, () => LangOptions, SetLanguage,
                    SettingsHub.MainWidth - 20f - StellarHelpPopover.ButtonSize - 8f - 1f),
                _k.Help("general.language", "themes.language", "help.themes.language", "tab.general")));
    }

    /// <summary>Header status: the current language setting ("Follow game client" / "English" …).</summary>
    public string Status(SettingsKit k) => LangOptions[LangIndex()];

    private System.Collections.Generic.IReadOnlyList<string> LangOptions
    {
        get
        {
            if (_langOptCache == null || _langOptLang != _text.Language)
            {
                _langOptLang = _text.Language;
                var opts = new string[UiLanguages.NativeNames.Count + 1];
                opts[0] = _text.T("themes.language.follow");
                for (var n = 0; n < UiLanguages.NativeNames.Count; n++) opts[n + 1] = UiLanguages.NativeNames[n];
                _langOptCache = opts;
            }
            return _langOptCache;
        }
    }

    private int LangIndex()
    {
        var i = System.Array.IndexOf(LangCodes, _loc.LanguageSetting);
        return i < 0 ? 0 : i;
    }

    private void SetLanguage(int i)
    {
        if (i < 0 || i >= LangCodes.Length) return;
        _loc.SetLanguageSetting(LangCodes[i]);
        _k.Done(_text.TFormat("themes.fb.language", LangOptions[i]));
    }
}
