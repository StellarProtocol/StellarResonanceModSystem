using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using UnityEngine;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Themes panel — preset selector, FontScale slider, window
/// opacity, control styles, live preview (Pill + HP/Stamina bars), and
/// the custom-colour editor (ThemeEditorBody).
/// </summary>
internal sealed class ThemesPanel
{
    private static readonly ThemePreset[] Presets =
        { ThemePreset.Default, ThemePreset.Dark, ThemePreset.Light, ThemePreset.Crimson };

    // Language dropdown: setting codes (index-aligned to the option labels). Index 0 ("follow") is the only
    // descriptive option (localized); indices 1-4 are language NAMES shown in their own script in every locale.
    private static readonly string[] LangCodes = { "follow", "en", "ja", "th", "id", "fil" };
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
                _langOptCache = new[] { _text.T("themes.language.follow"), "English", "日本語", "ไทย", "Bahasa Indonesia", "Filipino" };
            }
            return _langOptCache;
        }
    }

    private readonly INamedTheme _namedTheme;
    private readonly IChromeStyle _chromeStyle;
    private readonly ITheme _theme;
    private readonly ILocalizationControl _loc;    // language setting (the dropdown)
    private readonly ILocalization _text;          // framework text lookup (labels)
    private readonly ThemeEditorBody _editor;
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
    public ThemesPanel(INamedTheme namedTheme, ITheme theme, IThemeOverrides overrides,
                       ICustomThemeStore customThemes, ILocalizationControl loc, ILocalization text)
    {
        _namedTheme = namedTheme;
        _chromeStyle = (IChromeStyle)namedTheme;
        _theme = theme;
        _loc = loc;
        _text = text;
        _editor = new ThemeEditorBody(namedTheme, customThemes, overrides, theme, text);
    }

    /// <summary>uGUI element-tree form of <see cref="DrawBody"/> (SP1 Settings migration) — the functional
    /// controls (preset buttons, FontScale slider, Button/Scrollbar style toggles), wired to the same
    /// INamedTheme/IChromeStyle. The live PREVIEW (Pill/Bar) + the custom-colour EDITOR (colour rows +
    /// Add-override picker + HSV ColorPicker) are the remaining Themes sub-migration (need Pill/Bar in
    /// WindowBuilder + a ThemeEditorBody port) — follow-up.</summary>
    public HudElement Describe()
    {
        var items = new System.Collections.Generic.List<HudElement>();
        AddLanguage(items);
        AddPresetAndScale(items);
        AddControls(items);
        AddPreview(items);
        // Custom-colour editor (selector / create flow / overridden-slots list + HSV picker / add-override).
        items.Add(new SeparatorElement());
        items.Add(_editor.Describe());
        return new ColumnElement(items.ToArray());
    }

    // Language selector — Follow game client (default) or one of the four shipped UI languages. Persists via
    // ILocalizationControl and switches the overlay live (Func<string> labels re-poll; baked renderers flush).
    private void AddLanguage(System.Collections.Generic.List<HudElement> items)
    {
        items.Add(new TextElement(() => _text.T("themes.language"), Emphasis: true));
        items.Add(new RowElement(new HudElement[]
        {
            new DropdownElement(LangIndex, () => LangOptions, i => _loc.SetLanguageSetting(LangCodes[i]), Width: 180f),
        }));
        items.Add(new SeparatorElement());
    }

    private int LangIndex()
    {
        var i = System.Array.IndexOf(LangCodes, _loc.LanguageSetting);
        return i < 0 ? 0 : i;
    }

    private void AddPresetAndScale(System.Collections.Generic.List<HudElement> items)
    {
        items.Add(new TextElement(() => _text.T("themes.preset"), Emphasis: true));
        var presetRow = new System.Collections.Generic.List<HudElement>();
        foreach (var p in Presets)
        {
            var pp = p;
            presetRow.Add(new ButtonElement(
                () => _namedTheme.Active == pp && _namedTheme.ActiveCustomName == null ? $"{pp}*" : pp.ToString(),
                () => _namedTheme.SetActive(pp)));
        }
        items.Add(new RowElement(presetRow));
        items.Add(new TextElement(() => _text.T("themes.fontScale"), Emphasis: true));
        items.Add(new RowElement(new HudElement[]
        {
            new SliderElement(() => _pendingFontScale ?? _namedTheme.FontScale, ApplyFontScalePreview, 0.8f, 1.4f),
            new TextElement(() => $"{(_pendingFontScale ?? _namedTheme.FontScale):0.00}x"),
        }));
        items.Add(new TextElement(() => _text.T("themes.uiScale"), Emphasis: true));
        items.Add(new RowElement(new HudElement[]
        {
            new SliderElement(() => _pendingUiScale ?? (Nts?.UiScale ?? 1f), ApplyUiScalePreview, 0.75f, 1.5f),
            new TextElement(() => $"{(_pendingUiScale ?? (Nts?.UiScale ?? 1f)):0.00}x"),
        }));
        items.Add(new TextElement(() => _text.T("themes.windowOpacity"), Emphasis: true));
        items.Add(new RowElement(new HudElement[]
        {
            // Opacity is applied live (frame Image alpha) with no rebuild, so it's safe to set per drag-frame.
            new SliderElement(() => _chromeStyle.WindowOpacity, v => _chromeStyle.SetWindowOpacity(v), 0.3f, 1f),
            new TextElement(() => $"{Mathf.RoundToInt(_chromeStyle.WindowOpacity * 100f)}%"),
        }));
    }

    private void AddControls(System.Collections.Generic.List<HudElement> items)
    {
        HudElement Btn<T>(string label, T val, System.Func<T> get, System.Action<T> set) where T : System.Enum
            => new ButtonElement(() => get().Equals(val) ? label + " ✓" : label, () => set(val));

        items.Add(new TextElement(() => _text.T("themes.controls"), Emphasis: true));
        items.Add(new RowElement(new HudElement[]
        {
            new TextElement(() => _text.T("themes.button")),
            Btn("Outline", MenuButtonStyle.Outline, () => _chromeStyle.ButtonStyle, _chromeStyle.SetButtonStyle),
            Btn("Filled", MenuButtonStyle.Filled, () => _chromeStyle.ButtonStyle, _chromeStyle.SetButtonStyle),
            Btn("Glass", MenuButtonStyle.Glass, () => _chromeStyle.ButtonStyle, _chromeStyle.SetButtonStyle),
        }));
        items.Add(new RowElement(new HudElement[]
        {
            new TextElement(() => _text.T("themes.scrollbar")),
            Btn("Thumb", MenuScrollbarStyle.ThumbOnly, () => _chromeStyle.ScrollbarStyle, _chromeStyle.SetScrollbarStyle),
            Btn("Track", MenuScrollbarStyle.ThinTrack, () => _chromeStyle.ScrollbarStyle, _chromeStyle.SetScrollbarStyle),
        }));
    }

    // Live preview — the pill + HP/stamina bars themed by the active colours (uGUI port of DrawPreview).
    private void AddPreview(System.Collections.Generic.List<HudElement> items)
    {
        items.Add(new TextElement(() => _text.T("themes.preview"), Emphasis: true));
        items.Add(new RowElement(new HudElement[]
        {
            new PillElement(() => "Lv 78", () => _theme.Colors.Accent),
            new TextElement(() => "Ribery / Wind Knight"),
        }));
        items.Add(new BarElement(() => 0.78f, new ColorRgba(0.36f, 0.78f, 0.45f, 1f), () => "8240 / 10500", "HP"));
        items.Add(new BarElement(() => 0.42f, new ColorRgba(0.93f, 0.78f, 0.33f, 1f), () => "126 / 300", "Stamina"));
        // Typography sample — one element per style flag, localized, so every language's real-bold face,
        // italic, underline, and strikethrough are visible in the preview (and pinned by the visual scenario).
        items.Add(new RowElement(new HudElement[]
        {
            new TextElement(() => _text.T("themes.type.bold")) { Bold = true },
            new TextElement(() => _text.T("themes.type.italic")) { Italic = true },
            new TextElement(() => _text.T("themes.type.underline")) { Underline = true },
            new TextElement(() => _text.T("themes.type.strike")) { Strikethrough = true },
        }));
    }

    /// <summary>Per-frame tick for the uGUI hub (Host TickOverlayServices) — coalesces drag edits to one
    /// persist+rebake on mouse-release: the colour editor's ColorPicker AND the Font Scale / Window Opacity
    /// sliders (committing those per drag-frame rebuilds the window canvas → flicker).</summary>
    public void PollEditorUgui()
    {
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
