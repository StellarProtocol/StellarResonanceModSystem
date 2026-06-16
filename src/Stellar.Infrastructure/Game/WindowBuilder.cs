using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Pure uGUI builder for an interactive window element tree — no IL2CPP / game deps, so it runs headlessly
/// in the UI sandbox as well as in-game. Builds the GlassMenu frosted chrome (sibling partial
/// <c>.Chrome.cs</c>) + the widgets (<c>.Widgets.cs</c>) from a <see cref="WindowRegistration"/> onto a
/// caller-supplied parent, captures dynamic leaves as bindings (re-applied by <see cref="WindowToken.Apply"/>),
/// and reuses <see cref="UGuiPrimitives"/> for layout/text plumbing (shared with HudElementBuilder).
/// Sibling to HudElementBuilder; the HUD path is untouched. Spacing follows the spec Measurement contract.
/// </summary>
internal sealed partial class WindowBuilder
{
    private readonly WindowThemeAssets _assets;
    // Per-frame field-tick hook (cursor/Esc/debounce). Injected by the renderer (which owns a
    // MonoBehaviour ticker); null in the sandbox (static render needs no tick). Mirrors HudElementBuilder's
    // registerBar animator hook.
    private readonly Action<UGuiTextInput>? _registerField;
    // ColorPicker SV/hue drag hook: (area rect, pick(nx,ny)) → the renderer's interaction ticker polls it.
    // Null in the sandbox (static render).
    private readonly Action<RectTransform, Action<float, float>>? _registerDrag;
    // Window drag-to-move hook: (handle, window root, editOnly) → ticker moves the root on drag. editOnly=true
    // (overlay/status chromes) restricts the drag to layout edit-mode so they don't move during play.
    private readonly Action<RectTransform, RectTransform, bool>? _registerWindowDrag;
    // Tile hover hook: (cell rect, setHover(bool)) → ticker polls the pointer against the cell + toggles the
    // hover visual (icon grow + icon/label brighten). Null in the sandbox → tiles render in their rest state.
    private readonly Action<RectTransform, Action<bool>>? _registerHover;
    // Per-frame pulse hook: the ticker calls the registered Action with a 0..1 pulse value (brand-logo glow).
    // Null in the sandbox → the logo renders static at the rest pulse.
    private readonly Action<Action<float>>? _registerPulse;

    // Window resize hook: (grip, window root, min size, max size) → the ticker resizes the root on grip drag.
    // A settable property (not a ctor param) to stay under the ctor-dependency cap; set by WindowRenderer.
    // Null in the sandbox → the grip renders but doesn't resize.
    internal Action<RectTransform, RectTransform, Vector2, Vector2>? RegisterResize { get; set; }

    // Drag-to-rearrange hooks (CombatMeter raid grid). RegisterDragSlot: (cell rect, key, canDrag, setHover) →
    // the ticker drives the drag (ghost + hover highlight + drop). SetDragSlotDrop: (fromKey,toKey)→ wired once
    // per grid build. Settable properties (not ctor params) to stay under the ctor-dependency cap; set by
    // WindowRenderer. Null in the sandbox → cells render statically with no drag.
    internal Action<RectTransform, int, Func<bool>, Action<bool>>? RegisterDragSlot { get; set; }
    internal Action<Action<int, int>>? SetDragSlotDrop { get; set; }

    // Row right-click hook: (cell rect, callback) → the ticker fires the callback on right-button-down over the
    // cell. Used by the CombatMeter row context menu. Null in the sandbox → right-click is inert.
    internal Action<RectTransform, Action>? RegisterRightClick { get; set; }

    // Render-texture host hook: (RawImage, boxed-texture supplier, drag cb, scroll cb, pan cb) → the ticker binds
    // the texture each frame and routes drag (orbit), scroll (zoom) and shift+drag (pan) over the box to the
    // callbacks. Used by the inspector 3D portrait. Null in the sandbox → the box stays blank.
    internal Action<RawImage, Func<object?>, Action<float, float>?, Action<float>?, Action<float, float>?, Action<int, int>?>? RegisterRenderHost { get; set; }

