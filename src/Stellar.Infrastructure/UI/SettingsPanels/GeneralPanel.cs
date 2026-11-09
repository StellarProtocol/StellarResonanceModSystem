using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;
using UnityEngine;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → General (first rail tab, 2026-11-09): framework-wide settings that hold ONE value whatever the theme.
/// "Language" — the round Pill dropdown filling the row + its "?" — and "Display": Font scale + UI scale (moved here
/// from Themes, 2026-11-09: both are global, unlike Window opacity, which NamedThemeService stores per theme as
/// <c>windowOpacity.&lt;preset&gt;</c> and so stays on the Themes tab). Header status = "English · UI 1.00x". The
/// catalog keys keep their old <c>themes.*</c> names (same wording, so the translations carry over unchanged).
/// </summary>
internal sealed class GeneralPanel
{
    // Language dropdown: index 0 = "follow" (the only localized option); indices 1..N = UiLanguages, each shown
    // in its own script in every locale.
    private static readonly string[] LangCodes =
        System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Prepend(UiLanguages.Codes, "follow"));

    private const float ScaleLabelWidth = 100f;
    private const float ScaleValueWidth = 40f;

    private readonly ILocalizationControl _loc;   // language setting (the dropdown)
    private readonly ILocalization _text;         // framework text lookup (labels)
    private readonly INamedTheme _namedTheme;     // font scale + UI scale (global, not per theme)
    private SettingsKit _k = null!;
    // Options cached + rebuilt only when the active language changes (the "follow" label localizes), so the
    // per-frame dropdown poll doesn't allocate a fresh array.
    private string[]? _langOptCache;
    private string? _langOptLang;
    // Pending Font Scale while dragging — drives the knob + the "x" label. The value is ALSO applied LIVE during
    // the drag (ApplyFontScalePreview → NamedThemeService.SetFontScalePreview, an un-persisted ActiveChanged that
    // re-skins windows in place) and persisted ONCE on mouse-release (PollScaleUgui → SetFontScale). The garble
    // that previously made this unsafe is mitigated by WindowRenderer's Font.textureRebuilt → RefreshFontTexture.
    private float? _pendingFontScale;
    // Pending UI Scale while dragging — drives the knob + "x" label only; the canvas is NOT rescaled during the
    // drag. The final value is applied + persisted ONCE on mouse-release (PollScaleUgui → SetUiScale).
    private float? _pendingUiScale;
    // UiScale getter/setter + the font preview channel are concrete-only (INamedTheme is at its interface-member
    // cap), so reach them via the runtime instance — always NamedThemeService.
    private NamedThemeService? Nts => _namedTheme as NamedThemeService;

    public GeneralPanel(ILocalizationControl loc, ILocalization text, INamedTheme namedTheme)
    {
        _loc = loc;
        _text = text;
        _namedTheme = namedTheme;
    }

    public HudElement Describe(SettingsKit kit)
    {
        _k = kit;
        return StellarStyle.TopColumn(
            _k.Panel("themes.language", null,
                SettingsKit.Row(
                    _k.Dropdown(LangIndex, () => LangOptions, SetLanguage,
                        SettingsHub.MainWidth - 20f - StellarHelpPopover.ButtonSize - 8f - 1f),
                    _k.Help("general.language", "themes.language", "help.themes.language", "tab.general"))),
            BuildDisplayPanel());
    }

    /// <summary>Header status: the language setting + the UI scale ("English · UI 1.00x").</summary>
    public string Status(SettingsKit k) => _text.TFormat("general.status", LangOptions[LangIndex()], $"{UiScaleShown:0.00}");

    private float FontScaleShown => _pendingFontScale ?? _namedTheme.FontScale;
    private float UiScaleShown => _pendingUiScale ?? (Nts?.UiScale ?? 1f);

    // Font scale + UI scale — the same rows the Themes tab had (label, round-knob slider, "1.00x", "?"); the help keys
    // ("themes.font" / "themes.ui") are kept so their catalog texts carry over.
    private HudElement BuildDisplayPanel()
    {
        HudElement ScaleRow(string labelKey, HudElement slider, System.Func<string> value, string helpKey)
            => SettingsKit.Row(
                new TextElement(_k.T(labelKey), Color: () => _k.S.Text(), Width: ScaleLabelWidth, NoWrap: true) { Ellipsize = true },
                new CellElement(slider, Weight: 1f),
                _k.Value(value, ScaleValueWidth),
                _k.Help(helpKey, labelKey, "help." + helpKey, "tab.general"));
        return _k.Panel("general.display", null,
            ScaleRow("themes.scale.font", SettingsKit.Slider(() => FontScaleShown, ApplyFontScalePreview, 0.8f, 1.4f),
                () => $"{FontScaleShown:0.00}x", "themes.font"),
            ScaleRow("themes.scale.ui", SettingsKit.Slider(() => UiScaleShown, v => _pendingUiScale = v, 0.75f, 1.5f),
                () => $"{UiScaleShown:0.00}x", "themes.ui"));
    }

    /// <summary>Per-frame tick (Host TickOverlayServices, every framework tick whether or not the hub is shown) —
    /// coalesces a Font Scale / UI Scale drag to ONE persist on mouse-release (committing per drag-frame rebuilds the
    /// window canvas → flicker). Reads no localization (a client-language probe here would run before the hub shows).</summary>
    public void PollScaleUgui()
    {
        if (_pendingFontScale == null && _pendingUiScale == null) return;
        if (Input.GetMouseButton(0)) return;   // still dragging — hold the pending value (font is already applied live)
        if (_pendingFontScale is { } fs) { _namedTheme.SetFontScale(fs); _pendingFontScale = null; }
        if (_pendingUiScale is { } us) { Nts?.SetUiScale(us); _pendingUiScale = null; }
    }

    // Slider drag: track the pending value (knob/label) AND apply it LIVE (un-persisted) so window text resizes in
    // real time. Persisted on release by PollScaleUgui. UI Scale has no live preview: its drag only tracks the value
    // (the canvas is rescaled once, on release — avoids per-step repacks).
    private void ApplyFontScalePreview(float v)
    {
        _pendingFontScale = v;
        Nts?.SetFontScalePreview(v);
    }

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
