using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Owns the user-selected <see cref="ThemePreset"/> and the global font scale.
/// Persists both to the framework's <c>theme</c> config section. Fires
/// <see cref="ActiveChanged"/> on change; ThemeRenderer subscribes and rebuilds
/// its cached styles + textures.
/// </summary>
internal sealed class NamedThemeService : INamedTheme, IChromeStyle
{
    private const string PresetKey    = "preset";
    private const string FontScaleKey = "fontscale";
    private const string CustomNameKey = "customName";
    private const string CustomBaseKey = "customBase";
    private const string ButtonStyleKey    = "buttonStyle";
    private const string ScrollbarStyleKey = "scrollbarStyle";
    private const float  MinFontScale = 0.8f;
    private const float  MaxFontScale = 1.4f;

    private readonly IConfigSection _config;
    private readonly IPluginLog _log;
    private ThemePreset _active;
    private float _fontScale;
    private string? _activeCustomName;
    private MenuButtonStyle _buttonStyle;
    private MenuScrollbarStyle _scrollbarStyle;

    public NamedThemeService(IConfigSection config, IPluginLog log)
    {
        _config = config;
        _log = log;
        _active = ResolveInitialPreset();
        _fontScale = ClampScale(_config.Get(FontScaleKey, 1.0f));
        _activeCustomName = _config.Get<string?>(CustomNameKey, null);
        if (!string.IsNullOrEmpty(_activeCustomName))
            _active = ParsePreset(_config.Get(CustomBaseKey, "Default") ?? "Default");
        _buttonStyle    = ParseEnum(_config.Get(ButtonStyleKey, nameof(MenuButtonStyle.Outline)), MenuButtonStyle.Outline);
        _scrollbarStyle = ParseEnum(_config.Get(ScrollbarStyleKey, nameof(MenuScrollbarStyle.ThumbOnly)), MenuScrollbarStyle.ThumbOnly);
        _log.Info($"[NamedTheme] loaded preset={_active} fontscale={_fontScale:0.00} button={_buttonStyle} scrollbar={_scrollbarStyle}");
    }

    /// <summary>
    /// Resolve the initial preset with env-var override precedence:
    /// <c>STELLAR_THEME=&lt;Default|Dark|Light|Crimson&gt;</c> overrides the
    /// persisted preset for this session only. Visual capture scenarios use
    /// this to pre-select theme without driving the Settings UI. Read once at
    /// boot — changing the env var mid-run has no effect; restart the game to
    /// switch. The override does not persist: if the user changes preset
    /// in-game via Settings, that overrides for the rest of the session as
    /// usual, and only the explicit Settings change is written back to config.
    /// </summary>
    private ThemePreset ResolveInitialPreset()
    {
        var envTheme = Environment.GetEnvironmentVariable("STELLAR_THEME");
        if (!string.IsNullOrEmpty(envTheme)
            && Enum.TryParse<ThemePreset>(envTheme, ignoreCase: true, out var envPreset))
        {
            _log.Info($"[NamedTheme] STELLAR_THEME env var → {envPreset} (overrides persisted preset)");
            return envPreset;
        }
        return ParsePreset(_config.Get(PresetKey, "Default") ?? "Default");
    }

    public ThemePreset Active    => _active;
    // Live (un-persisted) font scale during a slider DRAG. The getter prefers it so renderers (via the
    // FontScaleProvider) resize live; it's cleared + the final value persisted on mouse-release (SetFontScale).
    private float? _fontScalePreview;
    public float       FontScale => _fontScalePreview ?? _fontScale;
    public event Action? ActiveChanged;

    public string? ActiveCustomName => _activeCustomName;

    // Phase 9b.5 — the colour editor calls this after a custom-theme override
    // edit so renderers that bake textures from theme colours (ThemeRenderer)
    // rebuild. Reuses the existing ActiveChanged path; no preset/persistence
    // change. The editor debounces calls to drag-release so a slider drag
    // triggers one rebuild, not one per frame.
    public void NotifyColorsChanged() => ActiveChanged?.Invoke();

    public void SetActiveCustom(string name, ThemePreset basePreset)
    {
        _activeCustomName = name;
        _active = basePreset;
        _config.Set(CustomNameKey, name);
        _config.Set(CustomBaseKey, basePreset.ToString());
        _config.Save();
        _log.Info($"[NamedTheme] active custom theme → {name} (base {basePreset})");
        ActiveChanged?.Invoke();
    }