    // Game-asset icon hook: (RawImage, boxed-texture supplier, optional UV supplier, box W, box H) → the
    // ticker binds the texture (and atlas sub-rect) each frame, showing the image only once the async load
    // lands, and letterboxes the centred RawImage to the texel aspect within the box. Used by
    // GameTextureElement (profession crests / imagine icons). Null in the sandbox → the box stays blank.
    internal Action<RawImage, Func<object?>, Func<UvRect>?, float, float>? RegisterGameTexture { get; set; }

    // Scrollbar drag-exclusion hook: the interaction ticker skips window-drag starts over these rects so
    // the uGUI Scrollbar receives the press (whole-frame-draggable Party windows hijacked it otherwise).
    internal Action<RectTransform>? RegisterScrollbar { get; set; }

    // Line-chart pan/zoom hook: (plot rect, getWindow, setWindow, total seconds, min span) → the ticker
    // zooms the visible window on scroll-over-plot (around the cursor's time) and pans it on left-drag, all
    // clamped via ChartWindow. Null in the sandbox → the plot renders statically (gestures verified in-game).
    internal Action<RectTransform, Func<(float, float)>, Action<(float, float)>, Func<float>, Func<float>>? RegisterChartPan { get; set; }

    public WindowBuilder(WindowThemeAssets assets,
        Action<UGuiTextInput>? registerField = null,
        Action<RectTransform, Action<float, float>>? registerDrag = null,
        Action<RectTransform, RectTransform, bool>? registerWindowDrag = null,
        Action<RectTransform, Action<bool>>? registerHover = null,
        Action<Action<float>>? registerPulse = null)
    {
        _assets = assets;
        _registerField = registerField;
        _registerDrag = registerDrag;
        _registerWindowDrag = registerWindowDrag;
        _registerHover = registerHover;
        _registerPulse = registerPulse;
    }

    /// <summary>Builds <paramref name="reg"/> under <paramref name="parent"/> and returns the token.</summary>
    public WindowToken Build(WindowRegistration reg, Transform parent)
    {
        var token = new WindowToken();
        token.Resizable = reg.Spec.Resizable;
        var (root, content) = BuildChrome(reg, parent, token);
        token.Root = root.gameObject;
        token.Rect = root;
        BuildElement(reg.Root, content, token);
        token.Apply();   // first paint
        return token;
    }

    // ---- token + bindings (captured at build; Apply re-pulls dynamic leaves) ----

    public sealed class WindowToken
    {
        public GameObject Root = null!;
        public RectTransform Rect = null!;
        public bool Resizable;   // window's root is fixed-size (grip-resizable) rather than content-height-fit
        private bool _laidOut;   // first structural layout done? (mount = immediate; later per-tick = deferred)
        internal readonly List<TextBinding> Texts = new();
        internal readonly List<ButtonBinding> Buttons = new();
        internal readonly List<ToggleBinding> Toggles = new();
        internal readonly List<SliderBinding> Sliders = new();
        internal readonly List<UGuiTextInput> Fields = new();
        internal readonly List<FieldBinding> FieldSyncs = new();   // re-sync field text from Get() on external change
        internal readonly List<CondBinding> Conds = new();
        internal readonly List<ListBinding> Lists = new();
        internal readonly List<VirtualListBinding> VirtualLists = new();
        internal readonly List<SpriteBinding> Sprites = new();   // dynamic atlas sub-rect (SpriteElement.UvFunc)
        internal readonly List<SwatchBinding> Swatches = new();
        internal readonly List<BarBinding> Bars = new();
        internal readonly List<ColorPickerBinding> Pickers = new();
        internal readonly List<FrameOpacityBinding> FrameOpacities = new();
        internal readonly List<HoverBinding> Hovers = new();           // per-apply pin-state poll (visual hover is ticker-driven)
        internal readonly List<SelectableBinding> Selectables = new(); // per-apply selected-state re-tint for SelectableElement rows
        internal readonly List<MeterRowBinding> MeterRows = new();      // per-apply poll for bespoke CombatMeter rows
        internal readonly List<AccentRowBinding> AccentRows = new();    // per-apply poll for role-stripe/share-backdrop rows
        internal readonly List<CooldownTileBinding> CooldownTiles = new(); // per-apply poll for CooldownBar tiles (icon+fill+seconds+★)
        internal readonly List<ChartBinding> Charts = new();            // per-apply poll: re-mesh LineChart only on series/range change
        internal readonly List<Texture2D> IconTextures = new();        // PNG icons (HideAndDontSave) — reclaimed on destroy
        // Atlas dedup: SpriteElement cells that share one atlas byte[] reuse a single uploaded texture (keyed by
        // array reference). The texture is owned by IconTextures (added once on first load), so disposal stays
        // single — this map only prevents the duplicate decode+upload, it does NOT own the texture.
        internal readonly Dictionary<byte[], Texture2D> AtlasCache = new();
        internal readonly List<Action<float>> Pulses = new();          // per-frame brand-logo glow pulse (ticker-driven)
        // Re-skin closures captured at build: each re-applies a themed sprite/colour/size from the (rebaked)
        // assets to its existing graphic. Run on a theme change instead of destroying+rebuilding the canvas
        // (which flickered) — uGUI is retained-mode, so in-place re-application is what avoids the 1-frame gap.
        internal readonly List<Action> ReskinActions = new();

