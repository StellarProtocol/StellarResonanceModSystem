using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.Style;

/// <summary>
/// The house per-setting help (SDL §4 "Round ? help" + "Help popover"; port of Maestro <c>Plugin.Help*.cs</c>): a round
/// 24 px "?" at the end of a settings row opens ONE shared floating popover anchored under it — GlassMenu, no title bar
/// (draggable by its frame), 360 wide; header = 32 px accent "?" tile + bold setting name + muted "SECTION · HELP" +
/// round ✕; body = wrapping text in one Tile box. Re-clicking the open "?" closes it. The window framework has no
/// hover tooltip, so this is the click-to-open replacement for the old grey description paragraphs.
/// <para>Usage: construct once per surface, <see cref="Register"/> with the window host, <see cref="Add"/> a key per
/// setting, place <see cref="Button"/> as the last child of each settings row (so the "?" column lines up, R17), and
/// call <see cref="Tick"/> every frame (re-asserts the anchored rect after a first open — see there).</para>
/// </summary>
internal sealed class StellarHelpPopover
{
    public const float PopoverWidth = 360f;
    public const float ButtonSize = 24f;
    private const float IconSize = 32f;
    private const string FallbackTitle = "Help";
    private const string HelpKey = "common.help";
    // The first SetVisible(true) MOUNTS the window and applies its DefaultRect AFTER our SetRect, so the popover would
    // land at the default spot on its first open; re-applying the anchored rect for a few frames fixes it
    // (WindowBuilder-Patterns.md, click-to-open tooltip). Later opens take the SetRect immediately.
    private const int RepositionFrames = 4;

    private readonly struct Entry
    {
        public readonly Func<string> Title, Body;
        public readonly Func<string>? Section;
        public Entry(Func<string> title, Func<string> body, Func<string>? section) { Title = title; Body = body; Section = section; }
    }

    private readonly StellarStyle _s;
    private readonly ILocalization _loc;
    private readonly IFramework _framework;
    private readonly string _windowId;
    private readonly Func<bool> _shouldRender;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StellarStyle.HoverFlag> _hover = new(StringComparer.Ordinal);
    private readonly StellarStyle.HoverFlag _closeHover = new();
    private IWindowControl? _window;
    private string _key = "";   // the entry the popover shows; "" = closed
    private WindowRect _rect;
    private int _repositionTicks;

    /// <summary><paramref name="shouldRender"/> = the owning surface's gate (e.g. the Settings hub's "not over the
    /// loading screen"). Construction reads no localization (safe during early wiring).</summary>
    public StellarHelpPopover(StellarStyle style, ILocalization loc, IFramework framework, string windowId,
        Func<bool> shouldRender)
    {
        _s = style; _loc = loc; _framework = framework; _windowId = windowId; _shouldRender = shouldRender;
    }

    /// <summary>Registers the (hidden) popover window. The title is never drawn (no title bar) but resolves lazily
    /// for the layout editor's lists; the DefaultRect is a constant (KB: never derive it from ScreenWidth).</summary>
    public IWindowControl Register(IWindowHost windows)
    {
        _window = windows.Register(new WindowRegistration(
            new WindowSpec(_windowId, FallbackTitle, new WindowRect(200f, 200f, PopoverWidth, 0f),
                WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            {
                ShouldRender = _shouldRender, Draggable = true, Closable = true, StartVisible = false,
                ShowTitleBar = false, TitleProvider = () => _loc.T(HelpKey),
            },
            BuildRoot(),
            OnClose: Close));
        return _window;
    }

    /// <summary>Adds (or replaces) the help for <paramref name="key"/>: the setting's name, the help text, and an
    /// optional section name for the "SECTION · HELP" subtitle. All live, so a language switch re-reads them.</summary>
    public void Add(string key, Func<string> title, Func<string> body, Func<string>? section = null)
        => _entries[key] = new Entry(title, body, section);

    public bool IsOpen(string key) => _key == key && _window?.IsShown == true;

    public void Close()
    {
        _key = "";
        _repositionTicks = 0;
        _window?.SetVisible(false);
    }

    /// <summary>Call every frame from the owner's tick: re-asserts the anchored rect for a few frames after an open.</summary>
    public void Tick()
    {
        if (_repositionTicks <= 0 || _window == null) return;
        _repositionTicks--;
        if (_window.IsShown) _window.SetRect(_rect);
    }

    /// <summary>The round "?" (exactly 24×24, R4): Neutral fill, text @10 % on hover, accent @35 % while its tip is
    /// open. Bare selectable = no padding / square wash of its own; OnClickWithRect anchors the popover under it.
    /// Content = [24 px strut, the "?"] in ONE Row (a stacked spacer grew it to 24×26); CenterGlyphs centres the drawn
    /// glyph in its box. Radius = RoundRadius(24) = 11.</summary>
    public HudElement Button(string key)
    {
        if (!_hover.TryGetValue(key, out var hover)) _hover[key] = hover = new StellarStyle.HoverFlag();
        return new CellElement(new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ButtonSize),
                new TextElement(() => "?", Color: () => _s.NeutralLabel(), Width: ButtonSize, Align: TextAlign.Center,
                    NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { })   // the real work is OnClickWithRect (it needs the button's rect); both fire
            {
                Bare = true,
                OnHover = over => hover.On = over,
                OnClickWithRect = r => Toggle(key, r),
            })
        {
            FillColorFn = () => _s.Fill(IsOpen(key) ? StellarStyle.Over(_s.Accent() with { A = 0.35f }, _s.Panel())
                                      : hover.On ? _s.NeutralHover() : _s.Neutral()),
            CornerRadius = StellarStyle.RoundRadius(ButtonSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = ButtonSize,
        }, Width: ButtonSize);
    }

