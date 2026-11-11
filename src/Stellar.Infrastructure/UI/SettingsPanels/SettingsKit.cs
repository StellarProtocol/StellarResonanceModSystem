using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Everything a Settings hub tab needs to draw itself in the house style (Stellar Design Language) and to confirm its
/// actions: the live-theme kit, the shared "?" help popover, the footer feedback line, the row flash, and the hub's
/// tab switch. One instance per hub, handed to every panel's <c>Describe(kit)</c>.
/// <para>Construction and every builder here read NO localization: the hub is built during wiring (before
/// <c>Game.Init</c>), where a first client-language read can latch the wrong language. Every string is a
/// <c>Func</c> resolved only when the window draws it.</para>
/// </summary>
internal sealed class SettingsKit
{
    /// <summary>Height of a settings row (the "?" is 24, a pill 28): every row is pinned to it so a row with a
    /// toggle lines up with one holding a pill (R4/R17).</summary>
    public const float RowHeight = 28f;

    public SettingsKit(StellarStyle style, StellarHelpPopover help, StellarFeedbackLine feedback, StellarRowFlash flash,
        ILocalization loc)
    {
        S = style; HelpPopover = help; Feedback = feedback; Flash = flash; Loc = loc;
    }

    public StellarStyle S { get; }
    public StellarHelpPopover HelpPopover { get; }
    public StellarFeedbackLine Feedback { get; }
    public StellarRowFlash Flash { get; }
    public ILocalization Loc { get; }

    /// <summary>Hint text that names a framework hotkey by its LIVE binding (set by the wiring as an init property,
    /// keeping the constructor under the dependency cap).</summary>
    public HotkeyHintText Keys { get; init; } = null!;

    /// <summary>Switches the hub to a tab by key ("plugins", "hotkeys", …). Set by the hub.</summary>
    public Action<string> GoToTab { get; set; } = _ => { };

    /// <summary>Raised when the hub switches tab: panels drop transient state (open confirms, rename fields, a key
    /// capture) like the prototype's tab handler.</summary>
    public event Action? TabChanged;

    internal void RaiseTabChanged() => TabChanged?.Invoke();

    /// <summary>A lazily-resolved catalog string.</summary>
    public Func<string> T(string key) => () => Loc.T(key);

    /// <summary>Shows "✓ message" in the footer for 3 s.</summary>
    public void Done(string message) => Feedback.Show(message);

    /// <summary>Registers help <paramref name="key"/> (title + body catalog keys, the tab name as the "SECTION · HELP"
    /// subtitle) and returns its round "?" (24×24). Re-registering a key only replaces the texts.</summary>
    public HudElement Help(string key, string titleKey, string bodyKey, string sectionKey)
    {
        HelpPopover.Add(key, T(titleKey), T(bodyKey), T(sectionKey));
        return HelpPopover.Button(key);
    }

    /// <summary>As <see cref="Help(string,string,string,string)"/> with live title / body texts (ones that name a
    /// hotkey through <see cref="Keys"/>).</summary>
    public HudElement Help(string key, Func<string> title, Func<string> body, string sectionKey)
    {
        HelpPopover.Add(key, title, body, T(sectionKey));
        return HelpPopover.Button(key);
    }

    /// <summary>A 24×24 gap where a row has no "?", so every "?" column lines up (R17).</summary>
    public static HudElement HelpGap() => new SpacerElement(StellarHelpPopover.ButtonSize, StellarHelpPopover.ButtonSize);

    /// <summary>A body-text label that takes the row's spare width and wraps (plain text, fixed 14 px).</summary>
    public HudElement Label(Func<string> text, Func<ColorRgba?>? color = null)
        => new CellElement(new TextElement(text, Color: color ?? (() => S.Text())), Weight: 1f);

    /// <summary>A one-line label that ellipsizes instead of wrapping (list rows).</summary>
    public HudElement LineLabel(Func<string> text, Func<ColorRgba?>? color = null)
        => new CellElement(new TextElement(text, Color: color ?? (() => S.Text()), NoWrap: true) { Ellipsize = true },
            Weight: 1f);

    /// <summary>A settings row pinned to <see cref="RowHeight"/>: [items…] with Gap 8. Put a <see cref="Label"/> first
    /// and the "?" (or <see cref="HelpGap"/>) last.</summary>
    public static HudElement Row(params HudElement[] items) => new RowElement(new HudElement[]
    {
        // The height strut sits OUTSIDE the gapped row: as a sibling it would add an 8 px gap before the first item.
        new SpacerElement(0.01f, RowHeight),
        new CellElement(new RowElement(items, Gap: 8f), Weight: 1f),
    }, Gap: 0.01f);

    /// <summary>A house SectionPanel with a catalog caption and an optional live hint.</summary>
    public HudElement Panel(string captionKey, Func<string>? hint, params HudElement[] children)
        => S.SectionPanel(T(captionKey), hint, children);

    /// <summary>A muted description paragraph (wraps).</summary>
    public HudElement Description(string key) => S.MutedLine(T(key));

    /// <summary>The existing on/off switch (capsule, 30×15) — the house kit keeps <c>ToggleElement</c> as is.</summary>
    public static HudElement Toggle(Func<bool> get, Action<bool> set, Func<bool>? enabled = null)
        => new ToggleElement(() => "", get, set, enabled);

    /// <summary>A round-knob slider that fills its row cell.</summary>
    public static HudElement Slider(Func<float> get, Action<float> set, float min, float max, Func<bool>? enabled = null)
        => new SliderElement(get, set, min, max, enabled) { SquareHandle = true, HandleSize = 14f };

    /// <summary>A muted value read-out of fixed width, right-aligned (slider values, versions).</summary>
    public HudElement Value(Func<string> text, float width)
        => new TextElement(text, Color: () => S.Muted(), Width: width, Align: TextAlign.Right, NoWrap: true);

    /// <summary>The house Pill dropdown (28 px, round), in the same Neutral fill / hover / label as the other settings
    /// pills (the Hotkeys key pill): the built-in Pill trigger colours read off-tone next to them (user, 2026-11-09).
    /// The option list keeps the built-in Pill panel.</summary>
    public HudElement Dropdown(Func<int> selected, Func<System.Collections.Generic.IReadOnlyList<string>> options,
        Action<int> onSelect, float width)
        => new DropdownElement(selected, options, onSelect, Width: width)
        {
            Style = DropdownStyle.Pill, CornerRadius = 13f,
            FillColorFn = () => S.Fill(S.Neutral()),
            HoverFillColorFn = () => S.Fill(S.Hover(S.Neutral())),
            LabelColorFn = () => S.NeutralLabel(),
        };

    /// <summary>Rich-text colour helpers for status lines (readable on the window background).</summary>
    public string Good(string text) => StellarStyle.Colored(text, S.Readable(S.Success(), S.Background()));
    public string Bad(string text) => StellarStyle.Colored(text, S.Readable(S.Danger(), S.Background()));
    public string Strong(string text) => StellarStyle.Colored(text, S.Text());
}
