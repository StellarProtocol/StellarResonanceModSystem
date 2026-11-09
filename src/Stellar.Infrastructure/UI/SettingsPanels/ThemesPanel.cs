using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;
using UnityEngine;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Themes, two columns. Left: "Theme" (theme cards 2 per row — built-ins then custom themes, 3 rows then
/// scroll — "+ Duplicate", and the active custom theme's Edit colours / Rename / Delete), "Language", "Classic controls"
/// (button / scrollbar style of older plugin windows). Right: "Size & opacity" (font scale, UI scale, window opacity) and
/// a live "Preview" — an example window drawn with the theme tokens. Edit colours opens the colour editor in its own
/// child window (<c>ThemesPanel.Colours.cs</c> + <see cref="ThemeEditorBody"/>), whose rows open the colour picker in a
/// second, docked child window (<c>ThemesPanel.Picker.cs</c>). Partials: <c>.Cards.cs</c> (cards +
/// theme actions), <c>.Preview.cs</c>, <c>.Colours.cs</c>.
/// </summary>
internal sealed partial class ThemesPanel
{
    private static readonly ThemePreset[] Presets =
        { ThemePreset.Default, ThemePreset.Dark, ThemePreset.Light, ThemePreset.Crimson, ThemePreset.Sakura };

    // DISPLAYED name only — never the persisted value: SetActive(pp) always takes the enum itself, and
    // NamedThemeService stores the enum (name), never this localized string.
    internal static string PresetNameKey(ThemePreset p) => p switch
    {
        ThemePreset.Dark    => "themes.preset.dark",
        ThemePreset.Light   => "themes.preset.light",
        ThemePreset.Crimson => "themes.preset.crimson",
        ThemePreset.Sakura  => "themes.preset.sakura",
        _                   => "themes.preset.default",
    };

    // Language dropdown: index 0 = "follow" (the only localized option); indices 1..N = UiLanguages, each shown
    // in its own script in every locale.
    private static readonly string[] LangCodes =
        System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Prepend(UiLanguages.Codes, "follow"));
    // Options cached + rebuilt only when the active language changes (the "follow" label localizes), so the
    // per-frame dropdown poll doesn't allocate a fresh array.
    private string[]? _langOptCache;
    private string? _langOptLang;
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

    private const float ScaleLabelWidth = 100f;
    private const float ScaleValueWidth = 40f;
    private const float StyleChipWidth = 54f;

    private readonly INamedTheme _namedTheme;
    private readonly IChromeStyle _chromeStyle;
    private readonly ICustomThemeStore _store;
    private readonly ILocalizationControl _loc;    // language setting (the dropdown)
    private readonly ILocalization _text;          // framework text lookup (labels)
    private readonly ThemeEditorBody _editor;
    private SettingsKit _k = null!;
    // Pending Font Scale while dragging — drives the knob + the "x" label. The value is ALSO applied LIVE during
    // the drag (ApplyFontScalePreview → NamedThemeService.SetFontScalePreview, an un-persisted ActiveChanged that
    // re-skins windows in place) and persisted ONCE on mouse-release (PollEditorUgui → SetFontScale). The garble
    // that previously made this unsafe is mitigated by WindowRenderer's Font.textureRebuilt → RefreshFontTexture.
    private float? _pendingFontScale;
    // Pending UI Scale while dragging — drives the knob + "x" label only; the canvas is NOT rescaled during the
    // drag. The final value is applied + persisted ONCE on mouse-release (PollEditorUgui → SetUiScale).
    private float? _pendingUiScale;
    // UiScale getter/setter are concrete-only (not on INamedTheme), so reach them via the runtime instance.
    private NamedThemeService? Nts => _namedTheme as NamedThemeService;

    // chromeStyle is the SAME object as namedTheme (NamedThemeService implements both) — derived here rather
    // than taken as a separate parameter so the two localization dependencies fit within the ctor-dep cap.
    // theme is no longer read (the house kit carries the live colours); the parameter stays for the wiring.
    public ThemesPanel(INamedTheme namedTheme, ITheme theme, IThemeOverrides overrides,
                       ICustomThemeStore customThemes, ILocalizationControl loc, ILocalization text)
    {
        _namedTheme = namedTheme;
        _chromeStyle = (IChromeStyle)namedTheme;
        _store = customThemes;
        _loc = loc;
        _text = text;
        // ThemeEditorBody owns relabeling the framework's editable colour-slot labels into the active
        // language (lazily, render-driven — see its ColorReg doc) since it is the class that actually reads
        // them (SlotAt/LabelAt).
        _editor = new ThemeEditorBody(namedTheme, customThemes, overrides, theme, text);
    }

    public HudElement Describe(SettingsKit kit)
    {
        _k = kit;
        kit.TabChanged += ResetTransient;
        // The left column ends in a VerticalFill so it stretches to the tab height with its panels at the top (a Row
        // centres a non-flexible child); the right column's Preview panel is the flexible one.
        return new RowElement(new HudElement[]
        {
            new CellElement(StellarStyle.TopColumn(BuildThemePanel(), BuildLanguagePanel(), BuildControlsPanel()),
                Width: LayoutPanel.ColumnWidth),
            new CellElement(new ColumnElement(new HudElement[]
            {
                BuildScalePanel(), BuildPreviewPanel(),
            }, Gap: 8f), Width: LayoutPanel.ColumnWidth),
        }, Gap: 8f);
    }

    /// <summary>Header status: "Sakura · 90% opacity · Follow game client".</summary>
    public string Status(SettingsKit k)
        => _text.TFormat("themes.status", ActiveThemeName(), Mathf.RoundToInt(_chromeStyle.WindowOpacity * 100f),
            LangOptions[LangIndex()]);

    private string ActiveThemeName() => _namedTheme.ActiveCustomName ?? _text.T(PresetNameKey(_namedTheme.Active));

    private HudElement BuildLanguagePanel()
        => _k.Panel("themes.language", null,
            SettingsKit.Row(
                SettingsKit.Dropdown(LangIndex, () => LangOptions, SetLanguage,
                    LayoutPanel.ColumnWidth - 20f - StellarHelpPopover.ButtonSize - 8f - 1f),
                _k.Help("themes.language", "themes.language", "help.themes.language", "tab.themes")));

    private void SetLanguage(int i)
    {
        if (i < 0 || i >= LangCodes.Length) return;
        _loc.SetLanguageSetting(LangCodes[i]);
        _k.Done(_text.TFormat("themes.fb.language", LangOptions[i]));
    }

    private HudElement BuildControlsPanel()
    {
        var s = _k.S;
        HudElement Chip<T>(string key, T value, System.Func<T> get, System.Action<T> set) where T : System.Enum
            => s.Chip(_k.T(key), () => get().Equals(value), () => set(value), StyleChipWidth, s.Accent);
        HudElement Chips(params HudElement[] c) => new RowElement(c, Gap: 6f);
        return _k.Panel("themes.classic", _k.T("themes.classic.hint"),
            SettingsKit.Row(_k.LineLabel(_k.T("themes.buttons.label")),
                Chips(Chip("themes.style.outline", MenuButtonStyle.Outline, () => _chromeStyle.ButtonStyle, _chromeStyle.SetButtonStyle),
                      Chip("themes.style.filled", MenuButtonStyle.Filled, () => _chromeStyle.ButtonStyle, _chromeStyle.SetButtonStyle),
                      Chip("themes.style.glass", MenuButtonStyle.Glass, () => _chromeStyle.ButtonStyle, _chromeStyle.SetButtonStyle)),
                _k.Help("themes.buttons", "themes.buttons.label", "help.themes.buttons", "tab.themes")),
            SettingsKit.Row(_k.LineLabel(_k.T("themes.scrollbar")),
                Chips(Chip("themes.style.thumb", MenuScrollbarStyle.ThumbOnly, () => _chromeStyle.ScrollbarStyle, _chromeStyle.SetScrollbarStyle),
                      Chip("themes.style.track", MenuScrollbarStyle.ThinTrack, () => _chromeStyle.ScrollbarStyle, _chromeStyle.SetScrollbarStyle)),
                _k.Help("themes.scrollbar", "themes.scrollbar", "help.themes.scrollbar", "tab.themes")));
    }

    private HudElement BuildScalePanel()
    {
        HudElement ScaleRow(string labelKey, HudElement slider, System.Func<string> value, string helpKey)
            => SettingsKit.Row(
                new TextElement(_k.T(labelKey), Color: () => _k.S.Text(), Width: ScaleLabelWidth, NoWrap: true) { Ellipsize = true },
                new CellElement(slider, Weight: 1f),
                _k.Value(value, ScaleValueWidth),
                _k.Help(helpKey, labelKey, "help." + helpKey, "tab.themes"));
        return _k.Panel("themes.size", null,
            ScaleRow("themes.scale.font",
                SettingsKit.Slider(() => _pendingFontScale ?? _namedTheme.FontScale, ApplyFontScalePreview, 0.8f, 1.4f),
                () => $"{(_pendingFontScale ?? _namedTheme.FontScale):0.00}x", "themes.font"),
            ScaleRow("themes.scale.ui",
                SettingsKit.Slider(() => _pendingUiScale ?? (Nts?.UiScale ?? 1f), ApplyUiScalePreview, 0.75f, 1.5f),
                () => $"{(_pendingUiScale ?? (Nts?.UiScale ?? 1f)):0.00}x", "themes.ui"),
            // Opacity is applied live (frame Image alpha + every self-drawn fill) with no rebuild, so it's safe per drag-frame.
            ScaleRow("themes.scale.opacity",
                SettingsKit.Slider(() => _chromeStyle.WindowOpacity, v => _chromeStyle.SetWindowOpacity(v), 0.3f, 1f),
                () => $"{Mathf.RoundToInt(_chromeStyle.WindowOpacity * 100f)}%", "themes.opacity"));
    }

    private int LangIndex()
    {
        var i = System.Array.IndexOf(LangCodes, _loc.LanguageSetting);
        return i < 0 ? 0 : i;
    }

    /// <summary>Per-frame tick for the uGUI hub (Host TickOverlayServices) — coalesces drag edits to one
    /// persist+rebake on mouse-release: the colour editor's ColorPicker AND the Font Scale / UI Scale sliders
    /// (committing those per drag-frame rebuilds the window canvas → flicker).</summary>
    public void PollEditorUgui()
    {
        // NOTE: this runs every framework tick regardless of whether the Settings window is even visible
        // (Wiring.ServiceTick.cs calls it unconditionally) — do NOT add anything here that reads the active
        // language/client-language probe (see ThemeEditorBody.cs's ColorReg doc for why: that read must stay
        // gated behind the window actually being shown).
        _editor.TickUgui();
        if (Input.GetMouseButton(0)) return;   // still dragging — hold the pending value (already applied live)
        if (_pendingFontScale is { } fs) { _namedTheme.SetFontScale(fs); _pendingFontScale = null; }
        if (_pendingUiScale is { } us) { Nts?.SetUiScale(us); _pendingUiScale = null; }
    }

    // Slider drag: track the pending value (knob/label) AND apply it LIVE (un-persisted) so window text resizes
    // in real time. Persisted on release by PollEditorUgui. The concrete service exposes the preview channel
    // (not on INamedTheme, which is at the interface-member cap); the runtime instance is always NamedThemeService.
    private void ApplyFontScalePreview(float v)
    {
        _pendingFontScale = v;
        (_namedTheme as NamedThemeService)?.SetFontScalePreview(v);
    }

    // UI Scale applies on RELEASE (PollEditorUgui → SetUiScale). During the drag we only track the pending
    // value so the knob + label move; the canvas is not rescaled until release (avoids per-step repacks).
    private void ApplyUiScalePreview(float v) => _pendingUiScale = v;
}