    private void Toggle(string key, WindowRect screenRect)
    {
        if (_window == null) return;
        if (IsOpen(key)) { Close(); return; }
        _key = key;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the popover drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        float k = _framework.ScreenWidth > 0 && _framework.CanvasWidth > 1
            ? (float)_framework.CanvasWidth / _framework.ScreenWidth : 1f;
        var r = new WindowRect(screenRect.X * k, screenRect.Y * k, screenRect.Width * k, screenRect.Height * k);
        float canvasH = _framework.CanvasHeight > 1 ? _framework.CanvasHeight : _framework.ScreenHeight;
        // Below the "?", right-aligned to it, so it opens to the LEFT of the "?" column instead of covering the setting.
        float x = Math.Max(10f, r.X + r.Width - PopoverWidth);
        float y = Math.Min(r.Y + r.Height + 4f, canvasH - 40f);
        _rect = new WindowRect(x, y, PopoverWidth, 0f);
        _window.SetVisible(true);
        _window.SetRect(_rect);
        // Always front: switching between "?"s finds the popover already VISIBLE, and only a show FROM HIDDEN re-activates
        // a window automatically — the owner may have been fronted/clicked since, so front it explicitly (non-sticky
        // WindowStacking; CLAUDE.md Rule 5 already-visible-child exception).
        _window.BringToFront();
        _window.MarkDirty();   // re-pull title/body for the new key this frame
        _repositionTicks = RepositionFrames;
    }

    private string Title() => _entries.TryGetValue(_key, out var e) ? e.Title() : "";
    private string Body()  => _entries.TryGetValue(_key, out var e) ? e.Body() : "";

    private string Subtitle()
    {
        var help = _loc.T(HelpKey);
        var section = _entries.TryGetValue(_key, out var e) && e.Section != null ? e.Section() : "";
        return (section.Length > 0 ? section + " · " + help : help).ToUpperInvariant();
    }

    private HudElement BuildRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            QuestionTile(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(Title, Color: () => _s.Text(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(Subtitle, Color: () => _s.Muted(), NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            CloseButton(),
        }, Gap: 10f),
        // The help text: one raised tile, wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(Body, Color: () => _s.Text()))
        {
            FillColorFn = () => _s.Fill(_s.Tile()), CornerRadius = StellarStyle.TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    // 32 px accent tile with a white bold "?" (the icon-tile look drawn with text instead of a PNG); radius 7 keeps the
    // sliced tile's min size under the side (RoundRadius note).
    private HudElement QuestionTile() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, IconSize),
            new TextElement(() => "?", Color: () => StellarStyle.White, Width: IconSize, Align: TextAlign.Center,
                NoWrap: true, FontSize: 18) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => _s.Fill(_s.Accent()), CornerRadius = MathF.Round(IconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
        MaxWidth = IconSize,
    }, Width: IconSize);

    // Round 24 px ✕: muted glyph at rest, text colour on a text @10 % circle on hover (closing help is not destructive,
    // so no red). Plain-text glyph: bold ✕ is untrusted (SDL §6).
    private HudElement CloseButton() => new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, ButtonSize),
            new TextElement(() => "✕", Color: () => _closeHover.On ? _s.Text() : _s.Muted(), Width: ButtonSize,
                Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: Close) { Bare = true, OnHover = over => _closeHover.On = over })
    {
        FillColorFn = () => _closeHover.On ? _s.Fill(_s.Hover(_s.Background())) : StellarStyle.Transparent,
        CornerRadius = StellarStyle.RoundRadius(ButtonSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = ButtonSize,
    }, Width: ButtonSize);
}
