using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Nested <c>PresetColorsView</c> — extracted from <c>ThemeRenderer.cs</c>
/// during Phase 9b to keep the parent file under the 500-LoC guardrail after
/// the HUD + menu colour facets pushed the total to 525. Behaviour is
/// unchanged; this is a pure partial split.
/// </summary>
internal sealed partial class ThemeRenderer
{
    /// <summary>
    /// Live view of <see cref="ThemePresets.Tables"/>[<c>namedTheme.Active</c>].
    /// Each property dereferences the active preset's palette on every call so
    /// the renderer's cached textures (built at <see cref="Initialise"/>) reflect
    /// the user's current selection once styles are rebuilt after
    /// <see cref="INamedTheme.ActiveChanged"/>.
    /// </summary>
    private sealed class PresetColorsView : IThemeColors
    {
        // Reverse of FrameworkColorRegistration.EditableTokens: token index →
        // slot key. Editable tokens resolve through the registry (so custom-theme
        // overrides apply); everything else reads the static preset tables.
        private static readonly Dictionary<int, string> EditableByIndex = BuildReverse();

        private readonly INamedTheme _theme;
        private readonly IColorResolver _resolver;
        public PresetColorsView(INamedTheme theme, IColorResolver resolver)
        {
            _theme = theme;
            _resolver = resolver;
        }

        private static Dictionary<int, string> BuildReverse()
        {
            var m = new Dictionary<int, string>();
            foreach (var (key, idx) in FrameworkColorRegistration.EditableTokens) m[idx] = key;
            return m;
        }

        private ColorRgba Get(int index)
        {
            if (EditableByIndex.TryGetValue(index, out var key)) return _resolver.Resolve(key);
            var preset = _theme.Active;
            if (ThemePresets.Tables.TryGetValue(preset, out var table)) return table[index];
            return ThemePresets.Tables[ThemePreset.Default][index];
        }

        private ColorRgba Get(int index, ColorRgba fallback)
        {
            if (EditableByIndex.TryGetValue(index, out var key)) return _resolver.Resolve(key);
            var preset = _theme.Active;
            if (ThemePresets.Tables.TryGetValue(preset, out var table) && index < table.Length)
                return table[index];
            return fallback;
        }

        public ColorRgba Accent      => Get(ThemePresets.Accent);
        public ColorRgba Gold        => Get(ThemePresets.Gold);
        public ColorRgba HpFill      => Get(ThemePresets.HpFill);
        public ColorRgba MpFill      => Get(ThemePresets.MpFill);
        public ColorRgba Stamina     => Get(ThemePresets.Stamina);
        public ColorRgba TextPrimary => Get(ThemePresets.TextPrimary);
        public ColorRgba TextMuted   => Get(ThemePresets.TextMuted);
        public ColorRgba Warning     => Get(ThemePresets.Warning);

        // Phase 9b — HUD + menu facets. Use the fallback overload so that
        // before Task 3 extends the Tables[] arrays, accessing these indices
        // returns the Default-preset values from ThemeColors.cs without
        // OutOfRangeException.
        private static readonly ThemeColors _defaultsFallback = new();

        public ColorRgba HudText        => Get(ThemePresets.HudText,        _defaultsFallback.HudText);
        public ColorRgba HudTextShadow  => Get(ThemePresets.HudTextShadow,  _defaultsFallback.HudTextShadow);
        public ColorRgba HudAccent      => Get(ThemePresets.HudAccent,      _defaultsFallback.HudAccent);
        public ColorRgba HudBarBg       => Get(ThemePresets.HudBarBg,       _defaultsFallback.HudBarBg);
        public ColorRgba HudPillBg      => Get(ThemePresets.HudPillBg,      _defaultsFallback.HudPillBg);
        public ColorRgba MenuBackground => Get(ThemePresets.MenuBackground, _defaultsFallback.MenuBackground);
        public ColorRgba MenuText       => Get(ThemePresets.MenuText,       _defaultsFallback.MenuText);
        public ColorRgba MenuMuted      => Get(ThemePresets.MenuMuted,      _defaultsFallback.MenuMuted);
        public ColorRgba MenuAccent     => Get(ThemePresets.MenuAccent,     _defaultsFallback.MenuAccent);
        public ColorRgba MenuBorder     => Get(ThemePresets.MenuBorder,     _defaultsFallback.MenuBorder);
    }
}
