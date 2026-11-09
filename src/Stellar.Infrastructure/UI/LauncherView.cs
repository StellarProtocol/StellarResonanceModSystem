using System;
using System.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI;

/// <summary>What the launcher's buttons do outside the view (a parameter object: the analyzer caps constructors at
/// 6 parameters). <paramref name="OpenSettings"/> takes the Settings tab to open at — null = just open the hub,
/// "plugins" = the Plugins tab (the header's "N failed to load" link).</summary>
internal sealed record LauncherActions(Action<string?> OpenSettings, Action Close, Action ToggleLayoutEdit);

/// <summary>
/// The Stellar launcher (the side-rail "Stellar" menu) in the house style — Stellar Design Language, 2.30.0 redesign,
/// built from the approved prototype <c>_scratch/prototypes/launcher.html</c>. ONE GlassMenu frame with NO chrome
/// title bar; the body switches between the Full menu and the compact bar (vertical or horizontal) via live
/// Conditionals on the registry mode, and the content-sized window re-fits.
/// <para>Full menu (fixed 400 wide, fixed height): header (brand logo, "Stellar", live status with a red "failed to
/// load" link, compact-mode and ✕ buttons) · a Find field above 16 plugins · a panel with the Favorites / All plugins
/// tabs over a 4-column tile grid that reserves 4 rows and scrolls beyond · Settings / Edit layout pills + version ·
/// the feedback line. Compact bar: ✕ + logo, Full / rotate buttons, the favorite tiles, Settings.</para>
/// <para>Partials (500-LoC cap): this file = state, root, shared bits · <c>.Full.cs</c> the Full menu ·
/// <c>.Tabs.cs</c> tab chips + empty states · <c>.Tile.cs</c> the grid tile and its star · <c>.Compact.cs</c> the
/// compact bar. The list logic (tabs, Find, the Favorites ghost) is <see cref="LauncherMenuModel"/>.</para>
/// </summary>
/// <remarks>
/// Built ONCE; everything that changes is a live Func or a Conditional re-evaluated on each refresh. Tiles are a fixed
/// pool read by index from the model's revision-cached lists, so plugins that register after the launcher is built
/// (most do) still appear. Every fill goes through <see cref="StellarStyle.Fill"/> (window opacity) and every colour
/// is read from the live theme.
/// </remarks>
internal sealed partial class LauncherView
{
    private const float FlashSeconds = 1f;   // clicked-tile "done" wash (SDL R13)

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly LauncherRegistry _registry;
    private readonly LauncherMenuModel _model;
    private readonly LauncherActions _actions;
    private readonly ILocalization _text;
    private readonly StellarStyle _s;
    private readonly Func<int> _failedCount;
    private readonly StellarFeedbackLine _feedback;

    // R13 flash: keyed by the entry INSTANCE + time, so a re-filter / scroll keeps it on the right tile.
    private LauncherEntry? _flashEntry;
    private double _flashUntil;

    /// <param name="registry">Entries, pins, mode, orientation, tab.</param>
    /// <param name="actions">Settings / close / layout-edit callbacks.</param>
    /// <param name="text">Framework text lookup (the <c>launcher.*</c> keys).</param>
    /// <param name="style">The house-style kit over the live theme.</param>
    /// <param name="failedCount">Live number of plugins that failed to load (0 hides the header link).</param>
    public LauncherView(LauncherRegistry registry, LauncherActions actions, ILocalization text, StellarStyle style,
        Func<int> failedCount)
    {
        _registry = registry;
        _model = new LauncherMenuModel(registry);
        _actions = actions;
        _text = text;
        _s = style;
        _failedCount = failedCount;
        _feedback = new StellarFeedbackLine(style, () => T("launcher.hint.idle"));
    }

    private bool Full => _registry.Mode == LauncherMode.Full;
    private bool Minimal => _registry.Mode == LauncherMode.Minimal;

    /// <summary>Body: the Full menu or one of the two compact-bar orientations, only the active one shown.</summary>
    public HudElement Root => new ColumnElement(new HudElement[]
    {
        new ConditionalElement(() => Full, BuildFull()),
        new ConditionalElement(() => Minimal && !_registry.MinimalHorizontal, BuildCompact(horizontal: false)),
        new ConditionalElement(() => Minimal && _registry.MinimalHorizontal, BuildCompact(horizontal: true)),
    }, Gap: 0.01f);

    // ── shared helpers ───────────────────────────────────────────────────────────────────────────────────────────
    private string T(string key) => _text.T(key);

    // A translated template with arguments; a bad template (translation typo) falls back to the raw text.
    private string F(string key, params object[] args)
    {
        var template = T(key);
        try { return string.Format(template, args); }
        catch (FormatException) { return template; }
    }

    private static byte[]? Icon(string name) => LauncherIcons.Get(name);

    private static byte[]? EntryIcon(LauncherEntry? e) => e?.IconPng ?? Icon("plugins");

    private static double Now() => Clock.Elapsed.TotalSeconds;

    private bool Flashing(LauncherEntry? e) => e != null && ReferenceEquals(e, _flashEntry) && Now() < _flashUntil;

    private void SwitchMode(LauncherMode mode)
    {
        _model.ClearGhosts();
        _registry.Mode = mode;
    }

    // Open a plugin: flash its tile, confirm in the footer. Plugin code must never break the launcher.
    private void Open(LauncherEntry? e)
    {
        if (e == null) return;
        _flashEntry = e;
        _flashUntil = Now() + FlashSeconds;
        _feedback.Show(F("launcher.feedback.opened", LauncherMenuModel.NameOf(e)));
        try { e.OnOpen(); } catch { /* never break the launcher */ }
    }

    private void ToggleFavorite(LauncherEntry? e)
    {
        if (e == null) return;
        var name = LauncherMenuModel.NameOf(e);
        _feedback.Show(_model.ToggleFavorite(e)
            ? F("launcher.feedback.fav_added", name, _model.FavoriteCount)
            : F("launcher.feedback.fav_removed", name));
    }

    /// <summary>Text in the regular-weight TMP face at a custom size (plain window text is a fixed 14 px; RichEmoji
    /// switches to the TMP face, which honours FontSize — SDL §6).</summary>
    private static TextElement Small(Func<string> text, Func<ColorRgba?> color, int size,
        TextAlign align = TextAlign.Left, int maxLines = 0)
        => new(text, color, Align: align, FontSize: size, NoWrap: maxLines == 0)
        { RichEmoji = true, MaxLines = maxLines };

    /// <summary>A flat white glyph PNG drawn in a live colour (<see cref="ImageElement.Tint"/>), optionally live.</summary>
    private static ImageElement Glyph(Func<byte[]?> png, int size, Func<ColorRgba> tint, bool live = false)
        => new(png, size, size) { Live = live, Tint = () => tint() };

    /// <summary><paramref name="c"/> with its alpha multiplied by <paramref name="alpha"/> (the ghost fade).</summary>
    private static ColorRgba Faded(ColorRgba c, float alpha) => c with { A = c.A * alpha };
}