        /// <summary>Re-apply the rebaked theme (sprites/colours/sizes) in place, then re-pull values. No GO
        /// destruction → flicker-free live theme switch.</summary>
        public void Reskin()
        {
            for (var i = 0; i < ReskinActions.Count; i++)
            {
                try { ReskinActions[i](); } catch { /* skip a bad leaf; never break the whole re-skin */ }
            }
            Apply();
            // Text sizes (Font Scale) change preferred sizes, but a re-skin doesn't auto-rebuild the layout —
            // rows could collapse (the "all sliders vanish after a font-scale drag; a tab switch brings them
            // back" bug — the tab switch was forcing this rebuild). Force it now.
            if (Rect != null) try { LayoutRebuilder.ForceRebuildLayoutImmediate(Rect); } catch { }
        }

        public void Apply()
        {
            // VirtualLists FIRST: each sets its plugin's window-offset via OnWindow(first) and repositions/
            // activates its pooled rows. Must precede Conds + Texts so slot Funcs (which index snapshot[first+i])
            // and the polymorphic header/picker Conditionals inside each slot read the fresh offset this poll.
            var structuralChange = false;
            for (var i = 0; i < VirtualLists.Count; i++) structuralChange |= VirtualLists[i].Apply();
            // Visibility (Conds/Lists) next so SetActive is settled before value pulls — matches HudElementBuilder.
            // A SetActive that changes which branch/rows are shown also changes the content's preferred size, but
            // uGUI does NOT auto-rebuild a ContentSizeFitter-sized window on a descendant SetActive — so a
            // content-sized window (AutoSizeWidth launcher: Full↔Minimal↔horizontal) would keep its old size and
            // clip/overflow. Force one rebuild when any visibility changed (mirrors Reskin()).
            for (var i = 0; i < Conds.Count; i++) structuralChange |= Conds[i].Apply();
            for (var i = 0; i < Lists.Count; i++) structuralChange |= Lists[i].Apply();
            if (structuralChange && Rect != null)
            {
                // The FIRST structural layout (mount) is forced immediate so the window opens correctly sized.
                // Every later per-tick structural change (a Conditional flip / List-count change) only MARKS the
                // layout dirty — Unity coalesces it into its batched canvas rebuild on the next render. This turns
                // the synchronous whole-window ForceRebuild (the ~6ms ChatTools apply spike) into a deferred pass
                // off the Stellar tick. Nothing reads the window size synchronously after Apply (verified), so the
                // one-render-frame settle delay is imperceptible at the 10 Hz apply rate.
                try
                {
                    if (_laidOut) LayoutRebuilder.MarkLayoutForRebuild(Rect);
                    else { LayoutRebuilder.ForceRebuildLayoutImmediate(Rect); _laidOut = true; }
                }
                catch { }
            }
            for (var i = 0; i < Texts.Count; i++) Texts[i].Apply();
            for (var i = 0; i < Sprites.Count; i++) Sprites[i].Apply();
            for (var i = 0; i < Buttons.Count; i++) Buttons[i].Apply();
            for (var i = 0; i < Toggles.Count; i++) Toggles[i].Apply();
            for (var i = 0; i < Sliders.Count; i++) Sliders[i].Apply();
            for (var i = 0; i < Swatches.Count; i++) Swatches[i].Apply();
            for (var i = 0; i < Bars.Count; i++) Bars[i].Apply();
            for (var i = 0; i < Pickers.Count; i++) Pickers[i].Apply();
            for (var i = 0; i < FrameOpacities.Count; i++) FrameOpacities[i].Apply();
            for (var i = 0; i < Hovers.Count; i++) Hovers[i].Poll?.Invoke();
            for (var i = 0; i < Selectables.Count; i++) Selectables[i].Apply();
            for (var i = 0; i < MeterRows.Count; i++) MeterRows[i].Apply();
            for (var i = 0; i < AccentRows.Count; i++) AccentRows[i].Apply();
            for (var i = 0; i < CooldownTiles.Count; i++) CooldownTiles[i].Apply();
            for (var i = 0; i < Charts.Count; i++) Charts[i].Apply();
            for (var i = 0; i < FieldSyncs.Count; i++) FieldSyncs[i].Apply();
        }

