using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;
using UnityEngine;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Themes, two columns. Left: "Theme" (theme cards 2 per row — built-ins then custom themes, 4 rows then
/// scroll — "+ Duplicate", and the active custom theme's Edit colours / Rename / Delete), "Classic controls"
/// (button / scrollbar style of older plugin windows). The Theme panel ends in "Window opacity": NamedThemeService stores
/// it PER THEME (<c>windowOpacity.&lt;preset&gt;</c>), so it lives with the theme; Font scale + UI scale are global and
/// moved to Settings › General (2026-11-09). Right: a live "Preview" filling the column — an example window drawn with
/// the theme tokens. Edit colours opens the colour editor in its own
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

    private const float OpacityLabelWidth = 100f;
    private const float OpacityValueWidth = 40f;
    private const float StyleChipWidth = 54f;

    private readonly INamedTheme _namedTheme;
    private readonly IChromeStyle _chromeStyle;
    private readonly ICustomThemeStore _store;
    private readonly ILocalization _text;          // framework text lookup (labels)
    private readonly ThemeEditorBody _editor;
    // Card swatches of INACTIVE custom themes: their own stored colours over their base preset (null in a test
    // double that only implements IThemeOverrides → the preset table, the old look).
    private readonly IThemeSwatchResolver? _swatches;
    private SettingsKit _k = null!;

    // chromeStyle is the SAME object as namedTheme (NamedThemeService implements both) — derived here rather
    // than taken as a separate parameter so the two localization dependencies fit within the ctor-dep cap.
    // theme is no longer read (the house kit carries the live colours); the parameter stays for the wiring.
    // Language moved to Settings › General (GeneralPanel, 2026-11-09).
    public ThemesPanel(INamedTheme namedTheme, ITheme theme, IThemeOverrides overrides,
                       ICustomThemeStore customThemes, ILocalization text)
    {
        _namedTheme = namedTheme;
        _chromeStyle = (IChromeStyle)namedTheme;
        _store = customThemes;
        _text = text;
        _swatches = overrides as IThemeSwatchResolver;
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
            new CellElement(StellarStyle.TopColumn(BuildThemePanel(), BuildControlsPanel()),
                Width: LayoutPanel.ColumnWidth),
            new CellElement(new ColumnElement(new HudElement[] { BuildPreviewPanel() }, Gap: 8f),
                Width: LayoutPanel.ColumnWidth),
        }, Gap: 8f);
    }

    /// <summary>Header status: "Sakura · 90% opacity".</summary>
    public string Status(SettingsKit k)
        => _text.TFormat("themes.status", ActiveThemeName(), Mathf.RoundToInt(_chromeStyle.WindowOpacity * 100f));

    private string ActiveThemeName() => _namedTheme.ActiveCustomName ?? _text.T(PresetNameKey(_namedTheme.Active));

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

    // "Window opacity" under the theme actions, with a muted "Saved for this theme" sub-line (the value is per theme:
    // switching theme moves the knob). Applied live (frame Image alpha + every self-drawn fill) with no rebuild, so it is
    // safe per drag-frame — no release coalescing. Hidden while the delete confirm is open: the confirm is ~88 px taller
    // than the actions row, and with this row too the left column would outgrow the fixed 478 px tab body.
    private HudElement BuildOpacityRow()
    {
        var s = _k.S;
        return new ConditionalElement(() => !(IsCustomActive && _confirmDelete), new ColumnElement(new HudElement[]
        {
            SettingsKit.Row(
                new TextElement(_k.T("themes.scale.opacity"), Color: () => s.Text(), Width: OpacityLabelWidth, NoWrap: true)
                    { Ellipsize = true },
                new CellElement(SettingsKit.Slider(() => _chromeStyle.WindowOpacity, v => _chromeStyle.SetWindowOpacity(v), 0.3f, 1f),
                    Weight: 1f),
                _k.Value(() => $"{Mathf.RoundToInt(_chromeStyle.WindowOpacity * 100f)}%", OpacityValueWidth),
                _k.Help("themes.opacity", "themes.scale.opacity", "help.themes.opacity", "tab.themes")),
            new TextElement(_k.T("themes.opacity.perTheme"), Color: () => s.Muted(), NoWrap: true, FontSize: 11)
                { Ellipsize = true },
        }, Gap: 1f));
    }

    /// <summary>Per-frame tick for the uGUI hub (Host TickOverlayServices) — coalesces the colour editor's ColorPicker
    /// drag edits to one persist+rebake on mouse-release. (The Font / UI Scale release flush moved with those sliders to
    /// <see cref="GeneralPanel.PollScaleUgui"/>.)</summary>
    public void PollEditorUgui()
    {
        // NOTE: this runs every framework tick regardless of whether the Settings window is even visible
        // (Wiring.ServiceTick.cs calls it unconditionally) — do NOT add anything here that reads the active
        // language/client-language probe (see ThemeEditorBody.cs's ColorReg doc for why: that read must stay
        // gated behind the window actually being shown).
        _editor.TickUgui();
    }
}
