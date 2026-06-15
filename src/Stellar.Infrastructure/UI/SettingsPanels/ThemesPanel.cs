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

    private readonly INamedTheme _namedTheme;
    private readonly IChromeStyle _chromeStyle;
    private readonly ITheme _theme;
    private readonly ThemeEditorBody _editor;
    // Pending Font Scale while dragging — drives the knob + the "x" label. The value is ALSO applied LIVE during
    // the drag (ApplyFontScalePreview → NamedThemeService.SetFontScalePreview, an un-persisted ActiveChanged that
    // re-skins windows in place) and persisted ONCE on mouse-release (PollEditorUgui → SetFontScale). The garble
    // that previously made this unsafe is mitigated by WindowRenderer's Font.textureRebuilt → RefreshFontTexture.
    private float? _pendingFontScale;

    public ThemesPanel(INamedTheme namedTheme, IChromeStyle chromeStyle, ITheme theme,
                       IThemeOverrides overrides, ICustomThemeStore customThemes)
    {
        _namedTheme = namedTheme;
        _chromeStyle = chromeStyle;
        _theme = theme;
        _editor = new ThemeEditorBody(namedTheme, customThemes, overrides, theme);
    }

    /// <summary>uGUI element-tree form of <see cref="DrawBody"/> (SP1 Settings migration) — the functional
    /// controls (preset buttons, FontScale slider, Button/Scrollbar style toggles), wired to the same
    /// INamedTheme/IChromeStyle. The live PREVIEW (Pill/Bar) + the custom-colour EDITOR (colour rows +
    /// Add-override picker + HSV ColorPicker) are the remaining Themes sub-migration (need Pill/Bar in
    /// WindowBuilder + a ThemeEditorBody port) — follow-up.</summary>
    public HudElement Describe()
    {
        var items = new System.Collections.Generic.List<HudElement>();
        AddPresetAndScale(items);
        AddControls(items);
        AddPreview(items);
        // Custom-colour editor (selector / create flow / overridden-slots list + HSV picker / add-override).
        items.Add(new SeparatorElement());
        items.Add(_editor.Describe());
        return new ColumnElement(items.ToArray());
    }

    private void AddPresetAndScale(System.Collections.Generic.List<HudElement> items)
    {
        items.Add(new TextElement(() => "Preset", Emphasis: true));
        var presetRow = new System.Collections.Generic.List<HudElement>();
        foreach (var p in Presets)
        {
            var pp = p;
            presetRow.Add(new ButtonElement(
                () => _namedTheme.Active == pp && _namedTheme.ActiveCustomName == null ? $"{pp}*" : pp.ToString(),
                () => _namedTheme.SetActive(pp)));
        }
        items.Add(new RowElement(presetRow));
        items.Add(new TextElement(() => "Font Scale", Emphasis: true));
        items.Add(new RowElement(new HudElement[]
        {
            new SliderElement(() => _pendingFontScale ?? _namedTheme.FontScale, ApplyFontScalePreview, 0.8f, 1.4f),
            new TextElement(() => $"{(_pendingFontScale ?? _namedTheme.FontScale):0.00}x"),
        }));
        items.Add(new TextElement(() => "Window Opacity", Emphasis: true));
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

        items.Add(new TextElement(() => "Controls", Emphasis: true));
        items.Add(new RowElement(new HudElement[]
        {
            new TextElement(() => "Button"),
            Btn("Outline", MenuButtonStyle.Outline, () => _chromeStyle.ButtonStyle, _chromeStyle.SetButtonStyle),
            Btn("Filled", MenuButtonStyle.Filled, () => _chromeStyle.ButtonStyle, _chromeStyle.SetButtonStyle),
            Btn("Glass", MenuButtonStyle.Glass, () => _chromeStyle.ButtonStyle, _chromeStyle.SetButtonStyle),
        }));
        items.Add(new RowElement(new HudElement[]
        {
            new TextElement(() => "Scrollbar"),
            Btn("Thumb", MenuScrollbarStyle.ThumbOnly, () => _chromeStyle.ScrollbarStyle, _chromeStyle.SetScrollbarStyle),
            Btn("Track", MenuScrollbarStyle.ThinTrack, () => _chromeStyle.ScrollbarStyle, _chromeStyle.SetScrollbarStyle),
        }));
    }

    // Live preview — the pill + HP/stamina bars themed by the active colours (uGUI port of DrawPreview).
    private void AddPreview(System.Collections.Generic.List<HudElement> items)
    {
        items.Add(new TextElement(() => "Preview", Emphasis: true));
        items.Add(new RowElement(new HudElement[]
        {
            new PillElement(() => "Lv 78", () => _theme.Colors.Accent),
            new TextElement(() => "Ribery / Wind Knight"),
        }));
        items.Add(new BarElement(() => 0.78f, new ColorRgba(0.36f, 0.78f, 0.45f, 1f), () => "8240 / 10500", "HP"));
        items.Add(new BarElement(() => 0.42f, new ColorRgba(0.93f, 0.78f, 0.33f, 1f), () => "126 / 300", "Stamina"));
    }

    /// <summary>Per-frame tick for the uGUI hub (Host TickOverlayServices) — coalesces drag edits to one
    /// persist+rebake on mouse-release: the colour editor's ColorPicker AND the Font Scale / Window Opacity
    /// sliders (committing those per drag-frame rebuilds the window canvas → flicker).</summary>
    public void PollEditorUgui()
    {
        _editor.TickUgui();
        if (Input.GetMouseButton(0)) return;   // still dragging — hold the pending value (already applied live)
        if (_pendingFontScale is { } fs) { _namedTheme.SetFontScale(fs); _pendingFontScale = null; }
    }

    // Slider drag: track the pending value (knob/label) AND apply it LIVE (un-persisted) so window text resizes
    // in real time. Persisted on release by PollEditorUgui. The concrete service exposes the preview channel
    // (not on INamedTheme, which is at the interface-member cap); the runtime instance is always NamedThemeService.
    private void ApplyFontScalePreview(float v)
    {
        _pendingFontScale = v;
        (_namedTheme as NamedThemeService)?.SetFontScalePreview(v);
    }

}