        /// <summary>Re-issue glyph UVs for every Text in the window after the dynamic font's atlas rebuilt.
        /// A shared OS dynamic font (WindowThemeAssets.MenuFont) repacks its atlas when a text-heavy panel
        /// requests many new glyphs; Text built earlier (incl. hidden tabs) keeps stale UVs → garbled glyphs
        /// until refreshed. uGUI auto-refreshes ENABLED tracked text, but hidden-tab text isn't — so we force
        /// ALL of them (GetComponentsInChildren(true) catches title/buttons/labels not in the binding lists).</summary>
        public void RefreshFontTexture()
        {
            if (Root == null) return;
            var texts = Root.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++) { try { texts[i].FontTextureChanged(); } catch { } }
        }

        /// <summary>Destroy native textures the GameObject teardown won't reclaim (the ColorPicker SV/hue
        /// bakes use HideFlags.HideAndDontSave). Call before destroying Root.</summary>
        public void DisposeNativeTextures()
        {
            for (var i = 0; i < Pickers.Count; i++) Pickers[i].Destroy();
            for (var i = 0; i < IconTextures.Count; i++) if (IconTextures[i] != null) UnityEngine.Object.Destroy(IconTextures[i]);
        }

        /// <summary>True while any of this window's text fields holds keyboard focus (drives the
        /// keyboard gate). Cheap; called per tick by WindowService via the renderer.</summary>
        public bool AnyFieldFocused
        {
            get { for (var i = 0; i < Fields.Count; i++) if (Fields[i].IsFocused) return true; return false; }
        }
    }

    // Binding inner-classes (Slider/Text/Button/Toggle/Swatch/Bar/FrameOpacity/Cond/List/Hover/Selectable)
    // live in the sibling partial WindowBuilder.Bindings.cs (split out for the file-size gate).

    // ---- recursive builder ----

    private void BuildElement(HudElement el, Transform parent, WindowToken token)
    {
        switch (el)
        {
            case RowElement r:    BuildLayout(r.Children, parent, token, r.Gap == 0f ? RowGap : r.Gap, UGuiPrimitives.RowMode); break;
            case ColumnElement c: BuildLayout(c.Children, parent, token, c.Gap == 0f ? SectionGap : c.Gap, UGuiPrimitives.ColumnMode); break;
            case TextElement t:   BuildText(t, parent, token); break;
            case SeparatorElement sep: BuildSeparator(parent, token, sep.Vertical); break;
            case SpacerElement sp: BuildSpacer(parent, sp.Width, sp.Height); break;
            case ButtonElement b:  BuildButton(b, parent, token); break;       // .Widgets.cs
            case ToggleElement tg: BuildToggle(tg, parent, token); break;      // .Widgets.cs
            case SliderElement sl: BuildSlider(sl, parent, token); break;      // .Widgets.cs
            case InputElement inp: BuildInput(inp, parent, token); break;      // .Widgets.cs
            case ScrollElement sc: BuildScroll(sc, parent, token); break;      // .Widgets.cs
            case ColorPickerElement cp: BuildColorPicker(cp, parent, token); break; // .ColorPicker.cs
            case SwatchElement sw: BuildSwatch(sw, parent, token); break;       // .Preview.cs
            case PillElement p:    BuildPill(p, parent, token); break;          // .Preview.cs (Themes preview)
            case BarElement br:    BuildBar(br, parent, token); break;          // .Preview.cs (Themes preview)
            case ConditionalElement cond: BuildConditional(cond, parent, token); break;
            case ListElement list: BuildList(list, parent, token); break;
            case VirtualListElement vlist: BuildVirtualList(vlist, parent, token); break;
            case TileElement tile: BuildTile(tile, parent, token); break;       // .Tiles.cs
            case ImageElement img:  BuildImage(img, parent, token); break;       // .Tiles.cs
            case SpriteElement sp:  BuildSprite(sp, parent, token); break;       // .Tiles.cs
            case BrandLogoElement bl: BuildBrandLogo(bl, parent, token); break;  // .Tiles.cs
            case CellElement cell:  BuildCell(cell, parent, token); break;        // .Table.cs
            case SelectableElement sel: BuildSelectable(sel, parent, token); break; // .Table.cs
            case MeterRowElement mr: BuildMeterRow(mr, parent, token); break;      // .MeterRow.cs
            case AccentRowElement ar: BuildAccentRow(ar, parent, token); break;    // .MeterRow.cs
            case CooldownTileElement ct: BuildCooldownTile(ct, parent, token); break;  // .CooldownTile.cs
            case DragSlotElement ds: BuildDragSlot(ds, parent, token); break;      // .DragSlot.cs
            case RenderTextureHostElement rh: BuildRenderHost(rh, parent); break;  // .DragSlot.cs
            case GameTextureElement gt: BuildGameTexture(gt, parent); break;       // .DragSlot.cs
            case LineChartElement lc: BuildLineChart(lc, parent, token); break;     // .LineChart.cs
        }
    }

    // Measurement contract: body padding 12, section gap 12, row gap 8, separator margin 2.
    private const float RowGap = 8f;
    private const float SectionGap = 12f;

    private void BuildLayout(IReadOnlyList<HudElement> children, Transform parent, WindowToken token, float gap, int columns)
    {
        var go = UGuiPrimitives.NewChild(columns == UGuiPrimitives.RowMode ? "Row" : columns > 1 ? "Grid" : "Column", parent);
        UGuiPrimitives.AddLayout(go, gap, columns);
        // Columns expand their children to the full window width (so Text wraps + Rows fill + Spacer pushes
        // to the real right edge). Rows stay content-sized (buttons size to their label). HUD-side
        // HudElementBuilder keeps the shared AddLayout default — this override is window-only.
        if (columns == UGuiPrimitives.ColumnMode && go.GetComponent<VerticalLayoutGroup>() is { } vlg)
            vlg.childForceExpandWidth = true;
        foreach (var child in children) BuildElement(child, go.transform, token);
    }

    private void BuildText(TextElement t, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Text", parent);
        var txt = go.AddComponent<Text>();
        var anchor = t.Align switch { TextAlign.Center => TextAnchor.MiddleCenter, TextAlign.Right => TextAnchor.MiddleRight, _ => TextAnchor.MiddleLeft };
        UGuiPrimitives.ConfigureText(txt, Scaled(t.Emphasis ? 15 : 14), anchor, bold: t.Emphasis);
        // Centre on the glyph GEOMETRY, not the font line-box — the OS dynamic font sits the ink low under a
        // Middle anchor, so bare text rendered ~2-3px below button labels (which already optically-centre via
        // asymmetric padding in BuildButton). alignByGeometry makes a label vertically match the buttons in a
        // Row (e.g. the layout toolbar); harmless for stacked column text.
        txt.alignByGeometry = true;
        ApplyMenuFont(txt);
        // Window text WRAPS within its width (HUD default is Overflow for short transparent-overlay text;
        // window panels have long labels/descriptions that must wrap, not spill past the frame).
        txt.horizontalOverflow = HorizontalWrapMode.Wrap;
        txt.color = _assets.MenuText;
        // minWidth=0 lets a Row shrink the Text below its single-line preferred width (Wrap then engages
        // instead of overflowing the RectMask2D); flexibleWidth=0 so the Text does NOT grow to fill the row —
        // it stays natural-width and a sibling Spacer does the pushing (flexibleWidth=1 here made the label
        // grow and shoved right-aligned widgets like the Hotkeys binding cell past the frame edge). In a
        // Column the parent's childForceExpandWidth still gives the Text full width so long lines wrap.
        var le = go.AddComponent<LayoutElement>();
        if (t.Width > 0f) { le.preferredWidth = le.minWidth = t.Width; le.flexibleWidth = 0f; }   // fixed column cell
        else { le.flexibleWidth = 0f; le.minWidth = 0f; }
        // Readability outline for chrome-less overlays (UnityEngine.UI.Shadow is stripped from the game interop;
        // Outline survives). 4-direction dark halo so light text stays legible over any world background.
        if (t.Shadow)
        {
            var ol = go.AddComponent<UnityEngine.UI.Outline>();
            ol.effectColor = new Color(0f, 0f, 0f, 0.85f);
            ol.effectDistance = new Vector2(1.1f, -1.1f);
        }
        token.Texts.Add(new TextBinding { C = txt, TextFn = t.Text, ColorFn = t.Color });
        RegisterTextReskin(token, txt, t.Emphasis ? 15 : 14);
    }

    // Override ConfigureText's builtin-Arial attempt with the OS dynamic font (resolved consistently in
    // both the editor sandbox and the IL2CPP player) — see WindowThemeAssets.MenuFont. No-op when null.
    private void ApplyMenuFont(Text t) { if (_assets.MenuFont != null) t.font = _assets.MenuFont; }

    // Apply the active theme's Font Scale to a base window-text size (so the Font Scale slider affects uGUI
    // windows, which previously hard-coded sizes). Window-only — the shared HUD ConfigureText isn't touched.
    private int Scaled(int baseSize) => Mathf.Max(8, Mathf.RoundToInt(baseSize * _assets.FontScale));

    // Register a re-skin closure for a window Text: re-applies the live font size (Font Scale), font, and the
    // default colour on a theme change — in place, no rebuild. (A ColorFn-bound Text gets its colour re-pulled
    // by TextBinding.Apply right after, so muted/warn texts override this default.)
    private void RegisterTextReskin(WindowToken token, Text txt, int baseSize, bool muted = false)
        => token.ReskinActions.Add(() =>
        {
            if (txt == null) return;
            txt.fontSize = Scaled(baseSize);
            if (_assets.MenuFont != null) txt.font = _assets.MenuFont;
            txt.color = muted ? _assets.MenuMuted : _assets.MenuText;
        });

    // 1 px themed divider. Horizontal: a 5 px-tall full-width slot, line centred (between rows). Vertical: a
    // 5 px-wide fixed-height slot, line centred (between columns inside a Row — the launcher's chrome panes).
    private const float VSeparatorHeight = 52f;   // launcher horizontal panes are ~2 icon rows tall
    private void BuildSeparator(Transform parent, WindowToken token, bool vertical = false)
    {
        var go = UGuiPrimitives.NewChild("Separator", parent);
        var le = go.AddComponent<LayoutElement>();
        var line = UGuiPrimitives.NewChild("Line", go.transform);
        var lrt = line.GetComponent<RectTransform>();
        if (vertical)
        {
            le.minWidth = 5f; le.preferredWidth = 5f; le.minHeight = VSeparatorHeight; le.preferredHeight = VSeparatorHeight;
            lrt.anchorMin = new Vector2(0.5f, 0f); lrt.anchorMax = new Vector2(0.5f, 1f);
            lrt.sizeDelta = new Vector2(1f, 0f); lrt.anchoredPosition = Vector2.zero;
        }
        else
        {
            le.minHeight = 5f; le.preferredHeight = 5f; le.flexibleWidth = 1f;
            lrt.anchorMin = new Vector2(0f, 0.5f); lrt.anchorMax = new Vector2(1f, 0.5f);
            lrt.sizeDelta = new Vector2(0f, 1f); lrt.anchoredPosition = Vector2.zero;
        }
        var img = line.AddComponent<Image>(); img.color = _assets.MenuBorder; img.raycastTarget = false;
        token.ReskinActions.Add(() => { if (img != null) img.color = _assets.MenuBorder; });
    }

    // Row gap. Width 0 (and no Height) → flexible (FlexibleSpace analog); Width >0 → fixed width; Height >0 →
    // fixed-height vertical gap (a little margin between Column sections, no divider line).
    private void BuildSpacer(Transform parent, float width = 0f, float height = 0f)
    {
        var go = UGuiPrimitives.NewChild("Spacer", parent);
        var le = go.AddComponent<LayoutElement>();
        if (height > 0f) { le.preferredHeight = le.minHeight = height; le.flexibleHeight = 0f; }
        if (width > 0f) { le.preferredWidth = le.minWidth = width; le.flexibleWidth = 0f; }
        else if (height <= 0f) le.flexibleWidth = 1f;
    }

    // React-style conditional — both subtrees built once; CondBinding SetActive-toggles each refresh.
    // The Settings tab strip uses this (one Conditional per tab, When = isActiveTab).
    // Column containers must force their children to the FULL window width (like the window's own
    // ColumnElement), or the width-fill chain breaks: content nested under a tab Conditional / a List wouldn't
    // inherit the window width, so Rows/Scrolls inside (e.g. the Hotkeys binding cell) got the wrong geometry
    // and overflowed the frame. AddLayout defaults childForceExpandWidth=false; this re-enables it for columns.
    private static void ExpandColumnWidth(GameObject go)
    {
        if (go.GetComponent<VerticalLayoutGroup>() is { } vlg) vlg.childForceExpandWidth = true;
    }

    private void BuildConditional(ConditionalElement cond, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Cond", parent);
        UGuiPrimitives.AddLayout(go, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(go);
        // Fill: the active branch grows to fill leftover height in a fixed-size window (the Cond grabs the slack
        // via flexibleHeight; childForceExpandHeight stretches the branch + its content to fill it).
        if (cond.Fill)
        {
            var fle = go.AddComponent<LayoutElement>(); fle.flexibleHeight = 1f;
            if (go.GetComponent<VerticalLayoutGroup>() is { } gv) gv.childForceExpandHeight = true;
        }
        var thenGo = UGuiPrimitives.NewChild("Then", go.transform);
        UGuiPrimitives.AddLayout(thenGo, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(thenGo);
        if (cond.Fill && thenGo.GetComponent<VerticalLayoutGroup>() is { } tv) tv.childForceExpandHeight = true;
        BuildElement(cond.Then, thenGo.transform, token);
        GameObject? elseGo = null;
        if (cond.Else != null)
        {
            elseGo = UGuiPrimitives.NewChild("Else", go.transform);
            UGuiPrimitives.AddLayout(elseGo, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(elseGo);
            if (cond.Fill && elseGo.GetComponent<VerticalLayoutGroup>() is { } ev) ev.childForceExpandHeight = true;
            BuildElement(cond.Else, elseGo.transform, token);
        }
        // With NO else-branch, toggle the WHOLE Cond container (not just its inner Then). Otherwise the
        // container stays an active 0-height child and the parent column still adds section-spacing around it —
        // 5 collapsed tab-Conditionals = a big empty gap above the active tab's content. Collapsing the
        // container removes it from the layout entirely. (With an else-branch the container must stay active to
        // show one of the two arms, so we keep toggling Then/Else.)
        token.Conds.Add(new CondBinding { When = cond.When, Then = elseGo == null ? go : thenGo, Else = elseGo });
    }

    // Variable-length list bounded by Slots.Count; first VisibleCount() slots shown via SetActive.
    private void BuildList(ListElement list, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("List", parent);
        UGuiPrimitives.AddLayout(go, gap: 2f, columns: list.Columns); ExpandColumnWidth(go);
        // Multi-column cell-size override (else the GridLayoutGroup uses the framework default 120×34, too narrow
        // for an icon+label+value row — StatInspector mini-HUD).
        if (list.Columns > 1 && list.CellWidth > 0f && go.GetComponent<UnityEngine.UI.GridLayoutGroup>() is { } grid)
            grid.cellSize = new Vector2(list.CellWidth, list.CellHeight > 0f ? list.CellHeight : grid.cellSize.y);
        var slots = new GameObject[list.Slots.Count];
        for (var i = 0; i < list.Slots.Count; i++)
        {
            var slot = UGuiPrimitives.NewChild("Slot", go.transform);
            UGuiPrimitives.AddLayout(slot, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(slot);
            BuildElement(list.Slots[i], slot.transform, token);
            slots[i] = slot;
        }
        token.Lists.Add(new ListBinding { Count = list.VisibleCount, Slots = slots });
    }
}