    public void SetActive(ThemePreset preset)
    {
        if (_active == preset && _activeCustomName is null) return;
        _active = preset;
        _activeCustomName = null;
        _config.Set(PresetKey, preset.ToString());
        _config.Set(CustomNameKey, "");
        _config.Save();
        _log.Info($"[NamedTheme] active preset → {preset}");
        ActiveChanged?.Invoke();
    }

    // Live preview during a slider drag — applies via ActiveChanged so windows re-skin in real time, but does
    // NOT write config. Persisted once on mouse-release via SetFontScale. (Same coalescing intent as the colour
    // editor's drag handling; the Font.textureRebuilt → RefreshFontTexture hook keeps the live resize garble-free.)
    private const float PreviewStep = 0.02f;   // quantise drag preview so ActiveChanged (rebake) fires far less

    public void SetFontScalePreview(float scale)
    {
        // Quantise to 0.02 so a continuous drag doesn't fire a rebake every frame (each ActiveChanged rebakes
        // the chrome sprites). The EXACT slider value is still persisted on release via SetFontScale, so this
        // only coarsens the live preview's step, not the final result. (A reskin-only channel that skips the
        // sprite rebake entirely is the cleaner long-term fix — tracked in the handoff.)
        var clamped = ClampScale((float)(Math.Round(scale / PreviewStep) * PreviewStep));
        if (_fontScalePreview is { } p && Math.Abs(clamped - p) < 0.0001f) return;
        _fontScalePreview = clamped;
        ActiveChanged?.Invoke();
    }

    public void SetFontScale(float scale)
    {
        var clamped = ClampScale(scale);
        _fontScalePreview = null;   // release: drop the live preview, the persisted value takes over
        if (Math.Abs(clamped - _fontScale) < 0.001f) { ActiveChanged?.Invoke(); return; }
        _fontScale = clamped;
        _config.Set(FontScaleKey, clamped);
        _config.Save();
        _log.Info($"[NamedTheme] font scale → {clamped:0.00}");
        ActiveChanged?.Invoke();
    }

    private static float ClampScale(float v) => Math.Clamp(v, MinFontScale, MaxFontScale);

    private static ThemePreset ParsePreset(string s)
        => Enum.TryParse<ThemePreset>(s, ignoreCase: true, out var v) ? v : ThemePreset.Default;

    private static TEnum ParseEnum<TEnum>(string? s, TEnum fallback) where TEnum : struct
        => Enum.TryParse<TEnum>(s, ignoreCase: true, out var v) ? v : fallback;

    // ---- IChromeStyle ----
    public MenuButtonStyle    ButtonStyle    => _buttonStyle;
    public MenuScrollbarStyle ScrollbarStyle => _scrollbarStyle;

    public void SetButtonStyle(MenuButtonStyle style)
    {
        if (_buttonStyle == style) return;
        _buttonStyle = style;
        _config.Set(ButtonStyleKey, style.ToString());
        _config.Save();
        _log.Info($"[NamedTheme] button style → {style}");
        ActiveChanged?.Invoke();
    }

    public void SetScrollbarStyle(MenuScrollbarStyle style)
    {
        if (_scrollbarStyle == style) return;
        _scrollbarStyle = style;
        _config.Set(ScrollbarStyleKey, style.ToString());
        _config.Save();
        _log.Info($"[NamedTheme] scrollbar style → {style}");
        ActiveChanged?.Invoke();
    }

    // Per-preset window-chrome opacity. Keyed by the ACTIVE preset (a custom theme inherits its base
    // preset's value). Sensible defaults: Default stays translucent/frosted; Dark/Light read near-opaque
    // over bright in-world scenes; Crimson sits between. The user can override per preset via the slider.
    public float WindowOpacity => ClampOpacity(_config.Get(OpacityKey(_active), DefaultOpacity(_active)));

    public void SetWindowOpacity(float opacity)
    {
        var clamped = ClampOpacity(opacity);
        if (Math.Abs(clamped - WindowOpacity) < 0.005f) return;
        _config.Set(OpacityKey(_active), clamped);
        _config.Save();
        // Deliberately NO ActiveChanged: the uGUI window reads WindowOpacity live (frame Image alpha, polled),
        // so opacity updates in real time without a canvas rebuild/flicker. (IMGUI chrome doesn't use it.)
    }

    private static string OpacityKey(ThemePreset p) => $"windowOpacity.{p}";
    private static float ClampOpacity(float v) => Math.Clamp(v, 0.3f, 1f);
    private static float DefaultOpacity(ThemePreset p) => p switch
    {
        ThemePreset.Default => 0.62f,   // translucent frosted glass (the design intent for Default)
        ThemePreset.Crimson => 0.90f,
        _                   => 0.96f,   // Dark / Light: effectively opaque, legibility-first
    };
}
